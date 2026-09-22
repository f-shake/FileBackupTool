using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Filtering;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Engine;

/// <summary>
/// 备份引擎。设计文档 03 §3。
/// </summary>
/// <remarks>
/// <code>
/// BeginBackup
///   ├─ 创建 Snapshot { Status = InProgress, BeginTime = UtcNow }
///   └─ 提交（以便取得 SnapshotId）
///
/// 枚举源文件（惰性，不物化）
///   ├─ 应用筛选规则（FzLib FileFilterRule 语法）
///   ├─ 硬编码跳过 $RECYCLE.BIN、数据目录、自己的备份目录
///   └─ 局部扫描时只把脏目录当作根
///
/// 处理（分批提交）
///   ├─ Increment → 与上一个镜像按批比对（TEMP 表，不 replay 进内存）
///   └─ 其他      → 全量路径
///
/// 提交
///   ├─ Snapshot.Status = Completed / Aborted
///   └─ 计数、字节数、镜像规模回写
/// </code>
/// </remarks>
public sealed class BackupEngine(
    IDbContextFactory<AppDbContext> dbFactory,
    TaskStore tasks,
    FileRecordStore fileRecords,
    SnapshotChainService chains,
    IFileSystem fs,
    IPathNormalizer normalizer,
    IHashService hash,
    BlobStoreFactory blobStores,
    IManifestWriter manifestWriter,
    IIntegrityService integrity,
    IClock clock,
    IBackupLogSink logSink,
    IOptions<JobOptions> jobOptions,
    IOptions<StorageOptions> storageOptions,
    ILoggerFactory loggerFactory,
    ILogger<BackupEngine> logger) : IBackupService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly TaskStore _tasks = tasks;
    private readonly FileRecordStore _fileRecords = fileRecords;
    private readonly SnapshotChainService _chains = chains;
    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly IHashService _hash = hash;
    private readonly BlobStoreFactory _blobStores = blobStores;
    private readonly IManifestWriter _manifestWriter = manifestWriter;
    private readonly IIntegrityService _integrity = integrity;
    private readonly IClock _clock = clock;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly JobOptions _options = jobOptions.Value;
    private readonly StorageOptions _storage = storageOptions.Value;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<BackupEngine> _logger = logger;

    public async Task<BackupRunResult> RunAsync(
        Guid taskId,
        SnapshotType? requestedType,
        IReadOnlyList<string>? dirtyRelativePaths,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        BackupTask task = await _tasks.GetAsync(taskId, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"找不到任务 {taskId}");
        if (task.IsDeleted)
        {
            throw new InvalidOperationException($"任务「{task.Name}」已被删除。");
        }

        PhysicalBlobStore blobStore = _blobStores.Create(task.BackupDir);
        blobStore.CleanTemporary();

        SnapshotType type = requestedType ?? await _chains.DecideNextTypeAsync(task, ct).ConfigureAwait(false);
        List<string>? dirtyScopes = NormalizeDirtyScopes(task, dirtyRelativePaths);

        // 增量必须有一个可 replay 的上一个镜像；拿不到就退化为全量。
        // 宁可多写一份数据，也不能写出一条 replay 不通的坏链。
        List<BackupSnapshot>? previousChain = null;
        if (type == SnapshotType.Increment)
        {
            List<BackupSnapshot> completed = await _chains.GetCompletedAsync(task.Id, ct).ConfigureAwait(false);
            BackupSnapshot? previous = completed.LastOrDefault();
            if (previous is null)
            {
                _logger.LogWarning("任务「{Task}」没有可用的上一个快照，本次退化为全量。", task.Name);
                type = task.DefaultFullType;
            }
            else
            {
                previousChain = await _chains.ResolveChainAsync(task.Id, previous.Id, ct).ConfigureAwait(false);
                if (previousChain is null)
                {
                    await LogAsync(task.Id, null, LogLevel.Warning,
                        "上一个快照的镜像链断裂，无法 replay，本次退化为全量备份。", null, ct).ConfigureAwait(false);
                    type = task.DefaultFullType;
                }
            }
        }

        var snapshot = new BackupSnapshot
        {
            TaskId = task.Id,
            Type = type,
            Status = SnapshotStatus.InProgress,
            BeginTime = _clock.UtcNow,
            UsedPartialScan = dirtyScopes is { Count: > 0 },
        };

        await _chains.CreateAsync(snapshot, ct).ConfigureAwait(false);
        _logger.LogInformation(
            "开始{Type}备份：任务「{Task}」快照 #{Id}（{Mode}）",
            DescribeType(type), task.Name, snapshot.Id,
            dirtyScopes is { Count: > 0 } ? $"局部扫描 {dirtyScopes.Count} 个目录" : "全量扫描");

        var context = new RunContext(task, snapshot);
        string? abortReason = null;

        try
        {
            IFileFilter filter = BuildFilter(task);
            var acquirer = new BlobAcquirer(_fs, blobStore, _hash, _options, _loggerFactory);

            if (type == SnapshotType.Increment)
            {
                await RunIncrementAsync(context, previousChain!, filter, dirtyScopes, acquirer, progress, ct)
                    .ConfigureAwait(false);
            }
            else
            {
                await RunFullAsync(context, filter, dirtyScopes, acquirer, type == SnapshotType.VirtualFull,
                    progress, ct).ConfigureAwait(false);
            }

            snapshot.Status = SnapshotStatus.Completed;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            abortReason = "用户取消";
            snapshot.Status = SnapshotStatus.Aborted;
            _logger.LogInformation("备份快照 #{Id} 被取消。", snapshot.Id);
        }
        catch (BackupAbortedException ex)
        {
            abortReason = ex.Message;
            snapshot.Status = SnapshotStatus.Aborted;

            // 终止路径上一律用 None：此时 ct 可能已被取消，用它会二次抛异常并盖掉真正的原因
            await LogAsync(task.Id, snapshot.Id, LogLevel.Error, $"备份已中止：{ex.Message}", null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            abortReason = $"{ex.GetType().Name}: {ex.Message}";
            snapshot.Status = SnapshotStatus.Aborted;
            await LogAsync(task.Id, snapshot.Id, LogLevel.Error, $"备份异常终止：{ex.Message}", ex.ToString(),
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogError(ex, "备份快照 #{Id} 异常终止。", snapshot.Id);
        }

        ApplyCountersToSnapshot(context);
        snapshot.EndTime = _clock.UtcNow;
        snapshot.AbortReason = abortReason;

        // 最后的落库用 CancellationToken.None：即使外层被取消，也必须把"已作废"这个事实写进去，
        // 否则快照会永远停在 InProgress，只能靠下次启动自愈。
        await _fileRecords.FinalizeSnapshotAsync(snapshot, CancellationToken.None).ConfigureAwait(false);

        progress?.Report(new JobProgress(
            snapshot.Status == SnapshotStatus.Completed ? JobPhase.Completed : JobPhase.Committing,
            Discovered: context.Discovered,
            Processed: context.Processed,
            Total: context.Discovered,
            BytesDone: context.BytesWritten));

        if (snapshot.Status == SnapshotStatus.Completed)
        {
            // 清单与回收的失败都不应把一次成功的备份变成失败。
            // 这两步一律用 CancellationToken.None：数据已经提交完了，
            // 此刻被取消只会留下"快照说 Completed、调用方却收到取消"的不一致状态。
            await TryWriteManifestAsync(context, progress, CancellationToken.None).ConfigureAwait(false);
            await TryCollectAsync(context, progress, CancellationToken.None).ConfigureAwait(false);
        }

        await LogAsync(
            task.Id, snapshot.Id,
            snapshot.FailedFileCount > 0 ? LogLevel.Warning : LogLevel.Information,
            snapshot.Status == SnapshotStatus.Completed
                ? $"备份完成：新增 {snapshot.CreatedFileCount}，修改 {snapshot.ModifiedFileCount}，"
                  + $"删除 {snapshot.DeletedFileCount}，失败 {snapshot.FailedFileCount}，"
                  + $"写入 {snapshot.TotalBytes} 字节，镜像 {snapshot.ImageFileCount} 个文件 / "
                  + $"{snapshot.ImageTotalBytes} 字节。"
                : $"备份未完成（{snapshot.Status}）：{abortReason}",
            null, CancellationToken.None).ConfigureAwait(false);

        return new BackupRunResult(
            snapshot.Id, snapshot.Type, snapshot.Status,
            snapshot.CreatedFileCount, snapshot.ModifiedFileCount, snapshot.DeletedFileCount,
            snapshot.FailedFileCount, snapshot.TotalBytes,
            snapshot.ImageFileCount, snapshot.ImageTotalBytes, abortReason);
    }

    // ------------------------------------------------------------------
    // 全量路径（设计文档 03 §3.1）
    // ------------------------------------------------------------------

    private async Task RunFullAsync(
        RunContext context,
        IFileFilter filter,
        List<string>? dirtyScopes,
        BlobAcquirer acquirer,
        bool virtualFull,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        BackupTask task = context.Task;
        var knownLengths = new HashSet<long>();

        foreach (FileEntry[] batch in EnumerateSourceFiles(task, filter, dirtyScopes).Chunk(_options.DbBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            context.Discovered += batch.Length;
            await MergeKnownLengthsAsync(task.Id, batch, knownLengths, ct).ConfigureAwait(false);

            var records = new List<BackupFile>(batch.Length);

            foreach (FileEntry entry in batch)
            {
                try
                {
                    string relativePath = GetRelativePath(task, entry.FullPath);
                    string? contentHash = null;

                    if (!virtualFull)
                    {
                        BlobAcquireResult acquired = await acquirer
                            .AcquireAsync(entry.FullPath, knownLengths.Contains(entry.Length), ct)
                            .ConfigureAwait(false);

                        contentHash = acquired.Hash;
                        context.BytesWritten += acquired.BytesWritten;
                        context.ScopeHashes.Add(acquired.Hash);
                        knownLengths.Add(entry.Length);
                    }

                    records.Add(NewRecord(context, relativePath, FileRecordType.Created,
                        entry.Length, entry.FileTimeUtc, contentHash));

                    context.Created++;
                    context.CreatedBytes += entry.Length;
                    context.Processed++;
                }
                catch (Exception ex) when (IsPerFileFailure(ex))
                {
                    await RegisterFileFailureAsync(context, entry.FullPath, ex, progress, ct).ConfigureAwait(false);
                }
            }

            await CommitBatchAsync(context, records, ct).ConfigureAwait(false);
            ReportProgress(progress, context, null, null);
        }
    }

    // ------------------------------------------------------------------
    // 增量路径（设计文档 03 §3.2）
    // ------------------------------------------------------------------

    private async Task RunIncrementAsync(
        RunContext context,
        List<BackupSnapshot> previousChain,
        IFileFilter filter,
        List<string>? dirtyScopes,
        BlobAcquirer acquirer,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        BackupTask task = context.Task;

        // 上一个镜像的规模是增量维护的，不需要 replay
        BackupSnapshot previous = previousChain[^1];
        context.PreviousImageFileCount = previous.ImageFileCount;
        context.PreviousImageTotalBytes = previous.ImageTotalBytes;

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var mirror = await PreviousMirrorTable.BuildAsync(
                db,
                [.. previousChain.Select(s => s.Id)],
                task.Id,
                dirtyScopes,
                _normalizer,
                ct)
            .ConfigureAwait(false);

        var knownLengths = new HashSet<long>();

        foreach (FileEntry[] batch in EnumerateSourceFiles(task, filter, dirtyScopes).Chunk(_options.DbBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            context.Discovered += batch.Length;

            // 归一化后的 key 才是比对键（Windows 上 IMG.JPG 与 img.jpg 是同一条记录）
            var keys = new string[batch.Length];
            for (int i = 0; i < batch.Length; i++)
            {
                keys[i] = _normalizer.Normalize(GetRelativePath(task, batch[i].FullPath));
            }

            Dictionary<string, MirrorRow> existing = await mirror.TakeBatchAsync(keys, ct).ConfigureAwait(false);
            await MergeKnownLengthsAsync(task.Id, batch, knownLengths, ct).ConfigureAwait(false);

            var records = new List<BackupFile>(batch.Length);

            for (int i = 0; i < batch.Length; i++)
            {
                FileEntry entry = batch[i];
                string key = keys[i];
                existing.TryGetValue(key, out MirrorRow row);
                bool existed = existing.ContainsKey(key);

                try
                {
                    string relativePath = GetRelativePath(task, entry.FullPath);

                    FileChangeKind kind = existed
                        ? FileChangeClassifier.Classify(true, row.Length, row.FileTimeUtc, entry.Length, entry.FileTimeUtc)
                        : FileChangeKind.Created;

                    if (kind == FileChangeKind.Unchanged)
                    {
                        // 未变化：**不写记录**。镜像里已有的那条记录继续生效。
                        context.Processed++;
                        continue;
                    }

                    BlobAcquireResult acquired = await acquirer
                        .AcquireAsync(entry.FullPath, knownLengths.Contains(entry.Length), ct)
                        .ConfigureAwait(false);

                    context.BytesWritten += acquired.BytesWritten;
                    context.ScopeHashes.Add(acquired.Hash);
                    knownLengths.Add(entry.Length);

                    records.Add(NewRecord(context, relativePath, FileChangeClassifier.ToRecordType(kind),
                        entry.Length, entry.FileTimeUtc, acquired.Hash));

                    if (kind == FileChangeKind.Created)
                    {
                        context.Created++;
                        context.CreatedBytes += entry.Length;
                    }
                    else
                    {
                        context.Modified++;
                        context.ModifiedBytesDelta += entry.Length - row.Length;
                    }

                    context.Processed++;
                }
                catch (Exception ex) when (IsPerFileFailure(ex))
                {
                    await RegisterFileFailureAsync(context, entry.FullPath, ex, progress, ct).ConfigureAwait(false);

                    // 失败的文件保持旧版本；若它本来就是新文件，则根本不进镜像
                    if (!existed)
                    {
                        context.FailedNewFiles++;
                    }
                }
            }

            await CommitBatchAsync(context, records, ct).ConfigureAwait(false);
            ReportProgress(progress, context, null, null);
        }

        // 墓碑：镜像里剩下没被磁盘匹配上的，就是在这次扫描范围内被删掉的文件。
        // 局部扫描时镜像只装了脏目录子树，所以子树外的删除**不会被误判**。
        var deleteBatch = new List<BackupFile>(_options.DbBatchSize);
        await foreach (MirrorRow row in mirror.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();

            deleteBatch.Add(new BackupFile
            {
                SnapshotId = context.Snapshot.Id,
                TaskId = task.Id,
                RelativePath = row.Path,
                RelativePathKey = row.PathKey,
                Type = FileRecordType.Deleted,
                Length = row.Length,
                ContentHash = null,
                FileTimeUtc = row.FileTimeUtc,
            });

            context.TouchedRelativePaths.Add(row.Path);
            context.Deleted++;
            context.DeletedBytes += row.Length;

            // 被删除路径的**上一个版本**的 blob 是本次唯一可能变成垃圾的集合
            if (row.ContentHash is not null)
            {
                context.ScopeHashes.Add(row.ContentHash);
            }

            if (deleteBatch.Count >= _options.DbBatchSize)
            {
                await CommitBatchAsync(context, deleteBatch, ct).ConfigureAwait(false);
            }
        }

        await CommitBatchAsync(context, deleteBatch, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 把累加器折算成快照的计数与镜像规模。
    /// </summary>
    /// <remarks>
    /// 镜像规模的公式对全量与增量、全量扫描与局部扫描**是统一的**：
    /// <code>
    /// imageCount = 上一个镜像的文件数 + 新增 − 删除
    /// imageBytes = 上一个镜像的字节数 + Σ新增长度 + Σ(修改后长度 − 修改前长度) − Σ删除长度
    /// </code>
    /// 未变化的文件不产生增量，所以"局部扫描时未触及的目录"天然按继承处理；
    /// 全量时上一个镜像取 (0, 0)，公式退化为"本次枚举到的一切"。
    /// <para>纯新增文件若处理失败则完全不进镜像，所以不计入任何一项。</para>
    /// </remarks>
    private static void ApplyCountersToSnapshot(RunContext context)
    {
        BackupSnapshot snapshot = context.Snapshot;

        snapshot.CreatedFileCount = context.Created;
        snapshot.ModifiedFileCount = context.Modified;
        snapshot.DeletedFileCount = context.Deleted;
        snapshot.FailedFileCount = context.Failed;
        snapshot.TotalBytes = context.BytesWritten;

        snapshot.ImageFileCount = Math.Max(0,
            context.PreviousImageFileCount + context.Created - context.Deleted);
        snapshot.ImageTotalBytes = Math.Max(0,
            context.PreviousImageTotalBytes
            + context.CreatedBytes
            + context.ModifiedBytesDelta
            - context.DeletedBytes);

        snapshot.MirrorInconsistencyCount = context.MirrorInconsistencyCount;
    }

    // ------------------------------------------------------------------
    // 辅助
    // ------------------------------------------------------------------

    private async Task MergeKnownLengthsAsync(
        Guid taskId,
        FileEntry[] batch,
        HashSet<long> knownLengths,
        CancellationToken ct)
    {
        var missing = new List<long>();
        foreach (FileEntry entry in batch)
        {
            if (!knownLengths.Contains(entry.Length))
            {
                missing.Add(entry.Length);
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        HashSet<long> fromDb = await _fileRecords.GetKnownLengthsAsync(taskId, missing, ct).ConfigureAwait(false);
        knownLengths.UnionWith(fromDb);
    }

    private BackupFile NewRecord(
        RunContext context,
        string relativePath,
        FileRecordType type,
        long length,
        DateTime fileTimeUtc,
        string? contentHash)
    {
        context.TouchedRelativePaths.Add(relativePath);

        return new BackupFile
        {
            SnapshotId = context.Snapshot.Id,
            TaskId = context.Task.Id,
            RelativePath = relativePath,
            RelativePathKey = _normalizer.Normalize(relativePath),
            Type = type,
            Length = length,
            ContentHash = contentHash,
            FileTimeUtc = fileTimeUtc,
        };
    }

    private async Task CommitBatchAsync(RunContext context, List<BackupFile> records, CancellationToken ct)
    {
        if (records.Count == 0)
        {
            return;
        }

        await _fileRecords.WriteBatchAsync(records, ct).ConfigureAwait(false);
        records.Clear();
    }

    private async Task RegisterFileFailureAsync(
        RunContext context,
        string path,
        Exception ex,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        context.Failed++;
        context.ConsecutiveFailures++;
        context.Processed++;

        _logger.LogWarning(ex, "处理文件失败：{Path}", path);
        await LogAsync(context.Task.Id, context.Snapshot.Id, LogLevel.Warning,
            $"处理文件失败：{path}", $"{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);

        if (_options.FailureRatioThreshold > 0
            && context.Processed >= _options.FailureRatioMinSamples
            && (double)context.Failed / context.Processed > _options.FailureRatioThreshold)
        {
            throw new BackupAbortedException(
                $"失败率 {context.Failed}/{context.Processed} 超过阈值 {_options.FailureRatioThreshold:P0}，已中止整个备份。");
        }

        if (context.ConsecutiveFailures >= _options.ConsecutiveFailureThreshold)
        {
            throw new BackupAbortedException($"连续失败 {context.ConsecutiveFailures} 次，已中止整个备份。");
        }

        ReportProgress(progress, context, null, null);
    }

    private static void ReportProgress(
        IProgress<JobProgress>? progress,
        RunContext context,
        int? total,
        string? currentPath) =>
        progress?.Report(new JobProgress(
            JobPhase.Processing,
            Discovered: context.Discovered,
            Processed: context.Processed,
            Total: total,
            CurrentPath: currentPath,
            BytesDone: context.BytesWritten));

    /// <summary>
    /// 枚举源文件。惰性，不物化（设计文档 01 §4.2）。
    /// </summary>
    /// <remarks>
    /// 局部扫描时**只把脏目录当作根**去枚举，而不是枚举整棵树再过滤——
    /// 这样"重扫范围"才真的变小。未变化目录里的文件根本不进入枚举，
    /// 它们的记录由上一个镜像经链 replay 继续生效（这就是"继承"）。
    /// </remarks>
    private IEnumerable<FileEntry> EnumerateSourceFiles(
        BackupTask task,
        IFileFilter filter,
        List<string>? dirtyScopes)
    {
        Func<string, bool> descend = filter.DescendInto;

        if (dirtyScopes is not { Count: > 0 })
        {
            foreach (FileEntry entry in _fs.EnumerateFiles(task.SourceDir, FileEnumerationOptions.Recursive, descend))
            {
                if (filter.IncludeFile(entry.FullPath))
                {
                    yield return entry;
                }
            }

            yield break;
        }

        foreach (string scope in dirtyScopes)
        {
            string root = scope.Length == 0 ? task.SourceDir : _normalizer.Combine(task.SourceDir, scope);
            if (!_fs.DirectoryExists(root))
            {
                // 目录已不存在：不枚举，交给镜像的墓碑逻辑把它记为删除
                continue;
            }

            foreach (FileEntry entry in _fs.EnumerateFiles(root, FileEnumerationOptions.Recursive, descend))
            {
                if (filter.IncludeFile(entry.FullPath))
                {
                    yield return entry;
                }
            }
        }
    }

    /// <summary>把脏目录集合压缩并校验：必须落在源目录内，且去掉被祖先覆盖的项。</summary>
    private List<string>? NormalizeDirtyScopes(BackupTask task, IReadOnlyList<string>? dirtyRelativePaths)
    {
        if (dirtyRelativePaths is not { Count: > 0 })
        {
            return null;
        }

        var cleaned = new List<string>();
        foreach (string raw in dirtyRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 源目录根脏了 → 等同于全量扫描
                return null;
            }

            string relative = raw.Replace('/', '\\').Trim('\\');
            string full = _normalizer.Combine(task.SourceDir, relative);
            if (!_normalizer.IsSameOrUnder(full, task.SourceDir))
            {
                _logger.LogWarning("忽略越界的脏目录：{Path}", raw);
                continue;
            }

            string? back = _normalizer.GetRelativePathOrNull(task.SourceDir, full);
            if (back is not null)
            {
                cleaned.Add(back);
            }
        }

        return cleaned.Count == 0 ? null : DirtyDirectorySet.Compact(cleaned, _normalizer);
    }

    private string GetRelativePath(BackupTask task, string fullPath) =>
        _normalizer.GetRelativePathOrNull(task.SourceDir, fullPath)
        ?? throw new IOException($"文件不在源目录内：{fullPath}");

    private IFileFilter BuildFilter(BackupTask task)
    {
        FilterRuleSet rules = FilterRuleSet.FromJson(task.FilterJson);
        IFileFilter inner = rules.IsEnabled && !rules.IsEffectivelyEmpty
            ? new FzLibFileFilter(rules, _normalizer)
            : NullFileFilter.Instance;

        // 硬排除：$RECYCLE.BIN（在 HardExcludeFileFilter 里）、自己的备份目录、
        // 数据目录（SQLite 的 -wal/-shm 边车文件是高频噪声源头，设计文档 02 §8）
        var excluded = new List<string> { task.BackupDir };
        if (!string.IsNullOrWhiteSpace(_storage.DataDirectory))
        {
            excluded.Add(_storage.DataDirectory);
        }

        return new HardExcludeFileFilter(inner, _normalizer, excluded);
    }

    private async Task TryWriteManifestAsync(RunContext context, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report(new JobProgress(JobPhase.Manifest,
                context.Discovered, context.Processed, context.Discovered, BytesDone: context.BytesWritten));

            ManifestWriteResult result = await _manifestWriter
                .WriteAsync(context.Task, context.Snapshot, context.TouchedRelativePaths, ct)
                .ConfigureAwait(false);

            // 清单生成阶段会完整 replay 一遍当前镜像，是唯一能顺带发现链上结构性不一致的地方。
            // 旧设计用 Debug.Assert(false) 当唯一守卫（Release 下被编译掉，损坏静默通过）。
            if (result.MirrorInconsistencies > 0)
            {
                context.Snapshot.MirrorInconsistencyCount = result.MirrorInconsistencies;
                await _fileRecords.FinalizeSnapshotAsync(context.Snapshot, ct).ConfigureAwait(false);
                await LogAsync(context.Task.Id, context.Snapshot.Id, LogLevel.Warning,
                    $"抢救清单生成时发现 {result.MirrorInconsistencies} 处镜像链不一致，请在完整校验中查看。",
                    null, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "生成抢救清单失败（不影响备份结果）。");
            await LogAsync(context.Task.Id, context.Snapshot.Id, LogLevel.Warning,
                "生成抢救清单失败。", ex.ToString(), ct).ConfigureAwait(false);
        }
    }

    private async Task TryCollectAsync(RunContext context, IProgress<JobProgress>? progress, CancellationToken ct)
    {
        try
        {
            progress?.Report(new JobProgress(JobPhase.Collecting));

            // **增量回收**（设计文档 03 §8）：只检查本次涉及的哈希——
            // 本次写过的 blob，以及被本次标记删除的那些路径的**上一个版本**的 blob
            // （后者的引用刚刚失效，是唯一可能变成垃圾的集合）。
            await _integrity.CollectAsync(context.Task.Id, context.ScopeHashes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "增量回收失败（不影响备份结果）。");
        }
    }

    private Task LogAsync(Guid taskId, int? snapshotId, LogLevel level, string message, string? detail, CancellationToken ct) =>
        _logSink.WriteAsync(taskId, snapshotId, level, message, detail, ct);

    private static string DescribeType(SnapshotType type) => type switch
    {
        SnapshotType.Full => "全量",
        SnapshotType.VirtualFull => "虚拟全量",
        _ => "增量",
    };

    /// <summary>
    /// 哪些异常算"单个文件失败，跳过继续"。
    /// </summary>
    /// <remarks>
    /// 旧设计的 bug：只 catch <c>IOException</c>。而 <c>UnauthorizedAccessException</c>
    /// **不是** <c>IOException</c> 的子类，遇到一个无权限的文件会让**整个备份直接炸掉**。
    /// <para>
    /// 另外注意 <c>PathTooLongException</c> 是 <c>IOException</c> 的子类，会被这里吞掉变成"静默跳过"。
    /// 设计文档 01 §6.1 明确说这不可接受——所以每个失败都计入
    /// <see cref="BackupSnapshot.FailedFileCount"/> 并写日志，UI 必须显著提示。
    /// </para>
    /// </remarks>
    private static bool IsPerFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException;

    /// <summary>一次备份运行的累加器。</summary>
    private sealed class RunContext(BackupTask task, BackupSnapshot snapshot)
    {
        public BackupTask Task { get; } = task;

        public BackupSnapshot Snapshot { get; } = snapshot;

        public int Discovered { get; set; }

        public int Processed { get; set; }

        public int Created { get; set; }

        public int Modified { get; set; }

        public int Deleted { get; set; }

        public int Failed { get; set; }

        /// <summary>纯新增且处理失败的文件数——它们完全不进镜像。</summary>
        public int FailedNewFiles { get; set; }

        public int ConsecutiveFailures { get; set; }

        public long BytesWritten { get; set; }

        public long CreatedBytes { get; set; }

        /// <summary>修改的净字节变化（新 − 旧）。</summary>
        public long ModifiedBytesDelta { get; set; }

        public long DeletedBytes { get; set; }

        public long PreviousImageFileCount { get; set; }

        public long PreviousImageTotalBytes { get; set; }

        public int MirrorInconsistencyCount { get; set; }

        public HashSet<string> TouchedRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次涉及的 blob 哈希（增量回收的检查范围）。</summary>
        public HashSet<string> ScopeHashes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>失败率/连续失败超阈值导致的中止。</summary>
    private sealed class BackupAbortedException(string message) : Exception(message);
}
