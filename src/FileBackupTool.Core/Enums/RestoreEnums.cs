namespace FileBackupTool.Core.Enums;

/// <summary>
/// 还原冲突策略。设计文档 04 §1.4。全局默认 <see cref="Skip"/>（最不具破坏性）。
/// </summary>
/// <remarks>
/// **不做静默覆盖**——旧设计的"另存为"用的是 <c>FileMode.Create</c>，会静默覆盖目标文件。
/// </remarks>
public enum RestoreConflictPolicy
{
    /// <summary>目标已存在则不动。</summary>
    Skip = 0,

    /// <summary>覆盖目标。</summary>
    Overwrite = 1,

    /// <summary>目标重命名为 <c>原名 (1).ext</c> 之类，两者都保留。</summary>
    KeepBoth = 2,

    /// <summary>比较时间戳，较新者优先。</summary>
    NewerWins = 3,
}

/// <summary>单个还原条目的结果。</summary>
public enum RestoreOutcome
{
    Restored = 0,

    /// <summary>因冲突策略被跳过。</summary>
    Skipped = 1,

    Failed = 2,

    /// <summary>记录来自虚拟全量快照（ContentHash 为 null）。文案：「该文件不存在实际备份文件，可能是由虚拟快照生成」。</summary>
    UnavailableVirtual = 3,

    /// <summary>blob 既不在池中也不在回收站。文案：「该文件不存在实际备份文件，可能是文件丢失」。</summary>
    UnavailableMissingBlob = 4,
}
