namespace FileBackupTool.Core.Enums;

/// <summary>
/// 快照类型。设计文档 03 §1。
/// </summary>
public enum SnapshotType
{
    /// <summary>全量：每个文件一条 Created 记录，写入 blob（已存在的复用）。可恢复。</summary>
    Full = 0,

    /// <summary>
    /// 虚拟全量：每个文件一条 Created 记录，**不写 blob**（ContentHash 为 null）。**不可恢复**。
    /// 用于"只想看变更历史，不需要能还原"。
    /// </summary>
    VirtualFull = 1,

    /// <summary>增量：只记录 Created / Modified / Deleted 差异。可恢复。</summary>
    Increment = 2,
}
