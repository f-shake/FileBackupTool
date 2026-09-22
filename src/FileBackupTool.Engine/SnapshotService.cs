using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine;

/// <summary>
/// 快照管理。设计文档 04 §2。
/// </summary>
public sealed class SnapshotService(
    SnapshotChainService chains,
    FileRecordStore fileRecords,
    IClock clock,
    IBackupLogSink logs,
    ILogger<SnapshotService> logger) : ISnapshotService
{
    private readonly SnapshotChainService _chains = chains;
    private readonly FileRecordStore _fileRecords = fileRecords;
    private readonly IClock _clock = clock;
    private readonly IBackupLogSink _logs = logs;
    private readonly ILogger<SnapshotService> _logger = logger;

    public async Task<PagedResult<SnapshotGroup>> ListAsync(
        Guid taskId,
        SnapshotType? typeFilter,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        List<BackupSnapshot> all = await _chains.GetVisibleAsync(taskId, ct).ConfigureAwait(false);

        // 按全量快照分组：每个 Full / VirtualFull 开一个新组，其后所有 Increment 归入该组
        var groups = new List<SnapshotGroup>();
        List<BackupSnapshot> current = [];

        foreach (BackupSnapshot snapshot in all)
        {
            if (snapshot.IsFull && current.Count > 0)
            {
                groups.Add(new SnapshotGroup(current[0], [.. current.Skip(1)]));
                current = [];
            }

            current.Add(snapshot);
        }

        if (current.Count > 0)
        {
            groups.Add(new SnapshotGroup(current[0], [.. current.Skip(1)]));
        }

        // 筛选：类型/日期。全量快照本身被筛掉时整组消失。
        IEnumerable<SnapshotGroup> filtered = groups;

        if (typeFilter.HasValue)
        {
            filtered = filtered.Where(g =>
                g.Full.Type == typeFilter.Value || g.Increments.Any(i => i.Type == typeFilter.Value));
        }

        if (fromUtc.HasValue)
        {
            filtered = filtered.Where(g => g.Full.BeginTime >= fromUtc.Value
                                           || g.Increments.Any(i => i.BeginTime >= fromUtc.Value));
        }

        if (toUtc.HasValue)
        {
            filtered = filtered.Where(g => g.Full.BeginTime <= toUtc.Value);
        }

        // 按 Id 倒序（最新在前）。Id 序就是创建序 = 时间序，而且在 BeginTime 相同的情况下
        // 仍然是**确定**的——用 BeginTime 排序遇到同时刻快照会得到不稳定的顺序。
        List<SnapshotGroup> list = [.. filtered.OrderByDescending(g => g.Full.Id)];

        int p = page < 1 ? 1 : page;
        int size = pageSize is < 1 or > 500 ? 50 : pageSize;
        int skip = (p - 1) * size;

        List<SnapshotGroup> pageItems = skip >= list.Count
            ? []
            : [.. list.Skip(skip).Take(size)];

        return new PagedResult<SnapshotGroup>(pageItems, list.Count, p, size);
    }

    public async Task<DeleteImpact?> PreviewDeleteAsync(int snapshotId, CancellationToken ct)
    {
        (BackupSnapshot target, List<BackupSnapshot> affected)? result =
            await _chains.ComputeCascadeDeleteAsync(snapshotId, ct).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        (BackupSnapshot targetSnapshot, List<BackupSnapshot> affected) = result.Value;
        List<int> ids = [.. affected.Select(s => s.Id)];
        (long count, long bytes) = await _fileRecords.GetTotalsAsync(ids, ct).ConfigureAwait(false);

        return new DeleteImpact(targetSnapshot, affected, (int)count, bytes);
    }

    public async Task DeleteAsync(int snapshotId, CancellationToken ct)
    {
        (BackupSnapshot target, List<BackupSnapshot> affected)? result =
            await _chains.ComputeCascadeDeleteAsync(snapshotId, ct).ConfigureAwait(false);

        if (result is null)
        {
            throw new InvalidOperationException($"找不到快照 #{snapshotId}");
        }

        (BackupSnapshot targetSnapshot, List<BackupSnapshot> affected) = result.Value;
        List<int> ids = [.. affected.Select(s => s.Id)];

        await _chains.MarkDeletedAsync(ids, _clock.UtcNow, ct).ConfigureAwait(false);

        _logger.LogInformation("删除快照 #{Id}，级联影响 {Count} 个快照。", snapshotId, ids.Count);

        await _logs.WriteAsync(targetSnapshot.TaskId, snapshotId, Microsoft.Extensions.Logging.LogLevel.Warning,
            $"删除快照 #{snapshotId}，级联删除了 {ids.Count} 个快照：{string.Join(", ", ids.OrderBy(i => i))}。",
            null, ct).ConfigureAwait(false);

        // 注意：**物理空间不立即释放**。删除是软删除；blob 只有在"没有任何记录（含软删除记录）引用"
        // 时才移入回收站。所以删快照后回收站占用会增长，UI 需要展示。
    }
}
