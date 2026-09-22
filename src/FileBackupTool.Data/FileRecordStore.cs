using System.Runtime.CompilerServices;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.Data;

/// <summary>
/// <see cref="BackupFile"/> 的查询与批量写入。设计文档 02 §3.1、03 §3.3、§4、§8。
/// </summary>
public sealed class FileRecordStore(IDbContextFactory<AppDbContext> factory)
{
    private readonly IDbContextFactory<AppDbContext> _factory = factory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    /// <summary>分块大小，避免 SQLite 参数上限与超大 IN 列表。</summary>
    private const int ChunkSize = 800;

    /// <summary>
    /// <c>Length</c> 预筛（设计文档 03 §3.3）：在给定的长度里，哪些**已经有非空哈希的记录**。
    /// </summary>
    /// <remarks>
    /// <b>刻意不过滤软删除的快照。</b> 软删除的记录仍然算有效引用，
    /// 它们引用的 blob 仍在池里——所以那些长度依然"无法证明唯一"，
    /// 必须让调用方走"先算哈希"的保守路径。
    /// </remarks>
    public async Task<HashSet<long>> GetKnownLengthsAsync(
        Guid taskId,
        IReadOnlyCollection<long> lengths,
        CancellationToken ct)
    {
        var result = new HashSet<long>();
        if (lengths.Count == 0)
        {
            return result;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        foreach (long[] chunk in lengths.Distinct().Chunk(ChunkSize))
        {
            List<long> found = await db.Files
                .Where(f => f.TaskId == taskId && f.ContentHash != null && chunk.Contains(f.Length))
                .Select(f => f.Length)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (long l in found)
            {
                result.Add(l);
            }
        }

        return result;
    }

    /// <summary>
    /// 是否有**任何**记录（含软删除的记录）引用这个 blob。设计文档 02 §5.1 / 03 §8 的回收判定。
    /// </summary>
    public async Task<bool> AnyReferenceToBlobAsync(Guid taskId, string hash, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Files
            .AnyAsync(f => f.TaskId == taskId && f.ContentHash == hash, ct)
            .ConfigureAwait(false);
    }

    /// <summary>批量版：给定一批哈希，返回其中被引用的那些。GC 用。</summary>
    public async Task<HashSet<string>> GetReferencedBlobsAsync(
        Guid taskId,
        IReadOnlyCollection<string> hashes,
        CancellationToken ct)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (hashes.Count == 0)
        {
            return result;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (string[] chunk in hashes.Chunk(ChunkSize))
        {
            List<string> found = await db.Files
                .Where(f => f.TaskId == taskId && f.ContentHash != null && chunk.Contains(f.ContentHash))
                .Select(f => f.ContentHash!)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (string h in found)
            {
                result.Add(h);
            }
        }

        return result;
    }

    /// <summary>
    /// 枚举"活着的引用"（<c>ContentHash</c> 非空、且所属快照为 Completed 且未删除）。
    /// 完整性校验用。**流式**，不物化。
    /// </summary>
    public async IAsyncEnumerable<(string Hash, string RelativePath, long Length)> EnumerateLiveReferencesAsync(
        Guid taskId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = from f in db.Files
                    join s in db.Snapshots on f.SnapshotId equals s.Id
                    where f.TaskId == taskId
                          && f.ContentHash != null
                          && !s.IsDeleted
                          && s.Status == SnapshotStatus.Completed
                    select new { Hash = f.ContentHash!, f.RelativePath, f.Length };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return (row.Hash, row.RelativePath, row.Length);
        }
    }

