using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.Extensions.Hosting;

namespace FileBackupTool.Api.Services;

/// <summary>
/// 启动自愈 + 启动自检（设计文档 06 §8，只在启动时跑一次）。
/// </summary>
/// <remarks>
/// <para>
/// <b>自愈</b>：把库里仍是 <c>InProgress</c> 的快照标记为 <c>Aborted</c>。
/// 队列**不跨进程重启恢复**（01 §4.1），所以进程上次被杀之后，那些快照永远等不到
/// 它们的收尾代码。留着它们的后果很具体：快照列表里永远有一条"进行中"，
/// 调度器会认为该任务还在备份而不再触发，前端会一直显示转圈——
/// 一次断电就能让该任务**从此不再自动备份**。
/// </para>
/// <para>
/// <b>为什么放在 <see cref="IHostedService.StartAsync"/> 而不是 <c>BackgroundService</c></b>：
/// 托管服务的 <c>StartAsync</c> 会在 Kestrel 开始接受请求**之前**完成。
/// 自愈必须在任何请求（尤其是 <c>GET /api/monitoring</c> 与调度器）之前生效，
/// 否则启动瞬间会短暂呈现"正在备份"的假状态。
/// </para>
/// <para>
/// <b>自检不阻塞启动</b>：自检发现的问题（源目录不见了、还原根所在的盘没挂载）
/// 只写进 <c>RuntimeInfo.Notices</c> 并由 <c>/api/settings/info</c> 展示，
/// **不让宿主起不来**。理由是它属于"运行环境问题"而不是"配置错误"——
/// 把服务卡在这里会让用户连界面都进不去，反而无法查看问题。
/// （对比：初始密码缺失属于配置错误，那种必须拒绝启动。）
/// </para>
/// </remarks>
public sealed class StartupSelfHealService(
    SnapshotChainService chains,
    TaskStore tasks,
    SettingsService settings,
    IBackupLogSink logSink,
    IFileSystem fs,
    IPathNormalizer normalizer,
    AppPaths paths,
    RuntimeInfo runtime,
    IClock clock,
    ILogger<StartupSelfHealService> logger) : IHostedService
{
    private readonly SnapshotChainService _chains = chains;
    private readonly TaskStore _tasks = tasks;
    private readonly SettingsService _settings = settings;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly AppPaths _paths = paths;
    private readonly RuntimeInfo _runtime = runtime;
    private readonly IClock _clock = clock;
    private readonly ILogger<StartupSelfHealService> _logger = logger;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await HealAsync(cancellationToken).ConfigureAwait(false);
        await SelfCheckAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>把上次进程遗留的 <c>InProgress</c> 快照标记为 <c>Aborted</c>。</summary>
    private async Task HealAsync(CancellationToken ct)
    {
        const string reason = "宿主启动时发现该快照仍处于「进行中」状态：上次进程未正常退出（崩溃 / 断电 / 被强杀）。";

        try
        {
            List<int> aborted = await _chains
                .AbortStaleInProgressAsync(reason, _clock.UtcNow, ct)
                .ConfigureAwait(false);

            if (aborted.Count == 0)
            {
                return;
            }

            _logger.LogWarning(
                "启动自愈：把 {Count} 个遗留的「进行中」快照标记为已中止（{Ids}）。",
                aborted.Count, string.Join(", ", aborted));

            _runtime.Notices.Add(
                $"启动自愈：{aborted.Count} 个上次未正常结束的快照已被标记为「已中止」" +
                $"（编号 {string.Join(", ", aborted)}）。它们不算可用的还原点。");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 自愈失败要显著，但同样不能拦住启动：库可能只是被另一个进程短暂锁住。
            _logger.LogError(ex, "启动自愈失败：遗留的「进行中」快照未被清理。");
            _runtime.Notices.Add("启动自愈失败：存在遗留的「进行中」快照，详见日志。");
        }
    }

    /// <summary>轻量自检：数据目录可写、源/备份/还原根是否存在。**不做全盘扫描**。</summary>
    private async Task SelfCheckAsync(CancellationToken ct)
    {
        try
        {
            CheckDataDirWritable();

            List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, ct).ConfigureAwait(false);

            foreach (BackupTask task in all)
            {
                if (!_fs.DirectoryExists(task.SourceDir))
                {
                    _runtime.Notices.Add($"任务「{task.Name}」的源目录不存在：{task.SourceDir}");
                    _logger.LogWarning("任务「{Name}」的源目录不存在：{Dir}", task.Name, task.SourceDir);
                }

                if (!_fs.DirectoryExists(task.BackupDir))
                {
                    // 备份目录不存在不是错误（首次备份会创建），但值得说一声。
                    _logger.LogInformation("任务「{Name}」的备份目录尚不存在，将在首次备份时创建：{Dir}",
                        task.Name, task.BackupDir);
                }
            }

            List<string> roots = await _settings.GetAllowedRestoreRootsAsync(ct).ConfigureAwait(false);
            if (roots.Count == 0)
            {
                _runtime.Notices.Add("尚未配置还原根白名单，还原功能不可用（设计文档 04 §1.3 的硬约束）。");
            }

            foreach (string root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                if (!_fs.DirectoryExists(root))
                {
                    _runtime.Notices.Add($"还原根目录不存在（盘未挂载？）：{root}");
                    _logger.LogWarning("白名单里的还原根目录不存在：{Root}", root);
                }
            }

            if (_runtime.Notices.Count > 0)
            {
                // 让"一次也没打开过设置页"的用户也能在任务日志里看到这些问题。
                foreach (BackupTask task in all)
                {
                    await _logSink.WriteAsync(
                        task.Id,
                        null,
                        LogLevel.Warning,
                        "启动自检发现问题，详见「设置 · 服务器信息」。",
                        string.Join(Environment.NewLine, _runtime.Notices),
                        ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "启动自检失败（不影响服务启动）。");
            _runtime.Notices.Add("启动自检失败，详见日志。");
        }
    }

    private void CheckDataDirWritable()
    {
        string probe = _normalizer.Combine(
            _paths.DataDir,
            $".write-probe-{Guid.NewGuid():N}.tmp");

        try
        {
            using (Stream stream = _fs.Create(probe))
            {
                stream.WriteByte(0);
            }

            _fs.DeleteFile(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _runtime.Notices.Add($"数据目录不可写：{_paths.DataDir}（数据库与日志都会失败）。");
            _logger.LogError(ex, "数据目录不可写：{Dir}", _paths.DataDir);
        }
        finally
        {
            // 探针文件必须清掉：它会落在 App_Data 里，而 App_Data 是要排除在备份之外的。
            try
            {
                if (_fs.FileExists(probe))
                {
                    _fs.DeleteFile(probe);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 清不掉也无妨，下次启动会再探一次。
                _logger.LogDebug(ex, "清理写入探针文件失败：{Path}", probe);
            }
        }
    }
}
