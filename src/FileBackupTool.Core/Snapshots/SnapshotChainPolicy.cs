using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Snapshots;

/// <summary>全量 / 增量的选取决策与快照链计算。设计文档 03 §2 / §4、04 §2.2。</summary>
/// <remarks>
/// <b>链上比较一律用 <c>SnapshotId</c>，不用 <c>BeginTime</c>。</b>
/// <para>
/// 同一任务的快照 Id 按创建顺序自增，而创建顺序就是链序——这是
/// <c>PreviousMirrorTable</c> 物化镜像时依赖的不变量（它按 <c>SnapshotId DESC</c> 取每路径最新记录）。
/// </para>
/// <para>
/// 反过来，用 <c>BeginTime</c> 做区间过滤是脆的：一旦两个快照的 <c>BeginTime</c> 相等
/// （时钟粒度、或备份快得离谱），`BeginTime &gt; full.BeginTime` 这类判断就会静默把目标快照排除在链外，
/// 表现为"墓碑写了但镜像没变"。这是实测踩到的 bug，所以固定成 Id。
/// </para>
/// </remarks>
public static class SnapshotChainPolicy
{
    /// <summary>
    /// 决定下一次备份应当跑什么类型。
    /// </summary>
    /// <remarks>
    /// <code>
    /// 若 不存在任何 Full/VirtualFull 快照
    ///     → 跑 Full（或 VirtualFull，若 task.IsDefaultVirtualBackup）
    /// 否则
    ///     设 full = 最近一次 Full/VirtualFull
    ///     设 n = full 之后（Id 更大）的快照总数
    ///     若 n > task.MaxAutoIncrementBackupCount → 跑 Full（或 VirtualFull）
    ///     否则 → 跑 Increment
    /// </code>
    /// 这是一个**保证增量链不会过长**的模型，与 cron 的"在特定时刻跑"正交。
    /// <para>
    /// **实时触发不改变这个逻辑**——它只是把"该跑增量了"这件事**提前**。
    /// 否则实时备份会退化成"永远增量、永不 full"。
    /// </para>
    /// </remarks>
    public static SnapshotType DecideNextType(
        IEnumerable<BackupSnapshot> completedSnapshots,
        bool isDefaultVirtual,
        int maxAutoIncrementBackupCount)
    {
        SnapshotType fullType = isDefaultVirtual ? SnapshotType.VirtualFull : SnapshotType.Full;

        BackupSnapshot? latestFull = completedSnapshots
            .Where(s => s.IsFull)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();

        if (latestFull is null)
        {
            return fullType;
        }

        int incrementsSinceFull = completedSnapshots.Count(s => s.Id > latestFull.Id);
        return incrementsSinceFull > maxAutoIncrementBackupCount ? fullType : SnapshotType.Increment;
    }

    /// <summary>
    /// 解析某个快照所属的镜像链：<c>[最近一次 Full/VirtualFull, 其后所有 Increment …, 该快照本身]</c>。
    /// </summary>
    /// <returns>按 Id 升序的链。若找不到其所属的全量快照则返回 null（链断裂）。</returns>
    public static List<BackupSnapshot>? ResolveChain(
        IEnumerable<BackupSnapshot> completedSnapshots,
        BackupSnapshot target)
    {
        if (target.IsFull)
        {
            return [target];
        }

        List<BackupSnapshot> all = [.. completedSnapshots.OrderBy(s => s.Id)];

        BackupSnapshot? full = all
            .Where(s => s.IsFull && s.Id != target.Id && s.Id <= target.Id)
            .OrderByDescending(s => s.Id)
            .FirstOrDefault();

        if (full is null)
        {
            return null;
        }

        List<BackupSnapshot> chain = [full];
        chain.AddRange(all.Where(s => !s.IsFull && s.Id > full.Id && s.Id <= target.Id));
        return chain;
    }

    /// <summary>
    /// 级联删除的影响范围。设计文档 04 §2.2。
    /// </summary>
    /// <remarks>
    /// **语义不变，无法回避**——增量链一旦断裂，其后的增量就无法 replay。
    /// 改进的是**交互**：对话框必须列出受影响的全部快照，而不是旧设计那样只说一句
    /// 「删除此快照，将同步删除后续的增量快照」。
    /// <para>范围 = <c>[target, 下一个 Full/VirtualFull)</c>。</para>
    /// </remarks>
    public static List<BackupSnapshot> ComputeCascadeDelete(
        IEnumerable<BackupSnapshot> snapshots,
        BackupSnapshot target)
    {
        List<BackupSnapshot> ordered = [.. snapshots.OrderBy(s => s.Id)];

        BackupSnapshot? nextFull = ordered
            .Where(s => s.IsFull && s.Id > target.Id)
            .OrderBy(s => s.Id)
            .FirstOrDefault();

        return
        [
            .. ordered.Where(s =>
                s.Id >= target.Id &&
                (nextFull is null || s.Id < nextFull.Id))
        ];
    }
}
