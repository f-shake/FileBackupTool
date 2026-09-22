using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Models;

/// <summary>后台作业的阶段。设计文档 06 §6：枚举阶段是**不确定态**，处理阶段才是确定态百分比。</summary>
public enum JobPhase
{
    Queued = 0,

    /// <summary>枚举源文件（先数一遍等于多扫一次盘，所以是不确定态 + 实时计数）。</summary>
    Enumerating = 1,

    /// <summary>逐文件处理（确定态百分比）。</summary>
    Processing = 2,

    /// <summary>正在写入数据库…</summary>
    Committing = 3,

    /// <summary>正在生成抢救清单…</summary>
    Manifest = 4,

    /// <summary>正在回收…</summary>
    Collecting = 5,

    /// <summary>还原预演。</summary>
    Planning = 6,

    /// <summary>正在还原。</summary>
    Restoring = 7,

    Completed = 8,
}

public readonly record struct JobProgress(
    JobPhase Phase,
    int Discovered = 0,
    int Processed = 0,
    int? Total = null,
    string? CurrentPath = null,
    long BytesDone = 0,
    long BytesTotal = 0);

/// <summary>一次备份的运行结果。</summary>
public sealed record BackupRunResult(
    int SnapshotId,
    SnapshotType Type,
    SnapshotStatus Status,
    int CreatedFileCount,
    int ModifiedFileCount,
    int DeletedFileCount,
    int FailedFileCount,
    long TotalBytes,
    /// <summary>本快照对应的**镜像**（不只是本次差异）的文件总数。</summary>
    long ImageFileCount = 0,
    /// <summary>本快照对应的镜像的总字节数。</summary>
    long ImageTotalBytes = 0,
    string? AbortReason = null)
{
    /// <summary>是否有文件未能备份。UI 必须据此显示"完成，但 N 个文件未备份"。</summary>
    public bool HasFailures => FailedFileCount > 0;

    /// <summary>
    /// 这次备份是否**因用户取消**而作废。
    /// </summary>
    /// <remarks>
    /// 引擎把取消**吞掉**了：<c>RunAsync</c> 把快照正确置为 <c>Aborted</c> 后返回一个**普通结果**
    /// 而不抛异常（09 B6 要求如此，好让"快照已作废"这个事实落库）。
    /// 于是作业层不能只看"有没有抛异常"，需要一个显式标志把"用户取消"与
    /// "因为失败率/连续失败/异常而中止"分开——否则用户点了取消，
    /// <c>GET /api/jobs</c> 与事件流都会显示这个作业 **Completed**。
    /// </remarks>
    public bool Canceled { get; init; }
}

// ---------------------------------------------------------------------------
// 还原
// ---------------------------------------------------------------------------

/// <summary>还原请求。设计文档 04 §1.2：粒度 = 整快照 / 目录 / 多选文件。</summary>
public sealed record RestoreRequest(
    Guid TaskId,
    int SnapshotId,
    /// <summary>要还原的相对路径集合（文件或目录）。**为空表示整快照**。</summary>
    IReadOnlyList<string> RelativePaths,
    /// <summary>还原目标根。必须落在白名单还原根目录之内。</summary>
    string TargetRoot,
    RestoreConflictPolicy ConflictPolicy = RestoreConflictPolicy.Skip,
    /// <summary>是否"应用到全部冲突"（UI 上避免逐文件弹窗）。</summary>
    bool ApplyPolicyToAll = true,
    /// <summary>
    /// **逐文件**的冲突策略覆盖（设计文档 04 §1.4 要求"逐文件可选"，全局默认才是
    /// <see cref="RestoreConflictPolicy.Skip"/>）。键是相对路径，大小写不敏感。
    /// 未命中的条目回落到 <see cref="ConflictPolicy"/>。
    /// </summary>
    IReadOnlyDictionary<string, RestoreConflictPolicy>? PerPathPolicies = null);

/// <summary>计划中一条目的拟定动作。</summary>
public enum RestorePlanAction
{
    Restore = 0,
    SkipByConflict = 1,
    Unavailable = 2,
}

public sealed record RestorePlanEntry(
    string RelativePath,
    string TargetFullPath,
    long Length,
    DateTime FileTimeUtc,
    string? ContentHash,
    bool TargetExists,
    RestorePlanAction Action,
    string? UnavailableReason = null,
    string? FinalTargetPath = null);

/// <summary>
/// 还原预演结果。设计文档 06 §4.4：执行前必须能列出"将写入哪些文件、冲突有哪些"。
/// </summary>
/// <remarks>
/// <b>对设计文档的一处细化</b>：<see cref="Entries"/> 只承载**需要注意的条目**
/// （冲突与不可恢复），而不是把每一个"将要写入"的文件都列出来。
/// 理由是整快照还原在百万文件下会让计划本身就把内存撑爆，而 UI 其实只需要
/// "写多少个 / 多少字节 / 哪些冲突 / 哪些不可恢复"。
/// 条目数少于 <see cref="FullListingThreshold"/> 时才把全部条目都带上。
/// <para>
/// <see cref="IRestoreService.ExecuteAsync"/> **不依赖** <see cref="Entries"/> 来做实际工作——
/// 它会按 <see cref="Request"/> 流式重新选出目标，再套用策略。
/// 这样计划是"给人看的快照"，不是"必须与执行严格一致的契约"，避免两者漂移。
/// </para>
/// </remarks>
public sealed record RestorePlan(
    RestoreRequest Request,
    IReadOnlyList<RestorePlanEntry> Entries,
    int TotalCount,
    int PlannedCount,
    int ConflictCount,
    int UnavailableCount,
    long PlannedBytes)
{
    /// <summary>条目数不超过此值时才把全部条目塞进 <see cref="Entries"/>。</summary>
    public const int FullListingThreshold = 5000;

    public bool EntriesTruncated => Entries.Count < TotalCount;

    /// <summary>不可恢复的文件清单（UI 要先列出，让用户选择"是否还原其余文件"，**不要静默跳过**）。</summary>
    public IReadOnlyList<RestorePlanEntry> UnavailableEntries =>
        [.. Entries.Where(e => e.Action == RestorePlanAction.Unavailable)];

    public IReadOnlyList<RestorePlanEntry> ConflictEntries =>
        [.. Entries.Where(e => e.Action == RestorePlanAction.SkipByConflict)];
}

public sealed record RestoreFailure(string RelativePath, string Reason);

/// <summary>还原结果汇总。设计文档 04 §1.5：成功后给出 成功/跳过/失败 各多少，附失败清单。</summary>
public sealed record RestoreResult(
    int RestoredCount,
    int SkippedCount,
    int FailedCount,
    int UnavailableCount,
    long RestoredBytes,
    IReadOnlyList<RestoreFailure> Failures)
{
    public static RestoreResult Empty => new(0, 0, 0, 0, 0, []);
}
