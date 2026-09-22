using FileBackupTool.Api.Hubs;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Monitoring;

namespace FileBackupTool.Api.Services;

/// <summary>
/// <see cref="IBackupTrigger"/> 的宿主实现：把"这个根变脏了"投进全局作业队列。
/// </summary>
/// <remarks>
/// <para>
/// 监控模块**不执行备份**（设计文档 01 §4.3），只产生信号。这个类就是那条通道的宿主侧端点：
/// 它把信号翻译成一个 <see cref="JobRequest"/>，并带上脏目录——
/// 有了脏目录，引擎才会走局部扫描（只重扫变化的目录），这正是"实时备份"能便宜的原因。
/// </para>
/// <para>
/// <b>为什么在这里判"自动备份总开关"</b>：用户在设置里关掉自动备份，
/// 意思就是"别再自己动我的盘"。如果监控触发的备份不认这个开关，
/// 用户关掉开关后机器依然在每天写盘——那是比不实现开关更糟的欺骗。
/// </para>
/// </remarks>
public sealed class BackupTrigger(
    IJobQueue queue,
    SettingsService settings,
    SignalRJobEventPublisher events,
    ILogger<BackupTrigger> logger) : IBackupTrigger
{
    private readonly IJobQueue _queue = queue;
    private readonly SettingsService _settings = settings;
    private readonly SignalRJobEventPublisher _events = events;
    private readonly ILogger<BackupTrigger> _logger = logger;

    public async Task RequestBackupAsync(
        Guid taskId,
        IReadOnlyList<string>? dirtyRelativePaths,
        string reason,
        CancellationToken ct)
    {
        if (!await _settings.GetAutoBackupEnabledAsync(ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "任务 {TaskId} 有变更（{Reason}），但自动备份已关闭，忽略本次触发。", taskId, reason);
            return;
        }

        // 去重：同一任务已有排队/运行中的作业时不再入队。
        // 监控的合并窗口已经做过一次合并，但调度器与监控是两条独立的时间线，
        // 只靠它们各自的节流无法排除"两者同时认为该备份"。
        //
        // 代价说明：被忽略的脏目录不会写进正在跑的那个作业里。这是可接受的——
        // 那批变更在监控侧仍是"未确认"状态，下一个合并窗口会再次触发，
        // 而正在跑的这次备份结束后队列就空了，不会丢。
        if (await JobQueueQuery.HasActiveJobForTaskAsync(_queue, taskId, ct).ConfigureAwait(false))
        {
            _logger.LogInformation(
                "任务 {TaskId} 已有排队/运行中的作业，本次触发（{Reason}）不再重复入队。", taskId, reason);
            return;
        }

        bool fullScan = dirtyRelativePaths is null || dirtyRelativePaths.Count == 0;

        string jobId = await _queue.EnqueueAsync(new JobRequest
        {
            Type = JobType.Backup,
            TaskId = taskId,
            DirtyScopes = fullScan ? null : dirtyRelativePaths,
            Automatic = true,
            Description = fullScan ? $"{reason} · 全量扫描" : $"{reason} · 局部扫描（{dirtyRelativePaths!.Count} 个目录）",
        }, ct).ConfigureAwait(false);

        _logger.LogInformation("监控触发任务 {TaskId} 的备份（{Reason}），作业 {JobId}。", taskId, reason, jobId);

        // 状态变化要让 UI 立刻看到（待处理变更数清零 / 变成"已排队"）。
        await _events.MonitorChangedAsync(taskId, "watcher", 0, ct).ConfigureAwait(false);
    }
}