    /// <summary>
    /// 取链上、<b>指定路径前缀内</b>的全部记录。设计文档 02 §3.1 的镜像重建。
    /// 只对这个小集合在内存里 replay——一个目录下通常几百到几万条，不是百万。
    /// </summary>
    public async Task<List<BackupFile>> GetChainRecordsInPrefixAsync(
        Guid taskId,
        IReadOnlyList<int> chainSnapshotIds,
        string? relativePathKeyPrefix,
        CancellationToken ct)
    {
        if (chainSnapshotIds.Count == 0)
        {
            return [];
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        IQueryable<BackupFile> query = db.Files
            .Where(f => f.TaskId == taskId && chainSnapshotIds.Contains(f.SnapshotId) && !f.IsDeleted);

        if (!string.IsNullOrEmpty(relativePathKeyPrefix))
        {
            string key = relativePathKeyPrefix;
            query = query.Where(f => f.RelativePathKey == key || f.RelativePathKey.StartsWith(key + "\\"));
        }

        List<BackupFile> rows = await query.OrderBy(f => f.SnapshotId).ToListAsync(ct).ConfigureAwait(false);

        // 内存里再精确过滤一次：EF 把 StartsWith 翻译成 LIKE，而路径里出现 `%` / `_` 时
        // LIKE 的通配符会过度匹配。候选集只有几百到几万条，这一遍几乎免费。
        if (!string.IsNullOrEmpty(relativePathKeyPrefix))
        {
            string prefix = relativePathKeyPrefix + "\\";
            rows = [.. rows.Where(f =>
                string.Equals(f.RelativePathKey, relativePathKeyPrefix, StringComparison.Ordinal) ||
                f.RelativePathKey.StartsWith(prefix, StringComparison.Ordinal))];
        }

        return rows;
    }

    /// <summary>某个路径的全部版本（文件历史）。设计文档 06 §4.3。</summary>
    /// <remarks>
    /// **必须对 <paramref name="chainSnapshotIds"/> 分块。** 调用方
    /// （<c>MirrorQueryService.GetFileHistoryAsync</c>）传进来的是"该任务**全部**已完成快照的 id"，
    /// 它随快照数单调增长；而 EF 会把 <c>Contains</c> 的集合**逐元素内联成 SQL 参数**
    /// （实测：10 个元素 → 899 字符，5000 个元素 → 307045 字符，见
    /// <c>SqlParameterizationTests</c>）。一旦超过 SQLite 的 32766 个参数上限，
    /// 这个端点会以 <c>too many SQL variables</c> **直接失败**，而不是变慢——
    /// 长期开着实时监控的任务（每 30 次增量一次全量）迟早会走到这一步。
    /// 同文件其它方法（<see cref="GetRecordsByPathKeysAsync"/> 等）本来就在分块，只有这里漏了。
    /// </remarks>
    public async Task<List<BackupFile>> GetFileHistoryAsync(
        Guid taskId,
        string relativePathKey,
        IReadOnlyList<int> chainSnapshotIds,
        CancellationToken ct)
    {
        var result = new List<BackupFile>();
        if (chainSnapshotIds.Count == 0)
        {
            return result;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // 逐块查、顺序追加即得全局升序：调用方传的 id 是升序的（GetCompletedAsync 按 Id 排序），
        // 每块内部又 OrderBy(SnapshotId)，所以追加结果与"不分块一次查完"的顺序一致。
        foreach (int[] chunk in chainSnapshotIds.Chunk(ChunkSize))
        {
            List<BackupFile> rows = await db.Files
                .Where(f => f.TaskId == taskId
                            && f.RelativePathKey == relativePathKey
                            && chunk.Contains(f.SnapshotId)
                            && !f.IsDeleted)
                .OrderBy(f => f.SnapshotId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            result.AddRange(rows);
        }

        return result;
    }

    /// <summary>按路径精确取某个快照镜像里的记录（还原用）。</summary>
    public async Task<List<BackupFile>> GetRecordsByPathKeysAsync(
        Guid taskId,
        IReadOnlyList<int> chainSnapshotIds,
        IReadOnlyCollection<string> relativePathKeys,
        CancellationToken ct)
    {
        var result = new List<BackupFile>();
        if (chainSnapshotIds.Count == 0 || relativePathKeys.Count == 0)
        {
            return result;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (string[] chunk in relativePathKeys.Chunk(ChunkSize))
        {
            List<BackupFile> rows = await db.Files
                .Where(f => f.TaskId == taskId
                            && chainSnapshotIds.Contains(f.SnapshotId)
                            && !f.IsDeleted
                            && chunk.Contains(f.RelativePathKey))
                .OrderBy(f => f.SnapshotId)
                .ToListAsync(ct)
                .ConfigureAwait(false);
            result.AddRange(rows);
        }

        return result;
    }

    /// <summary>链上所有涉及过的路径 key（用于整快照还原时确定要还原哪些文件）。</summary>
    public async IAsyncEnumerable<string> EnumerateChainPathKeysAsync(
        Guid taskId,
        IReadOnlyList<int> chainSnapshotIds,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (chainSnapshotIds.Count == 0)
        {
            yield break;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var query = db.Files
            .Where(f => f.TaskId == taskId && chainSnapshotIds.Contains(f.SnapshotId) && !f.IsDeleted)
            .Select(f => f.RelativePathKey)
            .Distinct();

        await foreach (string key in query.AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            yield return key;
        }
    }

    /// <summary>
    /// 取这些哈希的一个代表引用路径（供回收账本记录"这曾经是什么文件"）。
    /// 账本必须带上这个信息——否则用户在文件系统里只看到 <c>ab/cd/&lt;64位哈希&gt;</c>，
    /// 完全无从判断这是什么、能不能删，"待用户手动删除"就变成"待用户盲删"。
    /// </summary>
    public async Task<Dictionary<string, (string Path, long Length)>> GetBlobRepresentativesAsync(
        Guid taskId,
        IReadOnlyCollection<string> hashes,
        CancellationToken ct)
    {
        var result = new Dictionary<string, (string, long)>(StringComparer.Ordinal);
        if (hashes.Count == 0)
        {
            return result;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (string[] chunk in hashes.Chunk(ChunkSize))
        {
            List<BackupFile> rows = await db.Files
                .Where(f => f.TaskId == taskId && f.ContentHash != null && chunk.Contains(f.ContentHash))
                .OrderByDescending(f => f.Id)
                .Select(f => new BackupFile
                {
                    ContentHash = f.ContentHash,
                    RelativePath = f.RelativePath,
                    Length = f.Length,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (BackupFile row in rows)
            {
                if (row.ContentHash is not null && !result.ContainsKey(row.ContentHash))
                {
                    result[row.ContentHash] = (row.RelativePath, row.Length);
                }
            }
        }

        return result;
    }

    public async Task<(long Count, long Bytes)> GetTotalsAsync(
        IReadOnlyCollection<int> snapshotIds,
        CancellationToken ct)
    {
        if (snapshotIds.Count == 0)
        {
            return (0, 0);
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        long count = await db.Files.LongCountAsync(f => snapshotIds.Contains(f.SnapshotId), ct)
            .ConfigureAwait(false);
        long bytes = count == 0
            ? 0
            : await db.Files
                .Where(f => snapshotIds.Contains(f.SnapshotId) && f.Type != FileRecordType.Deleted)
                .SumAsync(f => (long?)f.Length, ct)
                .ConfigureAwait(false) ?? 0;

        return (count, bytes);
    }

    /// <summary>
    /// 分批写入文件记录。设计文档 01 §4.2：**不能**把整个快照的记录攒在 EF change tracker 里。
    /// </summary>
    /// <remarks>
    /// 用 EF 的 <c>AddRange</c> + <c>SaveChanges</c> + <c>ChangeTracker.Clear()</c>，
    /// 而不是裸 SQL——保持与模型/值转换一致（尤其是 DateTime 的存储格式），
    /// 否则镜像物化的 TEXT 比较会与写入格式悄悄不一致。
    /// </remarks>
    public async Task WriteBatchAsync(IReadOnlyList<BackupFile> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        // 备份作业内部是串行的，但作业与"完整性校验/GC"可能并发；写路径加个门更容易推理。
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
            await db.Files.AddRangeAsync(records, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            db.ChangeTracker.Clear();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>把一个快照的计数/字节数/失败数回写。设计文档 03 §3 的"提交"阶段。</summary>
    public async Task FinalizeSnapshotAsync(BackupSnapshot snapshot, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Snapshots.Update(snapshot);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
