namespace FileBackupTool.Api.Contracts;

/// <summary>白名单还原根目录（04 §1.3）。</summary>
public sealed record RestoreRootDto(string Path, string? Label, bool Exists, long? FreeBytes, long? TotalBytes);

/// <summary>还原范围粒度（04 §1.2）：整快照 / 目录 / 多选文件。</summary>
public static class RestoreScopeNames
{
    public const string Snapshot = "Snapshot";
    public const string Directory = "Directory";
    public const string Files = "Files";
}

/// <summary>
/// <c>POST /api/restore/plan</c> 与 <c>/api/restore/execute</c> 的请求体。
/// </summary>
/// <remarks>
/// 这是**前端契约**的形状（scope + directory/paths + targetRoot/targetSubPath），
/// 与引擎的 <c>RestoreRequest</c>（单一 TargetRoot + 相对路径集合）之间由控制器做转换。
/// 刻意让两者不共用类型：前端的"目标子路径"是两个字段，引擎只认一个绝对路径。
/// </remarks>
public sealed record RestoreApiRequestDto
{
    public string? Scope { get; init; }

    public int SnapshotId { get; init; }

    /// <summary>scope=Directory 时的单个目录相对路径。</summary>
    public string? Directory { get; init; }

    /// <summary>scope=Files 时的多选文件相对路径集合。</summary>
    public IReadOnlyList<string>? Paths { get; init; }

    /// <summary>白名单内的还原根（绝对路径）。</summary>
    public string? TargetRoot { get; init; }

    /// <summary>目标根下的子路径，缺省为空（直接落到根）。</summary>
    public string? TargetSubPath { get; init; }

    /// <summary>默认冲突策略，缺省 Skip。</summary>
    public string? DefaultStrategy { get; init; }

    /// <summary>逐文件覆盖的冲突策略，key 为 relativePath。</summary>
    public IReadOnlyDictionary<string, string>? Strategies { get; init; }

    /// <summary>预演令牌。当前实现不校验它（见控制器注释）。</summary>
    public string? PlanToken { get; init; }
}

/// <summary>预演结果中的一条（04 §1.5 向导第 3 步：冲突明细）。</summary>
public sealed record RestorePlanItemDto
{
    public required string RelativePath { get; init; }

    public required string TargetPath { get; init; }

    public long Length { get; init; }

    public DateTime? FileTimeUtc { get; init; }

    /// <summary>目标是否已存在。</summary>
    public bool Conflict { get; init; }

    public long? TargetLength { get; init; }

    public DateTime? TargetFileTimeUtc { get; init; }

    /// <summary>Create / Overwrite / Skip / Rename / Blocked。</summary>
    public required string Action { get; init; }

    /// <summary>实际生效的冲突策略。</summary>
    public required string Strategy { get; init; }

    public bool Restorable { get; init; }

    public string? BlockedReason { get; init; }
}

/// <summary>预演结果。</summary>
public sealed record RestorePlanDto
{
    /// <summary>预演令牌。当前恒为 null（引擎不支持），前端已按可空处理。</summary>
    public string? PlanToken { get; init; }

    public required string Scope { get; init; }

    public required int SnapshotId { get; init; }

    public required string TargetRoot { get; init; }

    public string? TargetSubPath { get; init; }

    /// <summary>需要注意的条目（冲突 / 不可恢复）。整快照还原时可能是**截断**的。</summary>
    public required IReadOnlyList<RestorePlanItemDto> Items { get; init; }

    public int WriteCount { get; init; }

    public int SkipCount { get; init; }

    public int ConflictCount { get; init; }

    public int BlockedCount { get; init; }

    public long TotalBytes { get; init; }

    public required string DefaultStrategy { get; init; }

    /// <summary>不可还原文件的清单：先展示，再让用户决定是否继续（04 §1.6）。</summary>
    public required IReadOnlyList<RestorePlanItemDto> BlockedItems { get; init; }

    /// <summary>条目是否被截断（<c>Items</c> 少于实际需要注意的条数）。</summary>
    public bool ItemsTruncated { get; init; }
}

/// <summary><c>POST /api/restore/execute</c> 的响应：作业句柄。</summary>
public sealed record RestoreJobHandleDto(string JobId, int? QueuePosition);

/// <summary>还原结果汇总（04 §1.5）。</summary>
public sealed record RestoreResultDto(
    string JobId,
    int Succeeded,
    int Skipped,
    int Failed,
    int Unavailable,
    long TotalBytes,
    long? ElapsedMs,
    IReadOnlyList<RestoreFailureDto> Failures);

public sealed record RestoreFailureDto(string RelativePath, string TargetPath, string Error);
