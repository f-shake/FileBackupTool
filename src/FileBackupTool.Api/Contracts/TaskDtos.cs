using FileBackupTool.Core.Entities;

namespace FileBackupTool.Api.Contracts;

/// <summary>备份任务（06 §4.1）。列表/详情附带的实时派生字段见 <see cref="BackupTaskDto"/> 的可空成员。</summary>
public sealed record BackupTaskDto
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string SourceDir { get; init; }

    public required string BackupDir { get; init; }

    public string? FilterJson { get; init; }

    public bool ByTimeInterval { get; init; }

    public int TimeIntervalMinutes { get; init; }

    public int MaxAutoIncrementBackupCount { get; init; }

    public bool IsDefaultVirtualBackup { get; init; }

    public bool ByWatching { get; init; }

    public bool IsEnabled { get; init; }

    public DateTime CreatedAt { get; init; }

    public DateTime UpdatedAt { get; init; }

    // ---- 实时派生字段（仅列表/详情接口填充） ----

    /// <summary>Ready / FullBackup / IncrementalBackup / Error / Queued。</summary>
    public string? Status { get; init; }

    public DateTime? LastBackupTime { get; init; }

    public LastBackupResultDto? LastBackupResult { get; init; }

    public DateTime? LastFullBackupTime { get; init; }

    public int? SnapshotCount { get; init; }

    /// <summary><c>Status == Completed</c> 的快照数。</summary>
    public int? ValidSnapshotCount { get; init; }

    /// <summary>watcher / polling / error；监控模块未接入时为 null。</summary>
    public string? MonitorMode { get; init; }

    public DateTime? NextScheduledTime { get; init; }

    /// <summary>"信息"列（旧界面同名列），如上次中断原因。</summary>
    public string? Message { get; init; }
}

/// <summary>上一次备份的结果摘要。失败计数必须显眼（07 §4.1）。</summary>
public sealed record LastBackupResultDto
{
    /// <summary>Success / PartialSuccess / Aborted / Failed。</summary>
    public required string Status { get; init; }

    public int? SnapshotId { get; init; }

    public string? SnapshotType { get; init; }

    public int FailedFileCount { get; init; }

    public DateTime? Time { get; init; }

    public string? Message { get; init; }
}

/// <summary>新建 / 修改任务的请求体。</summary>
public sealed record BackupTaskInputDto
{
    public string? Name { get; init; }

    public string? SourceDir { get; init; }

    public string? BackupDir { get; init; }

    public string? FilterJson { get; init; }

    public bool ByTimeInterval { get; init; }

    public int TimeIntervalMinutes { get; init; } = 1440;

    public int MaxAutoIncrementBackupCount { get; init; } = 30;

    public bool IsDefaultVirtualBackup { get; init; }

    public bool ByWatching { get; init; }

    public bool IsEnabled { get; init; } = true;
}

/// <summary>启用 / 禁用任务的请求体。</summary>
public sealed record SetEnabledDto(bool Enabled);

/// <summary>立即备份的请求体。</summary>
public sealed record BackupRequestDto(string? Type);

/// <summary>手动触发扫描的请求体（06 §4.5）。</summary>
public sealed record ScanRequestDto(bool? Full, string? Path);

/// <summary><c>POST /api/tasks/validate-dirs</c> 的请求体。</summary>
public sealed record ValidateDirsRequestDto
{
    public Guid? TaskId { get; init; }

    public string? SourceDir { get; init; }

    public string? BackupDir { get; init; }

    public IReadOnlyList<string>? RestoreRoots { get; init; }
}

/// <summary>目录关系校验结果（02 §7 五条规则）。</summary>
public sealed record DirValidationResultDto(bool Ok, IReadOnlyList<DirValidationIssueDto> Issues);

public sealed record DirValidationIssueDto(string Code, string Message, string? PathA, string? PathB, string? Kind);

/// <summary>入队后的作业句柄。</summary>
public sealed record JobHandleDto(string JobId, int? QueuePosition);
