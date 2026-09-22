using FileBackupTool.Api.Configuration;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Services;

/// <summary>
/// 可选的定时淘汰（设计文档 06 §8 / 04 §2.3）。**默认关闭**。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认关闭是产品决策，不是保守</b>：自动删除用户的备份历史是不可逆的惊喜。
/// 一个"能还原到三个月前"的承诺，不该被一个默认勾选的选项悄悄破坏。
/// 所以这里只认数据库里那个**显式**的开关（<c>Retention.AutoEvictionEnabled</c>），
/// 不做任何"看起来该开就开"的推断。
/// </para>
/// <para>
/// <b>两条不可越过的防线</b>：
/// <list type="number">
/// <item><b>永远保留最新的链组</b>。哪怕用户把上限设成 0 个 / 0 天，
/// 淘汰也绝不会删掉最后一条链——"备份全没了"比"超出配额"严重得多；</item>
/// <item><b>只淘汰完整的链</b>（当 <c>ApplyToFullChains</c> 为真时）：
/// 链里有中止/未结束的快照时整条跳过。那条链可能正是用户还要去看"为什么中止了"的对象，
/// 而它的增量也不足以独立存在。</item>
/// </list>
/// </para>
/// <para>
/// <b>淘汰是软删除</b>：<c>ISnapshotService.DeleteAsync</c> 只标记记录，
/// blob 要等无任何记录引用后才由回收站接管（02 §5）。所以这里不会立即释放空间，
/// 界面上"回收站占用"会随之增长——这是正确行为，不该被当成 bug。
/// </para>
/// </remarks>
public sealed class RetentionService(
    TaskStore tasks,
    SnapshotChainService chains,
    ISnapshotService snapshots,
    IBackupLogSink logSink,
    SettingsService settings,
    AuditLogger audit,
    IClock clock,
    IOptions<RetentionTickOptions> retentionTickOptions,
    ILogger<RetentionService> logger) : BackgroundService
{
    private const long BytesPerGb = 1024L * 1024 * 1024;

    private readonly TaskStore _tasks = tasks;
    private readonly SnapshotChainService _chains = chains;
    private readonly ISnapshotService _snapshots = snapshots;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly SettingsService _settings = settings;
    private readonly AuditLogger _audit = audit;
    private readonly IClock _clock = clock;
    private readonly RetentionTickOptions _options = retentionTickOptions.Value;
    private readonly ILogger<RetentionService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var period = TimeSpan.FromMinutes(Math.Max(5, _options.TickMinutes));
        _logger.LogInformation("保留策略服务已启动，节拍 {Minutes} 分钟（默认关闭，只有显式开启才会淘汰）。",
            period.TotalMinutes);

        using var timer = new PeriodicTimer(period);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "保留策略本轮执行失败，将在下一个节拍重试。");
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        RetentionSettingsDto retention = await _settings.GetRetentionAsync(ct).ConfigureAwait(false);
        if (!retention.Enabled)
        {
            return;
        }

        List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, ct).ConfigureAwait(false);
        DateTime now = _clock.UtcNow;

        foreach (BackupTask task in all)
        {
            List<BackupSnapshot> visible = await _chains.GetVisibleAsync(task.Id, ct).ConfigureAwait(false);
            List<List<BackupSnapshot>> chainGroups = GroupIntoChains(visible);

            if (chainGroups.Count <= 1)
            {
                // 只有一条链（或没有）——没有任何可淘汰的东西。
                continue;
            }

            List<List<BackupSnapshot>> victims = SelectVictims(chainGroups, retention, now);

            foreach (List<BackupSnapshot> chain in victims)
            {
                BackupSnapshot full = chain[0];
                string detail =
                    $"保留策略自动淘汰（模式 {retention.Mode}={retention.Value}）：" +
                    $"删除全量快照 {full.Id} 及其 {chain.Count - 1} 个增量快照，" +
                    $"时间范围 {chain[0].BeginTime:u} ~ {chain[^1].BeginTime:u}。";

                try
                {
                    await _snapshots.DeleteAsync(full.Id, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "保留策略删除快照 {SnapshotId} 失败。", full.Id);
                    continue;
                }

                _logger.LogInformation("保留策略：任务「{Task}」的链 {SnapshotId} 已淘汰（{Count} 个快照）。",
                    task.Name, full.Id, chain.Count);

                await _logSink.WriteAsync(task.Id, full.Id, LogLevel.Warning,
                    "保留策略自动淘汰了 1 个快照链组（可通过「设置 · 保留策略」关闭）。",
                    detail, ct).ConfigureAwait(false);

                await _audit.WriteSystemAsync(
                    AuditEventType.SnapshotDeleted,
                    target: $"{task.Name}/{full.Id}",
                    detail: detail,
                    ct: ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>把快照序列切成链组：每个全量快照开启一条新链，其后的增量归入该链。</summary>
    /// <remarks>
    /// 第一个全量之前的增量（若有）会被丢弃——它们不属于任何链，无法确定级联边界，
    /// 而"不确定就删"是不可接受的。它们仍然可见、可手动删除。
    /// </remarks>
    private static List<List<BackupSnapshot>> GroupIntoChains(List<BackupSnapshot> visibleAscending)
    {
        var groups = new List<List<BackupSnapshot>>();

        foreach (BackupSnapshot snapshot in visibleAscending)
        {
            if (snapshot.IsFull || groups.Count == 0)
            {
                if (snapshot.IsFull)
                {
                    groups.Add([snapshot]);
                    continue;
                }

                // 第一个全量之前：不归属任何链，跳过。
                continue;
            }

            groups[^1].Add(snapshot);
        }

        return groups;
    }

    /// <summary>
    /// 按保留策略挑出要淘汰的链组（**按从旧到新**）。<paramref name="groupsAscending"/> 是升序的。
    /// </summary>
    private List<List<BackupSnapshot>> SelectVictims(
        List<List<BackupSnapshot>> groupsAscending,
        RetentionSettingsDto retention,
        DateTime nowUtc)
    {
        // 新的在最后。keepFrom = 从这个下标开始（含）的链都要保留；
        // 它的下界被钉死在 count - 1，也就是"最少保留最新那条链"——见类注释的防线 ①。
        int count = groupsAscending.Count;
        int keepFrom = count - 1;

        switch (SettingsService.NormalizeMode(retention.Mode))
        {
            case "Days":
                DateTime cutoff = nowUtc.AddDays(-retention.Value);
                keepFrom = count - 1;

                while (keepFrom > 0 && groupsAscending[keepFrom - 1][^1].BeginTime >= cutoff)
                {
                    keepFrom--;
                }

                break;

            case "Size":
                long limit = retention.Value * BytesPerGb;

                if (limit > 0)
                {
                    // 从最新往旧累加；"加上这条就超限"的那条起，全部淘汰。
                    long accumulated = 0;
                    keepFrom = count - 1;

                    for (int i = count - 1; i >= 0; i--)
                    {
                        accumulated += ChainBytes(groupsAscending[i]);
                        if (accumulated > limit)
                        {
                            keepFrom = i + 1;
                            break;
                        }

                        keepFrom = 0;
                    }
                }

                break;

            default:
                // 条目数模式：保留最新的 N 条链。
                long keepGroups = Math.Max(1, retention.Value);
                keepFrom = (int)Math.Max(0, count - keepGroups);
                break;
        }

        // 兜底：任何模式下都不允许"一条都不留"。上面的分支都已保证这一点，
        // 这里再钉一次是因为它是不可逆操作的唯一保险，值得写死在同一个地方。
        keepFrom = Math.Clamp(keepFrom, 0, count - 1);

        var victims = new List<List<BackupSnapshot>>();

        for (int i = 0; i < keepFrom; i++)
        {
            List<BackupSnapshot> chain = groupsAscending[i];

            // 防线 ②：链里有中止 / 未结束的快照时整条跳过。
            if (retention.ApplyToFullChains &&
                chain.Any(s => s.Status != SnapshotStatus.Completed || s.EndTime is null))
            {
                _logger.LogInformation(
                    "保留策略：链 {SnapshotId} 含未完成快照，本次跳过（避免连带删除仍在排查的对象）。",
                    chain[0].Id);
                continue;
            }

            victims.Add(chain);
        }

        return victims;
    }

    private static long ChainBytes(List<BackupSnapshot> chain) => chain.Sum(s => s.TotalBytes);
}
