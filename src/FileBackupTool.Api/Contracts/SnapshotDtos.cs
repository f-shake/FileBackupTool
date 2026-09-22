using FileBackupTool.Core.Entities;

namespace FileBackupTool.Api.Contracts;

/// <summary>快照（02 §2.2）。时间戳全部 UTC，前端转本地显示。</summary>
public sealed record SnapshotDto
{
    public required int Id { get; init; }

    public required Guid TaskId { get; init; }

    /// <summary>Full / VirtualFull / Increment。</summary>
    public required string Type { get; init; }

    /// <summary>InProgress / Completed / Aborted。</summary>
    public required string Status { get; init; }

    public required DateTime BeginTime { get; init; }

    public DateTime? EndTime { get; init; }

    public int CreatedFileCount { get; init; }

    public int ModifiedFileCount { get; init; }

    public int DeletedFileCount { get; init; }

    /// <summary>本次未成功备份的文件数。**必须显眼**——"显示成功但实际不完整"是备份工具最危险的失败模式。</summary>
    public int FailedFileCount { get; init; }

    /// <summary>本次实际写入 blob 的字节数（"占用空间"列）。</summary>
    public long TotalBytes { get; init; }

    public string? AbortReason { get; init; }

    // ---- 仅 /api/snapshots/{sid} 详情接口填充的派生字段 ----
    //
    // 契约（前端 types.ts）里 SnapshotDetail **extends Snapshot**——是"在原对象上多几个字段"，
    // 不是"嵌一层"。所以这里就把它们平铺在同一个 DTO 上，而不是再包一层，
    // 否则前端拿到的会是 { snapshot: {...}, taskName } 这种对不上的形状。

    public string? TaskName { get; init; }

    /// <summary>本快照镜像内的文件总数（由快照自身维护的镜像计数，无需 replay）。</summary>
    public long? MirrorFileCount { get; init; }

    public long? MirrorTotalBytes { get; init; }

    /// <summary>重放镜像时发现的结构性不一致条数（不静默）。</summary>
    public int? MirrorInconsistencyCount { get; init; }

    /// <summary>是否可还原。虚拟全量**不可恢复**（04 §1.6）。</summary>
    public bool? Restorable { get; init; }
}

/// <summary>按全量快照分组（04 §2.1）。</summary>
public sealed record SnapshotGroupDto(SnapshotDto FullSnapshot, IReadOnlyList<SnapshotDto> Snapshots);

/// <summary>级联删除影响清单（04 §2.2）。对话框必须列出全部受影响的快照。</summary>
public sealed record DeleteImpactDto
{
    public required int SnapshotId { get; init; }

    /// <summary>将一并删除的全部快照（含自身），按开始时间升序。</summary>
    public required IReadOnlyList<SnapshotDto> AffectedSnapshots { get; init; }

    public int AffectedCount { get; init; }

    /// <summary>涉及的文件记录数。</summary>
    public int AffectedFileCount { get; init; }

    /// <summary>其中将被软删除的记录数（= <see cref="AffectedFileCount"/>：本实现一律软删除）。</summary>
    public int SoftDeletedFileCount { get; init; }

    /// <summary>删除后**可能**移入回收站的字节数（软删除不立即释放空间）。</summary>
    public long ReclaimableBytes { get; init; }

    public IReadOnlyList<string> Notices { get; init; } = [];
}

// ---------------------------------------------------------------------------
// 变更内容 / 镜像浏览（06 §4.2 / §4.3）
// ---------------------------------------------------------------------------

/// <summary>变更内容一行。</summary>
public sealed record ChangeEntryDto
{
    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    /// <summary>Created / Modified / Deleted。</summary>
    public required string Type { get; init; }

    public long Length { get; init; }

    public DateTime? FileTimeUtc { get; init; }

    public string? ContentHash { get; init; }

    /// <summary>"信息"列，如「该文件不存在实际备份文件，可能是由虚拟快照生成」。</summary>
    public string? Info { get; init; }
}

/// <summary>新增/修改/删除三类，各类独立分页。</summary>
public sealed record SnapshotChangesDto(
    PagedDto<ChangeEntryDto> Created,
    PagedDto<ChangeEntryDto> Modified,
    PagedDto<ChangeEntryDto> Deleted);

/// <summary>镜像中的一个条目（目录或文件）。</summary>
public sealed record MirrorEntryDto
{
    public required string Name { get; init; }

    /// <summary>相对源目录的路径。引擎内部用 <c>\</c> 分隔，对外统一成 <c>/</c>。</summary>
    public required string RelativePath { get; init; }

    public bool IsDirectory { get; init; }

    public long Length { get; init; }

    public DateTime? FileTimeUtc { get; init; }

    public string? ContentHash { get; init; }

    /// <summary>是否可还原（04 §1.6）。目录恒为 true。</summary>
    public bool Restorable { get; init; } = true;

    /// <summary>不可还原原因：「…可能是由虚拟快照生成」/「…可能是文件丢失」。</summary>
    public string? BlockedReason { get; init; }
}

/// <summary>
/// <c>GET /api/snapshots/{sid}/files</c>：在统一分页响应之上附加当前目录的统计。
/// </summary>
public sealed record MirrorListingDto(
    IReadOnlyList<MirrorEntryDto> Items,
    long Total,
    int Page,
    int PageSize,
    string Path,
    int DirCount,
    int FileCount,
    long TotalBytes);

/// <summary>目录树骨架（用于导航，不含文件）。</summary>
public sealed record TreeNodeDto(string Name, string RelativePath, bool HasChildren, int DirCount, int FileCount);

/// <summary>单文件元数据（<c>GET /api/snapshots/{sid}/file?path=</c>）。</summary>
public sealed record FileMetadataDto
{
    public required int SnapshotId { get; init; }

    public required string RelativePath { get; init; }

    public required string Name { get; init; }

    public long Length { get; init; }

    public DateTime? FileTimeUtc { get; init; }

    public string? ContentHash { get; init; }

    public required string SnapshotType { get; init; }

    public required DateTime SnapshotBeginTime { get; init; }

    public bool Restorable { get; init; }

    public string? BlockedReason { get; init; }
}

/// <summary>某个路径的一个版本（文件历史）。</summary>
public sealed record FileVersionDto
{
    public required int SnapshotId { get; init; }

    public required string SnapshotType { get; init; }

    public required DateTime SnapshotBeginTime { get; init; }

    public DateTime? SnapshotEndTime { get; init; }

    public required string RelativePath { get; init; }

    public long Length { get; init; }

    public DateTime? FileTimeUtc { get; init; }

    public string? ContentHash { get; init; }

    /// <summary>Created / Modified / Deleted。</summary>
    public required string ChangeType { get; init; }

    public bool Restorable { get; init; }

    public string? BlockedReason { get; init; }
}

public sealed record FileHistoryResultDto(string RelativePath, IReadOnlyList<FileVersionDto> Versions);
