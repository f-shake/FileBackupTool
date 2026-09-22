using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;

namespace FileBackupTool.Core.Abstractions;

/// <summary>一个后台 IO 作业的请求。</summary>
public sealed record JobRequest
{
    public required JobType Type { get; init; }

    public Guid? TaskId { get; init; }

    public int? SnapshotId { get; init; }

    /// <summary>给 UI 看的说明，例如「文档备份 · 增量」。</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>备份类型；null 表示按全量/增量策略自动决定。</summary>
    public SnapshotType? BackupType { get; init; }

    /// <summary>局部扫描的脏目录（源目录相对路径）。</summary>
    public IReadOnlyList<string>? DirtyScopes { get; init; }

    /// <summary>还原计划。</summary>
    public RestorePlan? RestorePlan { get; init; }

    /// <summary>
    /// 完整校验是否重算哈希（深度校验）。设计文档 08 §4.2：默认**只做引用比对**，
    /// 重算哈希需显式勾选——TB 级重算极慢。
    /// </summary>
    public bool DeepHashCheck { get; init; }

    /// <summary>是否由调度/监控自动触发（而非用户点击）。</summary>
    public bool Automatic { get; init; }
}

/// <summary>作业的只读视图（供 API 返回）。</summary>
public sealed record JobView(
    string Id,
    JobType Type,
    JobStatus Status,
    string Description,
    Guid? TaskId,
    int? SnapshotId,
    DateTime EnqueuedAtUtc,
    DateTime? StartedAtUtc,
    DateTime? EndedAtUtc,
    int? QueuePosition,
    JobProgress Progress,
    string? Error,
    BackupRunResult? BackupResult,
    RestoreResult? RestoreResult,
    GcResult? GcResult,
    IntegrityReport? IntegrityReport);

/// <summary>
/// 全局 IO 作业队列。设计文档 01 §4.1。
/// </summary>
/// <remarks>
/// <b>任何时刻，全局最多只有一个后台 IO 作业在运行。</b>
/// 理由：源与目标都在机械硬盘上，并发只会因寻道抖动互相拖慢；
/// 并发会让"某任务何时完成"完全不可预测；还原若与同任务的备份并行，
/// 还原出来的文件会被正在跑的备份当成新变更**再备份一遍**。
/// <para>
/// <b>队列不跨进程重启恢复</b>：重启后 <c>InProgress</c> 的快照会被启动自愈标记为
/// <c>Aborted</c>，排队中的作业直接作废，用户重新触发。
/// </para>
/// </remarks>
public interface IJobQueue
{
    /// <summary>当前是否有作业在跑（或队列非空）。</summary>
    bool IsBusy { get; }

    /// <summary>队列里正在等待的作业数。</summary>
    int QueuedCount { get; }

    /// <summary>ENQUEUE。返回作业 id。</summary>
    Task<string> EnqueueAsync(JobRequest request, CancellationToken ct);

    Task<IReadOnlyList<JobView>> ListAsync(CancellationToken ct);

    Task<JobView?> GetAsync(string jobId, CancellationToken ct);

    /// <summary>取消（排队中则作废，运行中则发取消信号）。</summary>
    Task<bool> CancelAsync(string jobId, CancellationToken ct);

    /// <summary>取消某个任务当前正在跑或排队的作业。</summary>
    Task<bool> CancelForTaskAsync(Guid taskId, CancellationToken ct);
}

/// <summary>
/// 作业事件发布（SignalR 的接入点）。设计文档 06 §5。
/// </summary>
public interface IJobEventPublisher
{
    Task PublishAsync(string eventName, object payload, CancellationToken ct);
}

/// <summary>不发事件（单元测试与无实时通道时用）。</summary>
public sealed class NullJobEventPublisher : IJobEventPublisher
{
    public static readonly NullJobEventPublisher Instance = new();

    public Task PublishAsync(string eventName, object payload, CancellationToken ct) => Task.CompletedTask;
}
