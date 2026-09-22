using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Models;

namespace FileBackupTool.Api.Contracts;

/// <summary>全局队列快照（06 §4.5）。全局只有一个运行中的作业（01 §4.1）。</summary>
public sealed record JobQueueSnapshotDto(QueuedJobDto? Running, IReadOnlyList<QueuedJobDto> Queued);

/// <summary>队列中的一个作业。</summary>
public sealed record QueuedJobDto
{
    public required string JobId { get; init; }

    /// <summary>Backup / Restore / Verify / Collect。</summary>
    public required string Type { get; init; }

    /// <summary>Queued / Running / Completed / Failed / Aborted。</summary>
    public required string State { get; init; }

    public Guid? TaskId { get; init; }

    public string? TaskName { get; init; }

    public int? SnapshotId { get; init; }

    /// <summary>排队位置，**1 表示下一个执行**（与前端契约一致；引擎内部是 0 基）。</summary>
    public int? Position { get; init; }

    public required DateTime EnqueuedAt { get; init; }

    public DateTime? StartedAt { get; init; }

    public DateTime? FinishedAt { get; init; }

    public JobProgressDto? Progress { get; init; }

    public string? Message { get; init; }

    public string? Error { get; init; }
}

/// <summary>两段式进度（06 §6）。</summary>
public sealed record JobProgressDto(
    string JobId,
    string Phase,
    int Discovered,
    int Processed,
    int? Total,
    string? CurrentPath,
    long BytesDone,
    long? BytesTotal);

/// <summary>
/// 作业视图 → DTO。集中在一处，避免每个控制器各写一遍"字段名翻译"。
/// </summary>
public static class JobViewMapper
{
    public static QueuedJobDto Map(JobView view, string? taskName) => new()
    {
        JobId = view.Id,
        Type = view.Type.ToString(),
        State = view.Status.ToString(),
        TaskId = view.TaskId,
        TaskName = taskName,
        SnapshotId = view.SnapshotId,
        // 引擎的 QueuePosition 是 0 基（0 = 下一个执行），前端契约明确写了 1 基。
        Position = view.QueuePosition is null ? null : view.QueuePosition.Value + 1,
        EnqueuedAt = view.EnqueuedAtUtc,
        StartedAt = view.StartedAtUtc,
        FinishedAt = view.EndedAtUtc,
        Progress = new JobProgressDto(
            view.Id,
            view.Progress.Phase.ToString(),
            view.Progress.Discovered,
            view.Progress.Processed,
            view.Progress.Total,
            view.Progress.CurrentPath,
            view.Progress.BytesDone,
            view.Progress.Total is null ? null : view.Progress.BytesTotal),
        Message = view.Description,
        Error = view.Error,
    };
}
