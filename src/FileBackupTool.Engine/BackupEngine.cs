using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Filtering;
using FileBackupTool.Core.IO;
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
    IoPrioritySwitch ioSwitch,
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
    private readonly IoPrioritySwitch _ioSwitch = ioSwitch;
    private readonly JobOptions _options = jobOptions.Value;
    private readonly StorageOptions _storage = storageOptions.Value;
    private readonly ILoggerFactory _loggerFactory = loggerFactory;
    private readonly ILogger<BackupEngine> _logger = logger;

    /// <summary>增量链的长度安全界。</summary>
    /// <remarks>
    /// 镜像物化把链上的快照 id 作为 <c>IN (...)</c> 列表**内联**进 SQL（每个 id 一个参数，
    /// 见 `PreviousMirrorTable`），而 SQLite 的参数上限是 32766 —— 超过它，该任务的
    /// **增量备份与还原会开始显式失败**，直到有人手动跑一次全量把链重置。
    /// <para>
    /// 正常情况下这个界碰不到：<c>DecideNextTypeAsync</c> 会在增量数超过
    /// <c>MaxAutoIncrementBackupCount</c>（界面把它夹到 ≤ 10000）时转全量。
    /// **但那个决策只在调用方没有显式指定类型时才被咨询**，而
    /// <c>POST /api/tasks/{id}/backup</c> 可以带 <c>type = "Increment"</c> 绕过它 ——
    /// 于是"连续请求增量"能把链一路推过上限。这里兜一道，取舍与下面那句
    /// "宁可多写一份数据，也不能写出一条 replay 不通的坏链"一致。
    /// </para>
    /// </remarks>
    private const int MaxIncrementChainLength = 20000;

    /// <summary>
    /// 一次局部扫描允许的脏目录数上限，超过就退化为全量扫描。
    /// </summary>
    /// <remarks>
    /// 镜像物化把每个脏目录写成 <b>2 个 SQL 参数</b>（`LIKE key\%` 与 `= key`），
    /// 加上链上的快照 id（每个 1 个），全部**内联**进同一条 SQL，而 SQLite 的参数上限是 32766。
    /// <para>
    /// 8000 的取值依据：参数数 = 1 + 链长 + 2N。链长即使按被夹住的上界 10001 算，
    /// 8000 个脏目录也只有 1 + 10001 + 16000 = 26002，留在上限之内。
    /// </para>
    /// <para>
    /// 为什么需要这条**绝对**上限而不只依赖占比（<c>LocalScanDegradeRatio</c> = 30%）：
    /// 源树目录数上万时，30% 以内就能过万 —— 占比判据拦不住，而越界会让**增量备份报错中止**。
    /// 退化本身是免费的（局部扫描只是省读，全量总是更完整）。
    /// </para>
    /// </remarks>
    private const int MaxDirtyScopeCount = 8000;

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

        // 先丢弃"被跳过的目录"队列里的残留。它挂在单例文件系统上，监控扫描等其它路径
        // 也会往里写——不先清一次，末尾那次上报就会把不属于本次备份的目录算进来。
        DrainSkipped();

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

        // 链长安全界。显式请求增量会绕过 DecideNextTypeAsync 的"超限转全量"（见常量说明），
        // 所以这里独立兜一道，而不是依赖那个决策被咨询过。
        if (type == SnapshotType.Increment && previousChain is { Count: > MaxIncrementChainLength })
        {
            _logger.LogWarning(
                "任务「{Task}」的增量链已有 {Count} 个快照，超过安全界 {Max}，本次退化为全量。",
                task.Name, previousChain.Count, MaxIncrementChainLength);

            await LogAsync(task.Id, null, LogLevel.Warning,
                $"增量链已有 {previousChain.Count} 个快照（安全界 {MaxIncrementChainLength}），"
                + "本次退化为全量备份，以免镜像物化的 SQL 参数超过 SQLite 上限。",
                null, ct).ConfigureAwait(false);

            type = task.DefaultFullType;
            previousChain = null;
        }

        // **局部扫描只对增量有意义**：增量的未触及目录靠上一个镜像"继承"（03 §5.4）。
        // 全量没有可继承的上一个镜像，它的语义是"一个快照仍然覆盖整个源"（03 §5.4）。
        //
        // 所以一旦本次跑的是全量，脏目录**必须丢弃**。否则这个"全量"快照的镜像只包含
        // 脏目录子树，而它会被后续增量当作基准 → 链从此长期不一致，抢救清单还会被
        // 重写成只剩脏子树的片。这条极容易触发：任务开着监控时，只要有文件变动
        // 就会带着脏目录触发备份，而"还没有任何全量"或"增量数超限"都会判定为全量——
        // 于是**第一个快照就可能是一个只覆盖变更目录的坏全量**。
        if (type != SnapshotType.Increment)
        {
            dirtyScopes = null;
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
        // 与 abortReason 分开：取消与"因失败率/异常中止"都写 Aborted，但作业层需要区分
        // （见 BackupRunResult.Canceled 的说明）。
        bool canceled = false;

        try
        {
            IFileFilter filter = BuildFilter(task);
            var acquirer = new BlobAcquirer(_fs, blobStore, _hash, _options, _ioSwitch, _loggerFactory);

            if (_ioSwitch.Enabled && !IoPriority.IsAvailable)
            {
                _logger.LogDebug("未启用低 IO 优先级：当前平台或文件系统不支持该能力，备份照常进行。");
            }

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
            canceled = true;
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

        // 跳过的目录必须交出来（03 §7「不静默」），见 ReportSkippedDirectoriesAsync。
        // 放在这里而不是枚举结束时：两条路径（全量/增量）都会跳过目录，收口在同一个地方，
        // 而且要在 ApplyCountersToSnapshot 之前——它要往 FailedFileCount 上计数。
        await ReportSkippedDirectoriesAsync(task.Id, snapshot.Id, context).ConfigureAwait(false);
        await ReportSkippedFilesAsync(task.Id, snapshot.Id, context).ConfigureAwait(false);

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

        // 诊断：开关开着、平台也说支持，但整轮一次都没成功应用过 ——
        // 多半是源盘的文件系统不支持该信息类。这行只进调试日志，不影响备份。
        if (_ioSwitch.Enabled && IoPriority.IsAvailable && !IoPriority.HasEverApplied)
        {
            _logger.LogDebug("低 IO 优先级已开启，但本机尚未成功应用过（源盘文件系统可能不支持）。");
        }

        await LogAsync(
            task.Id, snapshot.Id,
            snapshot.FailedFileCount > 0 || snapshot.SkippedFileCount > 0
                ? LogLevel.Warning
                : LogLevel.Information,
            snapshot.Status == SnapshotStatus.Completed
                ? $"备份完成：新增 {snapshot.CreatedFileCount}，修改 {snapshot.ModifiedFileCount}，"
                  + $"删除 {snapshot.DeletedFileCount}，失败 {snapshot.FailedFileCount}，"
                  + $"写入 {snapshot.TotalBytes} 字节，镜像 {snapshot.ImageFileCount} 个文件 / "
                  + $"{snapshot.ImageTotalBytes} 字节。"
                  // 跳过的目录也要出现在摘要里——否则用户只会看到"失败 0"，
                  // 而那棵树根本没被备份（03 §7 的立场是"不静默"）。
                  + (context.SkippedDirectoryCount > 0
                      ? $"（另有 {context.SkippedDirectoryCount} 个目录未被进入，详见上一条日志。）"
                      : string.Empty)
                  + (snapshot.SkippedFileCount > 0
                      ? $"（另有 {snapshot.SkippedFileCount} 个文件因仍在写入而跳过，下次扫描会重试。）"
                      : string.Empty)
                : $"备份未完成（{snapshot.Status}）：{abortReason}",
            null, CancellationToken.None).ConfigureAwait(false);

        return new BackupRunResult(
            snapshot.Id, snapshot.Type, snapshot.Status,
            snapshot.CreatedFileCount, snapshot.ModifiedFileCount, snapshot.DeletedFileCount,
            snapshot.FailedFileCount, snapshot.TotalBytes,
            snapshot.ImageFileCount, snapshot.ImageTotalBytes, abortReason)
        {
            Canceled = canceled,
            SkippedFileCount = snapshot.SkippedFileCount,
        };
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
                        if (!IsSettled(entry))
                        {
                            context.RecordSkip(entry.FullPath);
                            context.Processed++;
                            continue;
                        }

                        BlobAcquireResult acquired = await acquirer
                            .AcquireAsync(entry, knownLengths.Contains(entry.Length), ct)
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
                    context.Attempted++;
                    MarkFileSucceeded(context);
                }
                catch (FileUnstableException ex)
                {
                    // 文件还在写：不是失败。不计失败率、不触发中止，下一轮再来。
                    context.RecordSkip(ex.Path);
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
                        context.Attempted++;
                        MarkFileSucceeded(context);
                        continue;
                    }

                    if (!IsSettled(entry))
                    {
                        // 还在写：本轮不取 blob、不写记录。镜像里保持它上一版的状态，
                        // 下一轮它会重新以 Created/Modified 出现。
                        context.RecordSkip(entry.FullPath);
                        context.Processed++;
                        continue;
                    }

                    BlobAcquireResult acquired = await acquirer
                        .AcquireAsync(entry, knownLengths.Contains(entry.Length), ct)
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
                    context.Attempted++;
                    MarkFileSucceeded(context);
                }
                catch (FileUnstableException ex)
                {
                    // 拷贝期间文件在变：本轮的 blob 已作废。按跳过处理，下一轮重试。
                    context.RecordSkip(ex.Path);
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
        snapshot.SkippedFileCount = context.SkippedInProgress;
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

    /// <summary>
    /// 成功处理完一个文件后清零"连续失败"计数。
    /// </summary>
    /// <remarks>
    /// 这个计数器的语义是<b>连续</b>失败（03 §7.1：失败率超阈值**或连续失败 100 次**→ 中止）。
    /// 不在成功路径上归零的话，它就退化成"累计失败 100 次"，于是一个源目录里散布着
    /// 100 个互不相关的老旧坏文件也会中止整次备份 —— 那正是这个指标想避免的假阳性。
    /// 失败率阈值负责识别"整片坏掉"，连续计数负责识别"卡在某个点上"，两者分工不同。
    /// </remarks>
    private static void MarkFileSucceeded(RunContext context) => context.ConsecutiveFailures = 0;

    /// <summary>
    /// 前置守卫：这个文件距最后修改是否已经超过静默期。
    /// </summary>
    /// <remarks>
    /// <b>它只是"省钱"，不是正确性保证</b>：NTFS 对正被打开写入的文件，目录项里的长度与
    /// 最后写入时间是滞后的 —— 一个正在写的文件看起来可以完全静止，这条守卫会放行。
    /// 真正兜住的是 <c>BlobAcquirer</c> 的拷后复核。这里拦住 90% 的明显情况，
    /// 目的只是别白读一个几十 GB 的未完成文件。
    /// <para>
    /// 未来时间戳分两种：**小幅超前**（不超过一个静默期）按"正在写"处理；
    /// **大幅超前**（超过一个静默期）一律放行 —— 那多半是相机或另一台机器的时钟不准，
    /// 一直跳过等于永远不备份它。
    /// </para>
    /// </remarks>
    private bool IsSettled(FileEntry entry)
    {
        double window = _options.MinFileAgeSeconds;
        if (window <= 0)
        {
            return true;
        }

        DateTime now = _clock.UtcNow;
        TimeSpan age = now - entry.FileTimeUtc;

        if (age < TimeSpan.Zero)
        {
            // 未来时间戳。小幅超前按"仍在写入"处理（文件刚创建、时钟粒度可能让它略微超前）；
            // 但偏差超过一个静默期就不能再等 —— 那多半是相机或另一台机器的时钟不准，
            // 等下去等于永远不备份它。宁可带一个不准的时间戳备份，也不能无限期跳过。
            return -age >= TimeSpan.FromSeconds(window);
        }

        return age.TotalSeconds >= window;
    }

    /// <summary>
    /// 取走并上报本次枚举时被跳过的目录。
    /// </summary>
    /// <remarks>
    /// <c>PhysicalFileSystem</c> 把两类目录记进一个队列：**目录重解析点**（junction / 符号链接，
    /// 03 §3 明确不进入——进入它可能无限递归）与**无法枚举的目录**（权限、路径过长等，属失败）。
    /// 这个队列原来**只有 FsController 一个消费者**，而且用的是它自己 new 的私有实例，
    /// 引擎侧那个单例队列因此只入不出：
    /// <list type="bullet">
    /// <item>用户永远不知道有子树没被备份——快照照样报"备份完成：失败 0"（03 §7 要的正是"不静默"）；</item>
    /// <item>增量备份下更糟：无法枚举目录里那些**仍然存在**的文件的旧镜像行不会被
    /// <c>TakeBatchAsync</c> 取走，于是被当成删除写成墓碑——镜像里文件消失，全程无提示。</item>
    /// </list>
    /// 只入不出还会让这个队列随进程生命周期无界增长。
    /// </remarks>
    /// <summary>
    /// 取走"被跳过的目录"清单。
    /// </summary>
    /// <remarks>
    /// <c>IDirectorySkipReporter</c> 是**可选**接口（09 B3 的措辞就是"通过可选的
    /// <c>IDirectorySkipReporter</c> 交出来"），所以这里按可选处理：
    /// 实现它的文件系统（<c>PhysicalFileSystem</c>）才报得出东西，
    /// 别的实现（测试里的内存文件系统）返回空清单即可。
    /// </remarks>
    private IReadOnlyList<SkippedDirectory> DrainSkipped() =>
        _fs is IDirectorySkipReporter reporter ? reporter.DrainSkippedDirectories() : [];

    /// <summary>
    /// 取走并上报本次枚举时被跳过的目录。
    /// </summary>
    /// <remarks>
    /// <c>PhysicalFileSystem</c> 把两类目录记进一个队列：**目录重解析点**（junction / 符号链接，
    /// 03 §3 明确不进入——进入它可能无限递归）与**无法枚举的目录**（权限、路径过长等，属失败）。
    /// 这个队列原来**只有 FsController 一个消费者**，而且用的是它自己 new 的私有实例，
    /// 引擎侧那个单例队列因此只入不出：
    /// <list type="bullet">
    /// <item>用户永远不知道有子树没被备份——快照照样报"备份完成：失败 0"（03 §7 要的正是"不静默"）；</item>
    /// <item>增量备份下更糟：无法枚举目录里那些**仍然存在**的文件的旧镜像行不会被
    /// <c>TakeBatchAsync</c> 取走，于是被当成删除写成墓碑——镜像里文件消失，全程无提示。
    /// <b>注意：这一步只是"上报"，并没有"阻止"</b>——那些文件仍然会被墓碑化。
    /// 要不要把"源目录无法枚举"升级为中止整次备份，是一个待定项（见 09 J5）。</item>
    /// </list>
    /// 只入不出还会让这个队列随进程生命周期无界增长。
    /// </remarks>
    private async Task ReportSkippedDirectoriesAsync(Guid taskId, int snapshotId, RunContext context)
    {
        IReadOnlyList<SkippedDirectory> skipped;
        try
        {
            skipped = DrainSkipped();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "读取「被跳过的目录」清单失败，本次不记录。");
            return;
        }

        if (skipped.Count == 0)
        {
            return;
        }

        // 只有"无法枚举"才算失败。有意跳过的重解析点若也计数，源目录里放几个 junction
        // 就会让每次备份都显示成"成功（N 个失败）"——那会把真正的失败淹掉。
        int unreadable = skipped.Count(s => !s.Deliberate);
        context.SkippedDirectoryCount = skipped.Count;
        context.Failed += unreadable;

        string sample = string.Join("、", skipped.Take(5).Select(s => s.FullPath));
        string tail = skipped.Count > 5 ? " …" : string.Empty;

        await LogAsync(
                taskId, snapshotId,
                unreadable > 0 ? LogLevel.Warning : LogLevel.Information,
                unreadable > 0
                    ? $"有 {skipped.Count} 个目录未被进入，其中 {unreadable} 个无法枚举（已计入失败）：{sample}{tail}"
                    : $"有 {skipped.Count} 个目录未被进入（均为符号链接/junction，属有意跳过）：{sample}{tail}",
                string.Join(Environment.NewLine, skipped.Select(s => $"{s.FullPath} —— {s.Reason}")),
                CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogInformation("快照 #{Id} 跳过了 {Count} 个目录（其中无法枚举 {Bad} 个）。",
            snapshotId, skipped.Count, unreadable);
    }

    /// <summary>
    /// 把"因仍在写入而跳过"的文件汇总成一条日志。逐文件刷屏没有意义，
    /// 但**必须留下证据**：这些文件这一轮没有进备份，用户要能看出"这次备份并不完整"。
    /// </summary>
    private async Task ReportSkippedFilesAsync(Guid taskId, int snapshotId, RunContext context)
    {
        int skipped = context.SkippedInProgress;
        if (skipped == 0)
        {
            return;
        }

        string sample = string.Join("、", context.SkippedSamples);
        string tail = skipped > context.SkippedSamples.Count ? " …" : string.Empty;

        await LogAsync(
                taskId, snapshotId, LogLevel.Warning,
                $"有 {skipped} 个文件因仍在写入而跳过（下一次扫描会重试）：{sample}{tail}",
                _options.MinFileAgeSeconds > 0
                    ? $"判据：距最后修改不足 {_options.MinFileAgeSeconds:F0} 秒，或读取期间长度/时间发生变化。"
                      + "跳过不是失败：这些文件没有进入本次快照，何时重试取决于监控触发或任务的定时设置，不保证是立刻。"
                    : "判据：读取期间长度/时间发生变化（时间守卫已关闭）。跳过不是失败，会在下一次扫描时重试。",
                CancellationToken.None)
            .ConfigureAwait(false);

        _logger.LogWarning("快照 #{Id} 有 {Count} 个文件因仍在写入而跳过。", snapshotId, skipped);
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
        context.Attempted++;

        _logger.LogWarning(ex, "处理文件失败：{Path}", path);
        await LogAsync(context.Task.Id, context.Snapshot.Id, LogLevel.Warning,
            $"处理文件失败：{path}", $"{ex.GetType().Name}: {ex.Message}", ct).ConfigureAwait(false);

        // 分母只算"真正尝试过"的文件：跳过的那批根本没读过内容，把它们算进来会稀释失败率
        // （100 个跳过 + 1 个真失败会显示成 1%），也会把样本数凑过最小样本门槛。
        if (_options.FailureRatioThreshold > 0
            && context.Attempted >= _options.FailureRatioMinSamples
            && (double)context.Failed / context.Attempted > _options.FailureRatioThreshold)
        {
            throw new BackupAbortedException(
                $"失败率 {context.Failed}/{context.Attempted} 超过阈值 {_options.FailureRatioThreshold:P0}，已中止整个备份。");
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

        if (cleaned.Count == 0)
        {
            return null;
        }

        List<string> compacted = DirtyDirectorySet.Compact(cleaned, _normalizer);

        // **脏目录数的绝对上限。** 镜像物化把每个脏目录写成 **2 个 SQL 参数**
        // （`LIKE $likeN` 与 `= $eqN`），加上链上的快照 id，全部内联进同一条 SQL，
        // 而 SQLite 的参数上限是 32766。
        //
        // 只靠 `LocalScanDegradeRatio`（占比超过 30% 才退化全量）**不够**：源树目录数上万时，
        // 30% 以内就可能过万，而约 16384 个脏目录会让**增量备份以 SQLite 报错中止** ——
        // 那是"核心功能在目标规模上停止工作"，比还原失败严重得多（还原失败用户还能改范围）。
        //
        // 超过就退化为全量扫描。这个退化在语义上是**免费的**：局部扫描只是省读，
        // 全量总是更完整；而且它与"占比超限退化"是同一个取舍。
        if (compacted.Count > MaxDirtyScopeCount)
        {
            _logger.LogInformation(
                "脏目录数 {Count} 超过上限 {Max}，本次退化为全量扫描（避免镜像物化的 SQL 参数越界）。",
                compacted.Count, MaxDirtyScopeCount);
            return null;
        }

        return compacted;
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

        /// <summary>
        /// 真正"碰过"的文件数：**不含因仍在写入而跳过的那批**。
        /// </summary>
        /// <remarks>
        /// 失败率的分母必须是它，而不是 <see cref="Processed"/>：跳过意味着"根本没读"，
        /// 算进分母会把失败率稀释（100 个跳过 + 1 个真失败显示成 1%），
        /// 也会把样本数凑过 <c>FailureRatioMinSamples</c> 门槛。
        /// </remarks>
        public int Attempted { get; set; }

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

        /// <summary>本次枚举时跳过的目录数（含**有意**跳过的重解析点）。</summary>
        public int SkippedDirectoryCount { get; set; }

        /// <summary>因"仍在写入"而跳过的文件数。**不计入失败**。</summary>
        public int SkippedInProgress { get; set; }

        /// <summary>跳过的文件样例（最多 5 个），供汇总日志使用。</summary>
        public List<string> SkippedSamples { get; } = [];

        /// <summary>
        /// 记一次跳过。样例只留前 5 个：日志是给人看的，刷 200 条路径只会让它变成噪音。
        /// </summary>
        public void RecordSkip(string path)
        {
            SkippedInProgress++;

            if (SkippedSamples.Count >= 5)
            {
                return;
            }

            foreach (string existing in SkippedSamples)
            {
                if (string.Equals(existing, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            SkippedSamples.Add(path);
        }

        public HashSet<string> TouchedRelativePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>本次涉及的 blob 哈希（增量回收的检查范围）。</summary>
        public HashSet<string> ScopeHashes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>失败率/连续失败超阈值导致的中止。</summary>
    private sealed class BackupAbortedException(string message) : Exception(message);
}
