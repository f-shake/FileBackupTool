namespace FileBackupTool.Api.Contracts;

/// <summary>一个 blob 引用（完整性报告用）。</summary>
public sealed record BlobRefDto(string Hash, long? Length, string? ReferencedPath, int? SnapshotId);

/// <summary>快照链断裂。</summary>
public sealed record BrokenChainDto(int SnapshotId, string Type, string Reason, int? ExpectedPreviousSnapshotId);

/// <summary><c>POST /api/tasks/{id}/verify</c> 的响应（04 §2.4）。</summary>
public sealed record IntegrityReportDto
{
    public required Guid TaskId { get; init; }

    public required DateTime CheckedAt { get; init; }

    /// <summary>是否重算了哈希（TB 级极慢，默认关闭，需显式勾选）。</summary>
    public bool DeepHashChecked { get; init; }

    /// <summary>多余文件：blob 池里有、但没有任何记录引用。</summary>
    public required IReadOnlyList<BlobRefDto> OrphanBlobs { get; init; }

    public long OrphanBytes { get; init; }

    /// <summary>丢失文件：有记录、但 blob 既不在池中也不在回收站。**会导致还原失败，必须显著告警**。</summary>
    public required IReadOnlyList<BlobRefDto> MissingBlobs { get; init; }

    public required IReadOnlyList<BrokenChainDto> BrokenChains { get; init; }

    /// <summary>深校验中哈希不匹配的 blob。</summary>
    public required IReadOnlyList<BlobRefDto> CorruptBlobs { get; init; }

    /// <summary>重放镜像时发现的结构性不一致。</summary>
    public required IReadOnlyList<string> MirrorInconsistencies { get; init; }

    public int CheckedBlobCount { get; init; }

    public int CheckedRecordCount { get; init; }

    public bool Healthy { get; init; }

    public required string Summary { get; init; }
}

/// <summary>
/// <c>POST /api/tasks/{id}/verify</c> 的请求体。
/// </summary>
/// <remarks>
/// 整个 body 允许省略（前端 <c>verifyTask(id)</c> 不带参数时就是这种情况）。
/// <c>DeepHashCheck</c> 为 <c>null</c> 表示"按全局设置决定"，而不是 false——
/// 两者必须能区分：前者是"我没意见"，后者是"我明确要求只做引用比对"。
/// </remarks>
public sealed record VerifyRequestDto(bool? DeepHashCheck);

/// <summary><c>POST /api/tasks/{id}/collect</c> 的响应（02 §5.4）。</summary>
public sealed record GcResultDto(string? JobId, int MovedBlobCount, long MovedBytes, long RecycleBinBytes);

/// <summary><c>GET /api/tasks/{id}/recycle-bin</c>（02 §5.2 回收账本）。</summary>
public sealed record RecycleBinInfoDto
{
    public int BlobCount { get; init; }

    public long TotalBytes { get; init; }

    public required IReadOnlyList<RecycleBinEntryDto> Entries { get; init; }

    public int EntriesTotal { get; init; }

    public int EntriesPage { get; init; }

    public int EntriesPageSize { get; init; }

    /// <summary>账本文件路径（单个 manifest.json）。</summary>
    public string? LedgerPath { get; init; }
}

public sealed record RecycleBinEntryDto(string Hash, long Length, DateTime MovedAt, string? LastPath);
