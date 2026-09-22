using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 把任务实体 + 实时派生量拼成线上 DTO（设计文档 06 §4.1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这些字段要一次性算出来</b>：前端的任务列表只有一列"状态"，但它必须能区分
/// "正在跑全量"/"正在跑增量"/"已排队"/"上次失败"/"就绪"——这正是用户唯一一眼能看到的健康指标。
/// 让前端靠多次轮询自己拼，会拼出与后端不一致的结论（例如刷新快照列表的间隔里状态已变）。
/// </para>
/// <para>
/// <b>判据一律取数据库与作业队列</b>，不取进程内残留字段：进程重启后前者仍在，后者全丢。
/// </para>
/// </remarks>
public sealed class TaskProjection(
    TaskStatsQuery statsQuery,
    IJobQueue queue,
    ApiMapper mapper,
    MonitoringAccess monitoring,
    IClock clock)
{
    private readonly TaskStatsQuery _statsQuery = statsQuery;
    private readonly IJobQueue _queue = queue;
    private readonly ApiMapper _mapper = mapper;
    private readonly MonitoringAccess _monitoring = monitoring;
    private readonly IClock _clock = clock;

    /// <summary>批量构造（列表接口用；一次查询算全部任务，不做 N+1）。</summary>
    public async Task<IReadOnlyList<BackupTaskDto>> BuildListAsync(
        IReadOnlyList<BackupTask> tasks,
        CancellationToken ct)
    {
        Dictionary<Guid, TaskRuntimeStats> stats = await _statsQuery.LoadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<JobView> jobs = await _queue.ListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, JobStatus> activeBackups = ActiveBackupJobs(jobs);

        return
        [
            .. tasks.Select(task => Build(
                task,
                GetStats(stats, task.Id),
                GetActiveBackup(activeBackups, task.Id),
                _monitoring.GetModeForTask(task.Id))),
        ];
    }

    /// <summary>单个任务（详情、增删改的响应）。</summary>
    public async Task<BackupTaskDto> BuildOneAsync(BackupTask task, CancellationToken ct)
    {
        Dictionary<Guid, TaskRuntimeStats> stats = await _statsQuery.LoadAsync(ct).ConfigureAwait(false);
        IReadOnlyList<JobView> jobs = await _queue.ListAsync(ct).ConfigureAwait(false);

        return Build(
            task,
            GetStats(stats, task.Id),
            GetActiveBackup(ActiveBackupJobs(jobs), task.Id),
            _monitoring.GetModeForTask(task.Id));
    }

    /// <summary>
    /// 显式 <c>TryGetValue</c>，不用 <c>GetValueOrDefault</c>。
    /// </summary>
    /// <remarks>
    /// <c>CollectionExtensions.GetValueOrDefault</c> 在这里会因为
    /// "字典值类型 <c>TaskRuntimeStats</c> 与默认值 <c>TaskRuntimeStats?</c> 不一致"
    /// 而推断不出类型参数（CS0411）——显式写法既没有这个问题，也把"缺省 = null"表达得更直白。
    /// </remarks>
    private static TaskRuntimeStats? GetStats(Dictionary<Guid, TaskRuntimeStats> stats, Guid taskId) =>
        stats.TryGetValue(taskId, out TaskRuntimeStats? value) ? value : null;

    private static JobStatus? GetActiveBackup(Dictionary<Guid, JobStatus> activeBackups, Guid taskId) =>
        activeBackups.TryGetValue(taskId, out JobStatus value) ? value : null;

    /// <summary>
    /// 每个任务"正在排队/运行"的**备份**作业状态。
    /// </summary>
    /// <remarks>
    /// 只看备份作业：完整性校验、回收、还原都不改变"这个任务的备份状态"，
    /// 把它们算进来会让界面在用户点了一次"完整校验"后把任务显示成"备份排队中"。
    /// 一个任务同时只可能有一个活跃备份作业（调度与监控都先查队列，见 <see cref="JobQueueQuery"/>）。
    /// </remarks>
    private static Dictionary<Guid, JobStatus> ActiveBackupJobs(IReadOnlyList<JobView> jobs)
    {
        Dictionary<Guid, JobStatus> result = [];

        foreach (JobView job in jobs)
        {
            if (!job.TaskId.HasValue || job.Type != JobType.Backup)
            {
                continue;
            }

            if (job.Status is not (JobStatus.Queued or JobStatus.Running))
            {
                continue;
            }

            // Running 覆盖 Queued：已经跑起来的那条信息量更大。
            if (!result.TryGetValue(job.TaskId.Value, out JobStatus existing) || job.Status == JobStatus.Running)
            {
                result[job.TaskId.Value] = job.Status;
            }
        }

        return result;
    }

    private BackupTaskDto Build(
        BackupTask task,
        TaskRuntimeStats? stats,
        JobStatus? activeBackupJob,
        string? monitorMode) => _mapper.Task(task) with
    {
        Status = DeriveStatus(stats, activeBackupJob),
        LastBackupTime = stats?.LastBackupUtc,
        LastBackupResult = DeriveLastResult(stats),
        LastFullBackupTime = stats?.LastFullBackupUtc,
        SnapshotCount = stats?.SnapshotCount,
        ValidSnapshotCount = stats?.ValidSnapshotCount,
        MonitorMode = monitorMode,
        NextScheduledTime = DeriveNextScheduled(task, stats),
        Message = DeriveMessage(stats, activeBackupJob),
    };

    /// <summary>
    /// Ready / FullBackup / IncrementalBackup / Error / Queued。
    /// </summary>
    /// <remarks>
    /// 优先级：**正在备份**（有 InProgress 快照）> 已排队 > 上次中止 > 就绪。
    /// 正在备份优先于排队是必须的：作业已经跑起来了，界面却显示"排队中"，
    /// 用户会以为可以取消掉重来。
    /// <para>
    /// <c>Error</c> 只由"最近一条快照是 Aborted"推出，**不**由失败计数推出——
    /// 带失败计数但已完成的快照是"完成但有 N 个文件没备份"，那是 <c>Ready</c> + 醒目的失败数
    ///（前端在 lastBackupResult 里显示），把它染成红色会让真正的中止被淹没。
    /// </para>
    /// <para>
    /// 仅有"作业在跑但快照还没建出来"这一瞬间会落到 <c>Queued</c>：备份作业的第一件事就是建
    /// InProgress 快照，那个窗口是毫秒级的，用 <c>Queued</c> 表示"正在启动"足够准确，
    /// 也避免为它编造一个 <c>FullBackup</c>（那会谎报类型）。
    /// </para>
    /// </remarks>
    private static string DeriveStatus(TaskRuntimeStats? stats, JobStatus? activeBackupJob)
    {
        if (stats?.InProgressType is { } type)
        {
            return type == SnapshotType.Increment ? "IncrementalBackup" : "FullBackup";
        }

        if (activeBackupJob is JobStatus.Queued or JobStatus.Running)
        {
            return "Queued";
        }

        return stats?.LatestSnapshot?.Status == SnapshotStatus.Aborted ? "Error" : "Ready";
    }

    private static LastBackupResultDto? DeriveLastResult(TaskRuntimeStats? stats)
    {
        BackupSnapshot? latest = stats?.LatestSnapshot;
        if (latest is null)
        {
            return null;
        }

        string status = latest.Status switch
        {
            SnapshotStatus.Aborted => "Aborted",
            SnapshotStatus.InProgress => "Running",
            _ => latest.FailedFileCount > 0 ? "PartialSuccess" : "Success",
        };

        return new LastBackupResultDto
        {
            Status = status,
            SnapshotId = latest.Id,
            SnapshotType = latest.Type.ToString(),
            FailedFileCount = latest.FailedFileCount,
            Time = latest.EndTime ?? latest.BeginTime,
            Message = latest.AbortReason,
        };
    }

    /// <summary>
    /// 下次定时备份时间。
    /// </summary>
    /// <remarks>
    /// 与 <c>SchedulerService</c> 用同一条判据（上次成功备份 + 间隔），所以界面上显示的时间
    /// 就是调度器真的会跑的时间。没到时间才显示未来时刻；已到时间就显示"现在"，
    /// 否则用户会看到一个过去的时刻而以为调度坏了。
    /// </remarks>
    private DateTime? DeriveNextScheduled(BackupTask task, TaskRuntimeStats? stats)
    {
        if (!task.IsEnabled || !task.ByTimeInterval || task.TimeIntervalMinutes <= 0)
        {
            return null;
        }

        DateTime now = _clock.UtcNow;
        DateTime next = (stats?.LastBackupUtc ?? now).AddMinutes(task.TimeIntervalMinutes);
        return next < now ? now : next;
    }

    private static string? DeriveMessage(TaskRuntimeStats? stats, JobStatus? activeBackupJob)
    {
        BackupSnapshot? latest = stats?.LatestSnapshot;
        if (latest is null)
        {
            return activeBackupJob is null ? "尚未备份过" : "备份已排队。";
        }

        if (latest.Status == SnapshotStatus.InProgress)
        {
            return "正在备份…";
        }

        if (activeBackupJob is JobStatus.Queued)
        {
            return "备份已排队，等待前一个作业完成。";
        }

        if (latest.Status == SnapshotStatus.Aborted)
        {
            return latest.AbortReason is { Length: > 0 } reason ? $"上次备份被中断：{reason}" : "上次备份被中断。";
        }

        return latest.FailedFileCount > 0
            ? $"上次备份完成，但有 {latest.FailedFileCount} 个文件未能备份。"
            : null;
    }
}
