namespace FileBackupTool.Api.Contracts;

/// <summary>待处理变更队列中的一条（哪个目录脏了 + 静默期倒计时）。</summary>
public sealed record PendingChangeDirDto(
    string RelativePath,
    int ChangedFileCount,
    DateTime FirstChangeTime,
    double SettleSecondsRemaining);

/// <summary>每个任务/根的监控状态（07 §4.6）。</summary>
public sealed record RootMonitorStatusDto
{
    public required Guid TaskId { get; init; }

    public required string TaskName { get; init; }

    public required string SourceDir { get; init; }

    /// <summary>watcher（实时监控）/ polling（UNC 源轮询）/ error（监控异常）。</summary>
    public required string State { get; init; }

    public DateTime? LastScanTime { get; init; }

    public DateTime? NextScanTime { get; init; }

    /// <summary>待处理变更队列长度。</summary>
    public int PendingCount { get; init; }

    public IReadOnlyList<PendingChangeDirDto> PendingDirs { get; init; } = [];

    /// <summary>最近一次全量扫描时间。</summary>
    public DateTime? LastFullScanTime { get; init; }

    public DateTime? LastTriggerTime { get; init; }

    /// <summary>最近一次触发原因（引擎的 <c>RootMonitorStatus</c> 不带这个字段，恒为 null）。</summary>
    public string? LastTriggerReason { get; init; }

    public string? Error { get; init; }
}

/// <summary>最近触发记录。</summary>
public sealed record TriggerRecordDto(
    Guid TaskId,
    string TaskName,
    DateTime Time,
    string Reason,
    bool Triggered,
    string? SkippedReason,
    int? SnapshotId);

/// <summary><c>GET /api/monitoring</c> 的响应。</summary>
public sealed record MonitoringSnapshotDto(
    IReadOnlyList<RootMonitorStatusDto> Roots,
    JobQueueSnapshotDto Jobs,
    IReadOnlyList<TriggerRecordDto> RecentTriggers,
    DateTime ServerTime);
