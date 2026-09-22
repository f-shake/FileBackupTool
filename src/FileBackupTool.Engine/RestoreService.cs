using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using FileBackupTool.Engine.Mirror;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Engine;

/// <summary>
/// 还原。设计文档 04 §1。
/// </summary>
/// <remarks>
/// <b>这是纯新增功能</b>——ArchiveMaster 根本没有还原，只有"另存为"
/// （单文件或单文件夹 → 系统文件选择器 → 一个复制进度对话框）。
/// 因此没有现成 UX 可抄，但**能力不能倒退**：整快照 / 目录 / 多选文件三种粒度都要支持，
/// 还要有冲突策略、结果汇总、时间戳恢复。
/// <para>
/// <b>白名单是硬约束</b>：还原是<b>写文件</b>操作，这个应用能读服务器任意路径（目录浏览）
/// 并且能写（还原），一个被攻破的会话就等于服务器沦陷——攻击者可以往启动目录写文件、
/// 可以覆盖任意文件。白名单是低成本、高收益的缓解手段。
/// </para>
/// <para>
/// <b>目标集合按需流式选取</b>（借助与增量备份同一套 TEMP 表机制），
/// 所以"整快照还原百万文件"不会先物化出一百万条计划条目。
/// </para>
/// </remarks>
public sealed class RestoreService(
    IDbContextFactory<AppDbContext> dbFactory,
    TaskStore tasks,
    SnapshotChainService chains,
    IPathNormalizer normalizer,
    IFileSystem fs,
    BlobStoreFactory blobStores,
    SettingsStore settings,
    IBackupLogSink logSink,
    IOptions<JobOptions> jobOptions,
    ILogger<RestoreService> logger) : IRestoreService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly TaskStore _tasks = tasks;
    private readonly SnapshotChainService _chains = chains;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly IFileSystem _fs = fs;
    private readonly BlobStoreFactory _blobStores = blobStores;
    private readonly SettingsStore _settings = settings;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly JobOptions _options = jobOptions.Value;
    private readonly ILogger<RestoreService> _logger = logger;

    public async Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct)
    {
        (BackupTask _, PhysicalBlobStore blobStore) = await ResolveAsync(request, ct).ConfigureAwait(false);

        var entries = new List<RestorePlanEntry>();
        int total = 0;
        int planned = 0;
        int conflicts = 0;
        int unavailable = 0;
        long plannedBytes = 0;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (MirrorItem item in StreamTargetsAsync(request, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            total++;

            string targetPath = _normalizer.Combine(request.TargetRoot, item.Path);
            bool targetExists = _fs.FileExists(targetPath);
            PlannedAction action = ResolveAction(
                item, targetPath, PolicyFor(request, item.Path), usedNames, blobStore);

            switch (action.Action)
            {
                case RestorePlanAction.Restore:
                    planned++;
                    plannedBytes += item.Length;
                    break;
                case RestorePlanAction.SkipByConflict:
                    conflicts++;
                    break;
                default:
                    unavailable++;
                    break;
            }

            // 条目数少就全列出来；多则只列"需要注意的"，避免计划本身把内存撑爆
            if (total <= RestorePlan.FullListingThreshold || action.Action != RestorePlanAction.Restore)
            {
                entries.Add(new RestorePlanEntry(
                    item.Path, targetPath, item.Length, item.FileTimeUtc, item.ContentHash,
                    TargetExists: targetExists,
                    Action: action.Action,
                    UnavailableReason: action.Reason,
                    FinalTargetPath: action.FinalTargetPath));
            }
        }

        return new RestorePlan(request, entries, total, planned, conflicts, unavailable, plannedBytes);
    }

    public async Task<RestoreResult> ExecuteAsync(
        RestorePlan plan,
        IProgress<JobProgress>? progress,
        CancellationToken ct)
    {
        RestoreRequest request = plan.Request;
        (BackupTask task, PhysicalBlobStore blobStore) = await ResolveAsync(request, ct).ConfigureAwait(false);

        // 执行前重新校验白名单：计划可能是很久以前生成的
        List<string> allowedRoots = await _settings.GetAllowedRestoreRootsAsync(ct).ConfigureAwait(false);
        if (!DirectoryRelationshipValidator.IsWithinAllowedRoots(
                _normalizer, request.TargetRoot, allowedRoots, out string? reason))
        {
            throw new UnauthorizedAccessException($"还原目标被拒绝：{reason}");
        }

        int restored = 0;
        int skipped = 0;
        int failed = 0;
        int unavailable = 0;
        long bytes = 0;
        var failures = new List<RestoreFailure>();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int processed = 0;
        int total = plan.TotalCount > 0 ? plan.TotalCount : 0;

        await foreach (MirrorItem item in StreamTargetsAsync(request, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            processed++;

            string targetPath = _normalizer.Combine(request.TargetRoot, item.Path);

            try
            {
                PlannedAction action = ResolveAction(
                    item, targetPath, PolicyFor(request, item.Path), usedNames, blobStore);
                switch (action.Action)
                {
                    case RestorePlanAction.Unavailable:
                        unavailable++;
                        failures.Add(new RestoreFailure(item.Path, action.Reason ?? "不可恢复"));
                        break;

                    case RestorePlanAction.SkipByConflict:
                        skipped++;
                        break;

                    default:
                    {
                        string finalPath = action.FinalTargetPath ?? targetPath;
                        long written = await CopyBlobAsync(blobStore, item, finalPath, ct).ConfigureAwait(false);
                        restored++;
                        bytes += written;
                        usedNames.Add(finalPath);
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                failures.Add(new RestoreFailure(item.Path, $"{ex.GetType().Name}: {ex.Message}"));
                _logger.LogWarning(ex, "还原失败：{Path}", item.Path);
            }

            if (processed % 50 == 0 || (total > 0 && processed == total))
            {
                progress?.Report(new JobProgress(JobPhase.Restoring, Discovered: total, Processed: processed,
                    Total: total > 0 ? total : null, CurrentPath: item.Path, BytesDone: bytes));
            }
        }

        await _logSink.WriteAsync(task.Id, request.SnapshotId, LogLevel.Information,
            $"还原完成：成功 {restored}，跳过 {skipped}，失败 {failed}，不可恢复 {unavailable}，写入 {bytes} 字节。",
            $"目标根：{request.TargetRoot}", CancellationToken.None).ConfigureAwait(false);

        return new RestoreResult(restored, skipped, failed, unavailable, bytes, failures);
    }

    // ------------------------------------------------------------------

    /// <summary>校验请求、解析白名单与链，返回任务与 blob 池。</summary>
    private async Task<(BackupTask Task, PhysicalBlobStore BlobStore)> ResolveAsync(
        RestoreRequest request,
        CancellationToken ct)
    {
        BackupSnapshot snapshot = await _chains.GetAsync(request.SnapshotId, ct).ConfigureAwait(false)
                                  ?? throw new InvalidOperationException($"找不到快照 #{request.SnapshotId}");
        if (snapshot.TaskId != request.TaskId)
        {
            throw new InvalidOperationException("快照不属于该任务。");
        }

        BackupTask task = await _tasks.GetAsync(request.TaskId, ct).ConfigureAwait(false)
                          ?? throw new InvalidOperationException($"找不到任务 {request.TaskId}");

        List<string> allowedRoots = await _settings.GetAllowedRestoreRootsAsync(ct).ConfigureAwait(false);
        if (!DirectoryRelationshipValidator.IsWithinAllowedRoots(
                _normalizer, request.TargetRoot, allowedRoots, out string? reason))
        {
            throw new UnauthorizedAccessException($"还原目标被拒绝：{reason}");
        }

        List<BackupSnapshot>? chain = await _chains.ResolveChainAsync(request.TaskId, request.SnapshotId, ct)
            .ConfigureAwait(false);
        if (chain is null || chain.Count == 0)
        {
            throw new InvalidOperationException("该快照的镜像链断裂，无法还原。");
        }

        return (task, _blobStores.Create(task.BackupDir));
    }

    /// <summary>
    /// 流式产出还原目标（镜像里的存活条目）。整快照还原的 prefix 为 null，局部还原为给定的子树。
    /// </summary>
    private async IAsyncEnumerable<MirrorItem> StreamTargetsAsync(
        RestoreRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        List<BackupSnapshot>? chain = await _chains.ResolveChainAsync(request.TaskId, request.SnapshotId, ct)
            .ConfigureAwait(false);
        if (chain is null || chain.Count == 0)
        {
            yield break;
        }

        List<string>? prefixes = null;
        if (request.RelativePaths.Count > 0)
        {
            prefixes = [];
            foreach (string raw in request.RelativePaths)
            {
                string cleaned = (raw ?? string.Empty).Replace('/', '\\').Trim('\\');
                if (cleaned.Length == 0)
                {
                    // 出现了根，等价于整快照
                    prefixes = null;
                    break;
                }

                if (DirectoryRelationshipValidator.ContainsRelativeSegment(cleaned))
                {
                    throw new UnauthorizedAccessException($"路径包含 '.' 或 '..' 片段：{raw}");
                }

                prefixes.Add(_normalizer.Normalize(cleaned));
            }
        }

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var table = await PreviousMirrorTable.BuildAsync(
                db, [.. chain.Select(s => s.Id)], request.TaskId, prefixes, _normalizer, ct)
            .ConfigureAwait(false);

        await foreach (MirrorRow row in table.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            yield return new MirrorItem(row.PathKey, row.Path, row.Length, row.FileTimeUtc, row.ContentHash,
                row.SnapshotId);
        }
    }

    /// <summary>
    /// 取这个条目应使用的冲突策略：**逐文件的覆盖优先，全局默认兜底**。
    /// </summary>
    /// <remarks>
    /// 设计文档 04 §1.4 要求"冲突策略逐文件可选，全局默认跳过"。
    /// 先按原样查一次（前端可能已经用了大小写不敏感的字典），
    /// 未命中再按平台语义逐项比较——不能让"键的大小写不同"静默退化成"用了全局策略"。
    /// </remarks>
    private RestoreConflictPolicy PolicyFor(RestoreRequest request, string relativePath)
    {
        if (request.PerPathPolicies is not { Count: > 0 } overrides)
        {
            return request.ConflictPolicy;
        }

        if (overrides.TryGetValue(relativePath, out RestoreConflictPolicy exact))
        {
            return exact;
        }

        foreach ((string key, RestoreConflictPolicy value) in overrides)
        {
            if (_normalizer.AreEqual(key, relativePath))
            {
                return value;
            }
        }

        return request.ConflictPolicy;
    }

    /// <summary>
    /// 决定这个条目的拟定动作。
    /// </summary>
    /// <remarks>
    /// 执行时也复用它——冲突判定必须在**写入那一刻**重新做一次，
    /// 因为计划生成到执行之间目标目录可能已经变了。
    /// <para>
    /// blob 是否存在**在计划阶段就要查**（一次 <c>File.Exists</c>，不读内容，很便宜）：
    /// 设计文档 04 §1.6 要求"批量还原时若有部分文件不可恢复，先明确列出受影响的文件，
    /// 让用户选择是否还原其余文件，不要静默跳过"。等到执行时才发现，用户已经点了确认。
    /// </para>
    /// </remarks>
    private PlannedAction ResolveAction(
        MirrorItem item,
        string targetPath,
        RestoreConflictPolicy policy,
        HashSet<string> usedNames,
        PhysicalBlobStore blobStore)
    {
        if (string.IsNullOrEmpty(item.ContentHash))
        {
            return new PlannedAction(RestorePlanAction.Unavailable, null,
                "该文件不存在实际备份文件，可能是由虚拟快照生成");
        }

        if (!blobStore.Exists(item.ContentHash))
        {
            // 既不在池中也不在回收站 → 物理丢失，还原必然失败
            return new PlannedAction(RestorePlanAction.Unavailable, null,
                "该文件不存在实际备份文件，可能是文件丢失");
        }

        if (!_fs.FileExists(targetPath))
        {
            return new PlannedAction(RestorePlanAction.Restore, targetPath, null);
        }

        switch (policy)
        {
            case RestoreConflictPolicy.Skip:
                // 全局默认。**不做静默覆盖**——旧设计的"另存为"用 FileMode.Create 会静默覆盖目标。
                return new PlannedAction(RestorePlanAction.SkipByConflict, targetPath, "目标已存在，按策略跳过");

            case RestoreConflictPolicy.Overwrite:
                return new PlannedAction(RestorePlanAction.Restore, targetPath, null);

            case RestoreConflictPolicy.NewerWins:
            {
                DateTime? existing = _fs.GetFile(targetPath)?.FileTimeUtc;
                return existing.HasValue && existing.Value >= item.FileTimeUtc
                    ? new PlannedAction(RestorePlanAction.SkipByConflict, targetPath, "目标更新，按策略跳过")
                    : new PlannedAction(RestorePlanAction.Restore, targetPath, null);
            }

            case RestoreConflictPolicy.KeepBoth:
                return new PlannedAction(RestorePlanAction.Restore,
                    BuildAlternativePath(targetPath, usedNames), "目标已存在，保留两者");

            default:
                return new PlannedAction(RestorePlanAction.SkipByConflict, targetPath, "未知策略");
        }
    }

    private async Task<long> CopyBlobAsync(
        PhysicalBlobStore blobStore,
        MirrorItem item,
        string targetPath,
        CancellationToken ct)
    {
        if (item.ContentHash is null || !blobStore.Exists(item.ContentHash))
        {
            // 两种情况给不同文案：虚拟全量（哈希为 null）vs 物理丢失
            throw new IOException(item.ContentHash is null
                ? "该文件不存在实际备份文件，可能是由虚拟快照生成"
                : "该文件不存在实际备份文件，可能是文件丢失");
        }

        _fs.CreateDirectory(_normalizer.GetDirectoryName(targetPath));

        long copied;
        using (Stream source = blobStore.OpenRead(item.ContentHash))
        using (Stream destination = _fs.Create(targetPath))
        {
            copied = await Core.Streams.StreamHelper
                .CopyAsync(source, destination, _options.CopyBufferSizeBytes, ct)
                .ConfigureAwait(false);
        }

        // 还原**恢复原始时间戳**（设计文档 04 §1.5）
        if (item.FileTimeUtc != default)
        {
            _fs.SetLastWriteTimeUtc(targetPath, item.FileTimeUtc);
        }

        return copied;
    }

    private string BuildAlternativePath(string targetPath, HashSet<string> usedNames)
    {
        string directory = _normalizer.GetDirectoryName(targetPath);
        string name = _normalizer.GetFileName(targetPath);
        string stem = name;
        string extension = string.Empty;

        int dot = name.LastIndexOf('.');
        if (dot > 0)
        {
            stem = name[..dot];
            extension = name[dot..];
        }

        for (int i = 1; i < 10000; i++)
        {
            string candidate = _normalizer.Combine(directory, $"{stem} ({i}){extension}");
            if (!_fs.FileExists(candidate) && !usedNames.Contains(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"无法为 {targetPath} 生成不冲突的新名字。");
    }

    private readonly record struct PlannedAction(RestorePlanAction Action, string? FinalTargetPath, string? Reason);
}
