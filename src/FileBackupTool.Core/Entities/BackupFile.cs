using System.ComponentModel.DataAnnotations;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Entities;

/// <summary>
/// 文件记录（一个路径的一个版本）。设计文档 02 §2.3。
/// </summary>
/// <remarks>
/// 内容寻址后，旧设计的 <c>BackupFileName</c>（GUID）与 <c>Hash</c>（SHA-1）两列**合并为一列**
/// <see cref="ContentHash"/>——blob 名就是哈希。
/// </remarks>
public class BackupFile
{
    /// <summary>百万级，用 64 位。</summary>
    public long Id { get; set; }

    public int SnapshotId { get; set; }

    /// <summary>冗余，用于避免 join。</summary>
    public Guid TaskId { get; set; }

    /// <summary>
    /// 相对源目录的路径，**保留原始大小写**——还原时据此重建文件名，若归一化会破坏还原保真度。
    /// </summary>
    [MaxLength(2048)]
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="RelativePath"/> 的平台相关归一化形式（Windows 上小写），
    /// **比对、索引、唯一性一律走这一列**（设计文档 02 §3.2）。
    /// </summary>
    /// <remarks>
    /// 拆成两列是对设计文档 02 §3.2 的必要细化：文档要求"归一化必须在写入与查询两侧统一应用"，
    /// 但若把归一化后的值直接存进 <c>RelativePath</c>，原始大小写就丢了，
    /// 还原出来的文件名会变成小写（在 Windows 上"能跑"，在 NAS / Linux 上就是改名）。
    /// </remarks>
    [MaxLength(2048)]
    public string RelativePathKey { get; set; } = string.Empty;

    public FileRecordType Type { get; set; }

    public long Length { get; set; }

    /// <summary>SHA-256 十六进制小写（64 字符）。<see cref="FileRecordType.Deleted"/> 与虚拟全量为 null。</summary>
    [MaxLength(64)]
    public string? ContentHash { get; set; }

    /// <summary>源文件的 <c>LastWriteTimeUtc</c>。UTC。</summary>
    public DateTime FileTimeUtc { get; set; }

    /// <summary>软删除。</summary>
    public bool IsDeleted { get; set; }

    // ---- 导航 ----

    public BackupSnapshot? Snapshot { get; set; }
}
