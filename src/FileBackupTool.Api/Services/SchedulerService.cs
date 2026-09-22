using FileBackupTool.Api.Configuration;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Services;

/// <summary>
/// 定时备份调度（设计文档 06 §8：每 60 秒检查一次任务是否到期并入队）。
/// </summary>
/// <remarks>
/// <para>
/// <b>判据一律来自数据库，不来自进程内字段。</b> 这是设计文档明确要求的：
/// "上次备份时间"必须等于**库里已完成快照的最大 <c>BeginTime</c>**，
/// 而不是"本进程记得的某次备份"。理由很实在——
/// <list type="bullet">
/// <item>进程重启后内存字段全没了，若靠它判定，重启就会把所有任务都当成"到期"，
/// 于是重启一次就全量备份一次；</item>
/// <item>快照也可能由别的路径产生（手动点击、监控触发、将来的多实例），
/// 只有数据库是共同的事实来源。</item>
/// </list>
/// 同理，"这个任务正在备份"的判据是**库里存在 <c>InProgress</c> 的快照**。
/// </para>
/// <para>
/// <b>节拍为什么是 PeriodicTimer 而不是 Task.Delay 循环</b>：<c>PeriodicTimer</c>
/// 不会累积漂移（每次 <c>WaitForNextTickAsync</c> 都以固定节拍为准），
/// 而 <c>Task.Delay(60s)</c> 循环会把每次检查的耗时加到周期上，
/// 当检查变慢（库被大备份写满）时节拍会越拖越长。
/// </para>
/// <para>
/// <b>单次失败必须被吞掉</b>：一次数据库瞬时忙（SQLITE_BUSY）不该让调度器永久死掉——
/// 那意味着"从此再也不自动备份"，而且没有任何界面提示。所以循环内 catch 全部异常并记 Error。
/// </para>
/// </remarks>
public sealed class SchedulerService(
    TaskStore tasks,
    IDbContextFactory<AppDbContext> dbFactory,
    SettingsService settings,
    IJobQueue queue,
    IBackupLogSink logSink,
    IClock clock,
    IOptions<SchedulerOptions> schedulerOptions,
    ILogger<SchedulerService> logger) : BackgroundService
{
    private readonly TaskStore _tasks = tasks;
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly SettingsService _settings = settings;
    private readonly IJobQueue _queue = queue;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly IClock _clock = clock;
    private readonly SchedulerOptions _options = schedulerOptions.Value;
    private readonly ILogger<SchedulerService> _logger = logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 下限 5 秒：配成 0 或负数时不至于变成忙等。
        var period = TimeSpan.FromSeconds(Math.Max(5, _options.TickSeconds));
        _logger.LogInformation("定时备份调度已启动，节拍 {Seconds} 秒。", period.TotalSeconds);

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
                _logger.LogError(ex, "定时备份调度本轮检查失败，将在下一个节拍重试。");
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
        // 自动备份总开关：关掉之后连"检查"都不做。这不是性能优化，
        // 而是让"关了就是真的没动"这件事在代码上成立（见 BackupTrigger 的同款判断）。
        if (!await _settings.GetAutoBackupEnabledAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, ct).ConfigureAwait(false);
        if (all.Count == 0)
        {
            return;
        }

        Dictionary<Guid, DateTime> lastBackups = await _tasks.GetLastBackupTimesAsync(ct).ConfigureAwait(false);
        HashSet<Guid> inProgress = await GetInProgressTaskIdsAsync(ct).ConfigureAwait(false);
        DateTime now = _clock.UtcNow;

        foreach (BackupTask task in all)
        {
            if (!task.IsEnabled || !task.ByTimeInterval)
            {
                continue;
            }

            if (task.TimeIntervalMinutes <= 0)
            {
                // 间隔配成 0 / 负数视为"没配好"，跳过而不是当成"每分钟备份"。
                _logger.LogWarning("任务「{Name}」的备份间隔为 {Minutes} 分钟，配置无效，已跳过。",
                    task.Name, task.TimeIntervalMinutes);
                continue;
            }

            if (inProgress.Contains(task.Id))
            {
                // 库里还有未结束的快照 = 该任务正在备份。
                continue;
            }

            DateTime? last = lastBackups.TryGetValue(task.Id, out DateTime value) ? value : null;

            // 从未备份过（last == null）视为"已到期"：用户既然开了定时备份，
            // 就不该等一个完整间隔才看到第一次备份。
            if (last is not null && last.Value.AddMinutes(task.TimeIntervalMinutes) > now)
            {
                continue;
            }

            if (await JobQueueQuery.HasActiveJobForTaskAsync(_queue, task.Id, ct).ConfigureAwait(false))
            {
                continue;
            }

            string jobId = await _queue.EnqueueAsync(new JobRequest
            {
                Type = JobType.Backup,
                TaskId = task.Id,
                // BackupType = null：让引擎按"全量/虚拟全量/增量"策略自行决定（设计文档 03 §2）。
                Automatic = true,
                Description = $"{task.Name} · 定时备份",
            }, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "任务「{Name}」已到期（上次 {Last:u}，间隔 {Minutes} 分钟），作业 {JobId} 已入队。",
                task.Name, last, task.TimeIntervalMinutes, jobId);

            // 让"为什么突然开始备份了"在任务的日志里有答案。
            await _logSink.WriteAsync(
                task.Id,
                null,
                LogLevel.Information,
                $"到达定时备份时间（间隔 {task.TimeIntervalMinutes} 分钟），已加入队列。",
                null,
                ct).ConfigureAwait(false);
        }
    }

    /// <summary>库里存在未结束快照的任务 id（"正在备份"的权威判据）。</summary>
    private async Task<HashSet<Guid>> GetInProgressTaskIdsAsync(CancellationToken ct)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        List<Guid> ids = await db.Snapshots
            .Where(s => !s.IsDeleted && s.Status == SnapshotStatus.InProgress)
            .Select(s => s.TaskId)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. ids];
    }
}
