namespace FileBackupTool.Core.Enums;

/// <summary>
/// 快照状态。设计文档 02 §2.2。
/// </summary>
/// <remarks>
/// 取代旧设计的 <c>EndTime == default(DateTime)</c> 隐式哨兵。
/// 分批提交必然导致"跑到一半崩溃"留下半截记录，用显式状态后 UI 可直接显示"上次备份被中断"，
/// GC 有明确依据。**查询一律只看 <see cref="Completed"/>。**
/// </remarks>
public enum SnapshotStatus
{
    /// <summary>进行中。进程启动时若发现处于此状态，一律自愈为 <see cref="Aborted"/>。</summary>
    InProgress = 0,

    /// <summary>正常完成（可能带失败计数，见 <c>BackupSnapshot.FailedFileCount</c>）。</summary>
    Completed = 1,

    /// <summary>被取消，或进程在备份过程中退出。</summary>
    Aborted = 2,
}
