using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Engine;

/// <summary>
/// 完整校验与 blob 回收。设计文档 03 §8 / 04 §2.4。
/// </summary>
/// <remarks>
/// <b>回收判定</b>：一个 blob 可回收 ⟺ <b>没有任何记录（含软删除的记录）引用它</b>。
/// <para>
/// 这修正了旧设计的一个矛盾：旧代码的 GC 过滤 <c>IsDeleted == false</c>，
/// 导致软删除的快照其 blob 会被物理删除——"软删除"名不副实，且<b>比没有更危险</b>
/// （用户以为能救回来，其实不能）。
/// </para>
/// <para>
/// <b>识别方式</b>按<b>哈希格式</b>，不是旧实现那种"文件名长度 == 32"的启发式
/// （会误伤用户自有的 32 字符文件名）。
/// </para>
/// </remarks>
public sealed class GarbageCollector(
    TaskStore tasks,
    SnapshotChainService chains,
    FileRecordStore fileRecords,
    BlobStoreFactory blobStores,
    IHashService hash,
    IFileSystem fs,
    IClock clock,
    IOptions<StorageOptions> storageOptions,
    ILogger<GarbageCollector> logger) : IIntegrityService
{
    private readonly TaskStore _tasks = tasks;
    private readonly SnapshotChainService _chains = chains;
    private readonly FileRecordStore _fileRecords = fileRecords;
    private readonly BlobStoreFactory _blobStores = blobStores;
    private readonly IHashService _hash = hash;
    private readonly IFileSystem _fs = fs;
    private readonly IClock _clock = clock;
    private readonly StorageOptions _storage = storageOptions.Value;

    public async Task<IntegrityReport> VerifyAsync(Guid taskId, bool deepHashCheck, CancellationToken ct)
    {
        BackupTask task = await GetTaskAsync(taskId, ct).ConfigureAwait(false);
        PhysicalBlobStore blobStore = _blobStores.Create(task.BackupDir);

        var redundant = new List<string>();
        var lost = new List<string>();
        var corrupted = new List<string>();
        int checkedBlobs = 0;
        int checkedRecords = 0;

        // 池中所有哈希。百万级下这个集合本身就要 ~90MB —— 手工"完整校验"接受这个代价。
        var pool = new HashSet<string>(StringComparer.Ordinal);
        foreach (string h in blobStore.EnumerateBlobHashes())
        {
            ct.ThrowIfCancellationRequested();
            pool.Add(h);
        }

        // 多余：池里有，但没有任何记录（含软删除的记录）引用
        foreach (string[] chunk in pool.Chunk(2000))
        {
            ct.ThrowIfCancellationRequested();
            HashSet<string> referenced = await _fileRecords.GetReferencedBlobsAsync(taskId, chunk, ct)
                .ConfigureAwait(false);

            foreach (string h in chunk)
            {
                checkedBlobs++;
                if (!referenced.Contains(h))
                {
                    redundant.Add(h);
                }
            }
        }

        // 丢失：有记录引用，但 blob 既不在池中也不在回收站中。**会导致还原失败，必须显著告警。**
        var recycleBin = new HashSet<string>(blobStore.EnumerateRecycleBinHashes(), StringComparer.Ordinal);
        var reportedLost = new HashSet<string>(StringComparer.Ordinal);

        await foreach ((string h, string path, long _) in
                       _fileRecords.EnumerateLiveReferencesAsync(taskId, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            checkedRecords++;

            if (!pool.Contains(h) && !recycleBin.Contains(h) && reportedLost.Add(h))
            {
                lost.Add(h);
                logger.LogWarning("blob 丢失：{Hash}（例如 {Path}）", h, path);
            }
        }

        // 链连续性
        var broken = new List<string>();
        List<BackupSnapshot> completed = await _chains.GetCompletedAsync(taskId, ct).ConfigureAwait(false);
        foreach (BackupSnapshot snapshot in completed)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshot.Type != SnapshotType.Increment)
            {
                continue;
            }

            List<BackupSnapshot>? chain = await _chains.ResolveChainAsync(taskId, snapshot.Id, ct).ConfigureAwait(false);
            if (chain is null)
            {
                broken.Add($"快照 #{snapshot.Id}（{snapshot.BeginTime:u}）找不到其所属的全量快照，镜像不可 replay。");
            }
        }

        // 深度校验：抽样重算哈希（TB 级重算极慢，所以默认关闭，需显式勾选）
        if (deepHashCheck)
        {
            foreach (string h in pool)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using Stream stream = blobStore.OpenRead(h);
                    string actual = await _hash.ComputeHashAsync(stream, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, h, StringComparison.Ordinal))
                    {
                        corrupted.Add(h);
                        logger.LogError("blob 内容与文件名不符：{Expected} != {Actual}", h, actual);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    corrupted.Add(h);
                    logger.LogWarning(ex, "读取 blob 失败：{Hash}", h);
                }
            }
        }

        return new IntegrityReport(taskId, redundant, lost, broken, corrupted, [], checkedBlobs, checkedRecords);
    }

    public async Task<GcResult> CollectAsync(
        Guid taskId,
        IReadOnlyCollection<string>? scopeHashes,
        CancellationToken ct)
    {
        BackupTask task = await GetTaskAsync(taskId, ct).ConfigureAwait(false);
        PhysicalBlobStore blobStore = _blobStores.Create(task.BackupDir);

        // 候选集合：`null` = 全池（手动回收）；非 null = 只检查这些哈希（增量回收）。
        //
        // **判据必须是 `is null`，不能是 `is { Count: > 0 }`**。备份尾巴传进来的
        // `ScopeHashes` 是个集合，一次"什么都没变"的增量备份（6 小时兜底扫描扫完没发现
        // 变化、或监控触发但内容已稳定）它就是**空集合**：非 null 但 Count == 0。
        // 用 Count 判据的话这种备份会掉进全池分支——枚举整个 blob 池（百万级文件）、
        // 每个 2000 一批地查引用，正好是 03 §8 与接口注释要避免的那件事；
        // 而作业队列是**单消费者串行**的，这段开销会让后面排队的备份/还原一起等。
        // 空集合的正确含义是"本次没有可能变成垃圾的 blob"，也就是什么都不用查。
        //
        // 给定的范围来自调用方，**必须过滤掉非法哈希**——否则一个手滑传进来的字符串
        // 会让整个回收以 ArgumentException 结束，而回收是备份流程的收尾步骤。
        IEnumerable<string> candidates = scopeHashes is null
            ? blobStore.EnumerateBlobHashes()
            : scopeHashes.Where(h => _hash.IsValidHash(h)).Distinct(StringComparer.Ordinal);

        var redundant = new List<string>();
        var buffer = new List<string>(2000);

        foreach (string h in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!blobStore.Exists(h))
            {
                // 已经被回收过或从未存在
                continue;
            }

            buffer.Add(h);
            if (buffer.Count < 2000)
            {
                continue;
            }

            redundant.AddRange(await FilterReferencedAsync(taskId, buffer, ct).ConfigureAwait(false));
            buffer.Clear();
        }

        if (buffer.Count > 0)
        {
            redundant.AddRange(await FilterReferencedAsync(taskId, buffer, ct).ConfigureAwait(false));
        }

        if (redundant.Count == 0)
        {
            return new GcResult(0, 0, []);
        }

        // 账本要带上"这曾经是什么文件"，否则用户只能盲删
        Dictionary<string, (string Path, long Length)> representatives = await _fileRecords
            .GetBlobRepresentativesAsync(taskId, redundant, ct)
            .ConfigureAwait(false);

        long bytes = 0;
        var requests = new List<BlobRecycleRequest>(redundant.Count);
        foreach (string h in redundant)
        {
            long length = blobStore.GetLength(h);
            bytes += length;
            representatives.TryGetValue(h, out (string Path, long Length) info);
            requests.Add(new BlobRecycleRequest(h, length, info.Path, WasDirectory: false));
        }

        int moved = blobStore.RecycleMany(requests, _clock.UtcNow);
        logger.LogInformation("回收了 {Count} 个 blob，共 {Bytes} 字节（任务「{Task}」）。", moved, bytes, task.Name);

        return new GcResult(moved, bytes, []);
    }

    public async Task<RecycleBinStatus> GetRecycleBinAsync(Guid taskId, CancellationToken ct)
    {
        BackupTask task = await GetTaskAsync(taskId, ct).ConfigureAwait(false);
        return _blobStores.Create(task.BackupDir).GetRecycleBinStatus();
    }

    public async Task ClearRecycleBinAsync(Guid taskId, CancellationToken ct)
    {
        BackupTask task = await GetTaskAsync(taskId, ct).ConfigureAwait(false);
        _blobStores.Create(task.BackupDir).ClearRecycleBin();
        logger.LogWarning("清空了任务「{Task}」的回收站（硬删除）。", task.Name);
    }

    private async Task<List<string>> FilterReferencedAsync(
        Guid taskId,
        List<string> candidates,
        CancellationToken ct)
    {
        HashSet<string> referenced = await _fileRecords.GetReferencedBlobsAsync(taskId, candidates, ct)
            .ConfigureAwait(false);

        return [.. candidates.Where(h => !referenced.Contains(h))];
    }

    private async Task<BackupTask> GetTaskAsync(Guid taskId, CancellationToken ct) =>
        await _tasks.GetAsync(taskId, ct).ConfigureAwait(false)
        ?? throw new InvalidOperationException($"找不到任务 {taskId}");
}
