using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Snapshots;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.Data;

/// <summary>
/// 快照链的读取与决策。设计文档 03 §2 / §4，04 §2.2。
/// </summary>
/// <remarks>
/// **查询一律只看 <c>Status == Completed</c>**——Aborted 快照里可能有半截记录，
/// 让它参与镜像重建会把不一致传染给后续快照。
/// </remarks>
public sealed class SnapshotChainService(IDbContextFactory<AppDbContext> factory)
{
    private readonly IDbContextFactory<AppDbContext> _factory = factory;

    public async Task<List<BackupSnapshot>> GetCompletedAsync(Guid taskId, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Snapshots
            .Where(s => s.TaskId == taskId && !s.IsDeleted && s.Status == SnapshotStatus.Completed)
            .OrderBy(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>任务的全部快照（含 Aborted，不含软删除），按 Id 升序（= 创建顺序 = 链序）。用于列表与删除预演。</summary>
    public async Task<List<BackupSnapshot>> GetVisibleAsync(Guid taskId, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Snapshots
            .Where(s => s.TaskId == taskId && !s.IsDeleted)
            .OrderBy(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<BackupSnapshot?> GetAsync(int snapshotId, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Snapshots.FirstOrDefaultAsync(s => s.Id == snapshotId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 解析镜像链。<c>[最近一次 Full/VirtualFull, 其后所有 Increment …, 该快照本身]</c>。
    /// </summary>
    /// <returns>链断裂（某条增量找不到它所属的全量）时返回 null。</returns>
    public async Task<List<BackupSnapshot>?> ResolveChainAsync(Guid taskId, int snapshotId, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        BackupSnapshot? target = await db.Snapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        List<BackupSnapshot> completed = await GetCompletedAsync(taskId, ct).ConfigureAwait(false);

        // 目标自身可能是 Aborted / InProgress；镜像只应由 Completed 组成，
        // 所以以"该快照之前的最后一个 Completed 状态"作为链尾。
        BackupSnapshot? tail = completed.LastOrDefault(s => s.Id == snapshotId)
                              ?? completed.LastOrDefault(s => s.Id <= target.Id);

        if (tail is null)
        {
            return null;
        }

        List<BackupSnapshot>? chain = SnapshotChainPolicy.ResolveChain(completed, tail);
        if (chain is null)
        {
            return null;
        }

        // 链序必须与 SnapshotId 顺序一致——物化镜像的 SQL 依赖这条不变量（按 SnapshotId DESC 取最新）。
        // 违背它不会报错，只会**静默产生错误的镜像**，所以这里显式校验。
        for (int i = 1; i < chain.Count; i++)
        {
            if (chain[i].Id <= chain[i - 1].Id)
            {
                return null;
            }
        }

        return chain;
    }

    /// <summary>级联删除的影响范围。设计文档 04 §2.2：对话框**必须**列出这些快照。</summary>
    public async Task<(BackupSnapshot Target, List<BackupSnapshot> Affected)?> ComputeCascadeDeleteAsync(
        int snapshotId,
        CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        BackupSnapshot? target = await db.Snapshots
            .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
            .ConfigureAwait(false);

        if (target is null)
        {
            return null;
        }

        List<BackupSnapshot> all = await db.Snapshots
            .Where(s => s.TaskId == target.TaskId && !s.IsDeleted)
            .OrderBy(s => s.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return (target, SnapshotChainPolicy.ComputeCascadeDelete(all, target));
    }

    /// <summary>决定下一次应当跑哪种备份。设计文档 03 §2。</summary>
    public async Task<SnapshotType> DecideNextTypeAsync(BackupTask task, CancellationToken ct)
    {
        List<BackupSnapshot> completed = await GetCompletedAsync(task.Id, ct).ConfigureAwait(false);
        return SnapshotChainPolicy.DecideNextType(completed, task.IsDefaultVirtualBackup, task.MaxAutoIncrementBackupCount);
    }

    /// <summary>
    /// 启动自愈：把残留的 <c>InProgress</c> 快照全部标记为 <c>Aborted</c>。
    /// 设计文档 03 §7.4 / 08 §1.3——**这条路径没有测试就等于没有实现**。
    /// </summary>
    /// <returns>被修正的快照 id。</returns>
    public async Task<List<int>> AbortStaleInProgressAsync(string reason, DateTime nowUtc, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        List<BackupSnapshot> stale = await db.Snapshots
            .Where(s => s.Status == SnapshotStatus.InProgress)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (stale.Count == 0)
        {
            return [];
        }

        foreach (BackupSnapshot s in stale)
        {
            s.Status = SnapshotStatus.Aborted;
            s.EndTime = nowUtc;
            s.AbortReason = reason;
        }

        db.Snapshots.UpdateRange(stale);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return [.. stale.Select(s => s.Id)];
    }

    /// <summary>插入一条新快照并回填 Id。</summary>
    public async Task CreateAsync(BackupSnapshot snapshot, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>软删除一批快照（级联删除的落库部分）。</summary>
    public async Task MarkDeletedAsync(IReadOnlyCollection<int> snapshotIds, DateTime nowUtc, CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
        {
            return;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        List<BackupSnapshot> snapshots = await db.Snapshots
            .Where(s => snapshotIds.Contains(s.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (BackupSnapshot s in snapshots)
        {
            s.IsDeleted = true;
            s.DeletedAt = nowUtc;
        }

        db.Snapshots.UpdateRange(snapshots);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
