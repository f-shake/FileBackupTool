using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Models;

/// <summary>统一分页响应。设计文档 06 §4：<c>{ items, total, page, pageSize }</c>。</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int Page, int PageSize)
{
    public static PagedResult<T> Empty(int page, int pageSize) => new([], 0, page, pageSize);
}

/// <summary>镜像中的一条条目（浏览用）。</summary>
public sealed record MirrorEntry(
    string RelativePath,
    string Name,
    bool IsDirectory,
    long Length,
    DateTime FileTimeUtc,
    string? ContentHash,
    int LastChangedSnapshotId,
    SnapshotType LastChangedSnapshotType);

/// <summary>某个路径的全部版本（文件历史）。</summary>
public sealed record FileVersionEntry(
    int SnapshotId,
    SnapshotType SnapshotType,
    DateTime SnapshotBeginTime,
    FileRecordType RecordType,
    long Length,
    DateTime FileTimeUtc,
    string? ContentHash);

/// <summary>按全量快照分组的快照列表。设计文档 04 §2.1。</summary>
public sealed record SnapshotGroup(
    Entities.BackupSnapshot Full,
    IReadOnlyList<Entities.BackupSnapshot> Increments);

/// <summary>级联删除影响清单。设计文档 04 §2.2：对话框**必须列出受影响的全部快照**。</summary>
public sealed record DeleteImpact(
    Entities.BackupSnapshot Target,
    IReadOnlyList<Entities.BackupSnapshot> Affected,
    int TotalFileRecords,
    long TotalBytes)
{
    public int AffectedCount => Affected.Count;
}

/// <summary>完整性校验报告。</summary>
public sealed record IntegrityReport(
    Guid TaskId,
    IReadOnlyList<string> RedundantBlobs,
    IReadOnlyList<string> LostBlobs,
    IReadOnlyList<string> BrokenChains,
    IReadOnlyList<string> CorruptedBlobs,
    IReadOnlyList<string> MirrorInconsistencies,
    int CheckedBlobCount,
    int CheckedRecordCount)
{
    public bool IsHealthy =>
        RedundantBlobs.Count == 0 && LostBlobs.Count == 0 && BrokenChains.Count == 0 &&
        CorruptedBlobs.Count == 0 && MirrorInconsistencies.Count == 0;
}

/// <summary>GC 结果。</summary>
public sealed record GcResult(int RecycledBlobCount, long RecycledBytes, IReadOnlyList<string> LostBlobs);

/// <summary>跨任务的目录关系校验结果。</summary>
public sealed record DirectoryValidationIssue(string Code, string Message, string PathA, string PathB);
