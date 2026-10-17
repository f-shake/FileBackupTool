using System.ComponentModel.DataAnnotations;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Entities;

/// <summary>
/// 快照。设计文档 02 §2.2。
/// </summary>
public class BackupSnapshot
{
    public int Id { get; set; }

    public Guid TaskId { get; set; }

    public SnapshotType Type { get; set; }

    /// <summary>显式状态，取代 <c>EndTime == default</c> 哨兵。</summary>
    public SnapshotStatus Status { get; set; } = SnapshotStatus.InProgress;

    /// <summary>UTC。</summary>
    public DateTime BeginTime { get; set; }

    /// <summary>UTC。仅在完成/中止时写入。</summary>
    public DateTime? EndTime { get; set; }

    public int CreatedFileCount { get; set; }

    public int ModifiedFileCount { get; set; }

    public int DeletedFileCount { get; set; }

    /// <summary>本次备份中处理失败的文件数。**快照必须携带失败计数**——备份工具最危险的失败模式不是报错，而是"显示成功但实际不完整"。</summary>
    public int FailedFileCount { get; set; }

    /// <summary>
    /// 本次因"仍在写入"而跳过的文件数。**不计入失败**，下一轮会自动重试。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="FailedFileCount"/> 分开是刻意的：失败代表"试过了、没成"，跳过代表
    /// "还不该试"。把它们混在一起，会让源目录里一直有几个正在写的大文件时，
    /// 每次备份都显示成"成功（N 个失败）"，用户迟早会对这个数字脱敏 ——
    /// 而它正是"备份看起来成功、实际不完整"的唯一提示。
    /// </remarks>
    public int SkippedFileCount { get; set; }

    /// <summary>本次实际写入 blob 的字节数（用于快照列表的"占用空间"列）。</summary>
    public long TotalBytes { get; set; }

    /// <summary>手动取消原因，或崩溃自愈写入的原因。</summary>
    [MaxLength(1000)]
    public string? AbortReason { get; set; }

    /// <summary>本次备份是否走了局部扫描（只重扫变化目录）。用于 UI 展示与排查。</summary>
    public bool UsedPartialScan { get; set; }

    /// <summary>
    /// 本快照所对应的**镜像**（不只是本快照的差异）的文件总数。
    /// </summary>
    /// <remarks>
    /// 这是对设计文档 02 §2.2 的一处扩展。文档没有这两列，但抢救清单的头部
    /// （<c>fileCount</c> / <c>totalBytes</c>）需要一个**当前镜像**的规模，
    /// 而按文档做法只能靠全量 replay 才能算出来——百万文件下不可接受。
    /// 增量维护它们几乎免费：全量时直接取本次计数，增量时在上个快照的基础上加减。
    /// 顺带让快照列表多出"镜像文件数 / 镜像总大小"两列。
    /// </remarks>
    public long ImageFileCount { get; set; }

    /// <summary>本快照所对应的镜像的总字节数。见 <see cref="ImageFileCount"/>。</summary>
    public long ImageTotalBytes { get; set; }

    /// <summary>重放镜像时发现的结构性不一致条数（见 <see cref="IntegrityIssueType.MirrorInconsistency"/>）。不静默。</summary>
    public int MirrorInconsistencyCount { get; set; }

    public bool IsDeleted { get; set; }

    /// <summary>软删除时间（UTC）。用于保留策略与审计。</summary>
    public DateTime? DeletedAt { get; set; }

    // ---- 导航 ----

    public BackupTask? Task { get; set; }

    public List<BackupFile> Files { get; set; } = [];

    public bool IsFull => Type is SnapshotType.Full or SnapshotType.VirtualFull;

    /// <summary>是否可供还原。虚拟全量**不可恢复**。</summary>
    public bool IsRestorable => Type != SnapshotType.VirtualFull;

    public TimeSpan? Duration => EndTime.HasValue ? EndTime.Value - BeginTime : null;
}
