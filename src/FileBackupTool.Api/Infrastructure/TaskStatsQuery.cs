using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 任务列表/详情需要的实时派生量。
/// </summary>
/// <remarks>
/// <see cref="LatestSnapshot"/> 是"最近一条快照（含中止的）"，用于推导
/// 上次备份结果与"信息"列；<see cref="ValidSnapshotCount"/> 只数 <c>Completed</c>。
/// 两者的区别是有意的：用户需要看到"上次跑失败了"，而不只是"成功过几次"。
/// </remarks>
public sealed record TaskRuntimeStats(
    int SnapshotCount,
    int ValidSnapshotCount,
    DateTime? LastBackupUtc,
    DateTime? LastFullBackupUtc,
    BackupSnapshot? LatestSnapshot,
    int InProgressCount,
    SnapshotType? InProgressType);

/// <summary>
/// 批量取任务实时状态（设计文档 06 §4.1 的"含实时状态、上次备份时间、快照数"）。
/// </summary>
/// <remarks>
/// <para>
/// <b>刻意用聚合 SQL 而不是逐个任务调 <c>GetVisibleAsync</c></b>：后者会把每个任务的全部
/// 快照行都读进内存，一台跑了两年的机器上那是几十万行——只为了算出 4 个数字。
/// 这里两个 <c>GROUP BY</c> 查询就能拿到全部任务的结果。
/// </para>
/// <para>
/// <b>全部以数据库为准</b>，不读进程内字段：设计文档 01 §4.1 要求"进程重启后状态可恢复"，
/// 而唯一能跨重启成立的事实来源就是库里的快照记录。
/// </para>
/// </remarks>
public sealed class TaskStatsQuery(IDbContextFactory<AppDbContext> dbFactory)
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;

    public async Task<Dictionary<Guid, TaskRuntimeStats>> LoadAsync(CancellationToken ct)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // ① 每个任务的计数与最近时间。
        //    写成一堆三元表达式而不是 g.Where(...).Max(...)：后者在 EF 里要么不翻译、
        //    要么翻成相关子查询（每条任务一次），而 CASE WHEN 聚合是单次扫描。
        var aggregates = await db.Snapshots
            .Where(s => !s.IsDeleted)
            .GroupBy(s => s.TaskId)
            .Select(g => new
            {
                TaskId = g.Key,
                Count = g.Count(),
                Valid = g.Count(s => s.Status == SnapshotStatus.Completed),
                LastBackup = g.Max(s => s.Status == SnapshotStatus.Completed ? s.BeginTime : (DateTime?)null),
                LastFull = g.Max(
                    s => s.Status == SnapshotStatus.Completed &&
                         (s.Type == SnapshotType.Full || s.Type == SnapshotType.VirtualFull)
                        ? s.BeginTime
                        : (DateTime?)null),
                LatestId = g.Max(s => s.Id),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (aggregates.Count == 0)
        {
            return [];
        }

        // ② 最近一条快照实体（Id 全局递增，所以 Max(Id) 就是最新那条）。
        int[] latestIds = [.. aggregates.Select(a => a.LatestId)];
        List<BackupSnapshot> latest = await db.Snapshots
            .Where(s => latestIds.Contains(s.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Dictionary<int, BackupSnapshot> latestById = latest.ToDictionary(s => s.Id);

        // ③ 进行中的快照（正常最多一条，且很快消失）。单独查是因为它的类型决定
        //    前端显示的"正在全量/增量备份"——而它的状态在聚合里被 COUNT 掉了。
        var inProgress = await db.Snapshots
            .Where(s => !s.IsDeleted && s.Status == SnapshotStatus.InProgress)
            .Select(s => new { s.TaskId, s.Type })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        Dictionary<Guid, (int Count, SnapshotType Type)> inProgressByTask = [];
        foreach (var row in inProgress)
        {
            inProgressByTask.TryGetValue(row.TaskId, out (int Count, SnapshotType Type) existing);
            inProgressByTask[row.TaskId] = (existing.Count + 1, row.Type);
        }

        Dictionary<Guid, TaskRuntimeStats> result = [];

        foreach (var row in aggregates)
        {
            inProgressByTask.TryGetValue(row.TaskId, out (int Count, SnapshotType Type) running);

            result[row.TaskId] = new TaskRuntimeStats(
                row.Count,
                row.Valid,
                row.LastBackup,
                row.LastFull,
                latestById.GetValueOrDefault(row.LatestId),
                running.Count,
                running.Count > 0 ? running.Type : null);
        }

        return result;
    }
}
