namespace FileBackupTool.Api.Contracts;

/// <summary>业务日志一行（06 §4.7）。</summary>
public sealed record BackupLogEntryDto
{
    public required long Id { get; init; }

    public required Guid TaskId { get; init; }

    public int? SnapshotId { get; init; }

    /// <summary>Trace / Debug / Information / Warning / Error / Critical / None。</summary>
    public required string Level { get; init; }

    /// <summary>原始 <c>Microsoft.Extensions.Logging.LogLevel</c> 数值，供前端着色排序。</summary>
    public int LevelValue { get; init; }

    public required DateTime Time { get; init; }

    public required string Message { get; init; }

    /// <summary>是否有 Detail（决定"详情"按钮是否可用）——只为这一位就把整段 Detail 传出去不值。</summary>
    public bool HasDetail { get; init; }
}

public sealed record BackupLogDetailDto
{
    public required long Id { get; init; }

    public required Guid TaskId { get; init; }

    public int? SnapshotId { get; init; }

    public required string Level { get; init; }

    public int LevelValue { get; init; }

    public required DateTime Time { get; init; }

    public required string Message { get; init; }

    public bool HasDetail { get; init; }

    public string? Detail { get; init; }
}

/// <summary>审计日志一行（05 §5）。</summary>
public sealed record AuditLogEntryDto
{
    public required long Id { get; init; }

    public required DateTime Time { get; init; }

    /// <summary>稳定事件类型字符串，如 <c>LOGIN_SUCCESS</c> / <c>SNAPSHOT_DELETE</c>。</summary>
    public required string EventType { get; init; }

    public string? Actor { get; init; }

    public string? SourceIp { get; init; }

    public string? UserAgent { get; init; }

    public string? Target { get; init; }

    public string? Detail { get; init; }
}
