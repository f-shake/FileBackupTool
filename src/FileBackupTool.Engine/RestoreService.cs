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
        // 跨条目共享的"已确认没有重解析点"的目录集合（见 FindReparsePoint）
        var okDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (MirrorItem item in StreamTargetsAsync(request, ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            total++;

            string targetPath = _normalizer.Combine(request.TargetRoot, item.Path);
            bool targetExists = _fs.FileExists(targetPath);
            PlannedAction action = ResolveAction(
                item, targetPath, request.TargetRoot, PolicyFor(request, item.Path),
                usedNames, okDirectories, blobStore);

            switch (action.Action)
            {
                case RestorePlanAction.Restore:
                    planned++;
                    plannedBytes += item.Length;
                    // **必须像 ExecuteAsync 那样登记已占用的目标名**（见 :163）。
                    // KeepBoth 生成的"a (1).txt"本身也要占住这个名字，否则同一批里
                    // 两个本该分别落到 a (1).txt 与 a (2).txt 的条目会算出**同一个**新名字——
                    // 计划界面显示"将写入 2 个文件、无冲突"，执行时却按已登记的名字改判，
                    // 用户确认过的落点和实际落盘不一致。预演不可信比没有预演更糟。
                    usedNames.Add(action.FinalTargetPath ?? targetPath);
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
        // 跨条目共享的"已确认没有重解析点"的目录集合（见 FindReparsePoint）。
        // 执行时**重新**建一个：计划可能生成于很久以前，那时的目录状态不能作为依据。
        var okDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    item, targetPath, request.TargetRoot, PolicyFor(request, item.Path),
                    usedNames, okDirectories, blobStore);
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

        // 只有 Completed 的快照有合法镜像（02 §2.2：**查询一律只看 Status == Completed**）。
        //
        // 不拦这一下的话，`ResolveChainAsync` 会**静默回退到该快照之前的最后一个 Completed 快照**
        // ——于是"还原快照 #N"实际写下去的是快照 #M 的内容，而用户是照着 #N 的预演结果点的确认。
        // 浏览一个已作废的快照时给出上一个镜像是有意的取舍（EngineTests 有断言守着），
        // 但还原是**写文件**操作，写错时间点是不可逆的，所以这里必须明确拒绝而不是沿用回退。
        if (snapshot.Status != SnapshotStatus.Completed)
        {
            throw new InvalidOperationException(
                $"快照 #{request.SnapshotId} 的状态是 {snapshot.Status}，没有可还原的镜像。请选择已完成的快照。");
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

        // **规模上限。** 镜像物化把"选中的文件"写成一组 OR 前缀，每个前缀占 **2 个 SQL 参数**
        // （`LIKE $likeN` 与 `= $eqN`），再加上链上的快照 id（每个 1 个），全部内联进同一条 SQL。
        // SQLite 的参数上限是 32766 —— 超过它**不是变慢，是 `too many sql variables` 直接失败**，
        // 而这条路径同时服务预演与执行，也就是整个还原功能在"勾选很多文件"时 100% 不可用
        // （而且是**数据恢复**路径）。
        //
        // 预算**按实际链长算**，不写常数：链长是被强制夹住的（`DecideNextTypeAsync` 在增量数
        // 超过 `MaxAutoIncrementBackupCount` 时转全量，界面把该值夹到 ≤ 10000），所以
        // "链长只有几百"是**假设**而不是约束 —— 拿它去算一个常数会在链很长时依然越界。
        const int SqliteParameterLimit = 32766;
        int pathBudget = (SqliteParameterLimit - chain.Count - 2) / 2;
        if (request.RelativePaths.Count > pathBudget)
        {
            throw new InvalidOperationException(
                $"一次最多还原 {pathBudget} 个选中的文件（当前 {request.RelativePaths.Count} 个）。"
                + "请改用「整个快照」或「该目录及其子目录」作为范围。");
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
    /// 找出目标路径上第一个**重解析点**（符号链接 / junction）：目标文件自身，或它各级已存在的祖先目录。
    /// 没有则返回 <c>null</c>。
    /// </summary>
    /// <remarks>
    /// 为什么必须有这一道：白名单校验是纯字符串比较，而**写操作会跟随重解析点**。
    /// 白名单根里只要有一个目录 junction，或目标位置上已经有一个文件符号链接，
    /// 还原就会写到白名单之外。这是"任意文件写入"，05 §1.1 把它列在头号后果。
    /// <para>
    /// 只查**已存在**的段：不存在的中间目录是我们自己创建的，新建的目录不会是重解析点，
    /// 所以从目标文件往上查到第一个不存在的目录即可停止（根一定存在，循环会在根处收口）。
    /// </para>
    /// <para>
    /// <paramref name="okDirectories"/> 是跨条目共享的"已确认干净"目录集合。
    /// 不加它的话，百万文件的整快照还原会做上百万次 <c>GetDirectory</c>；
    /// 加上之后每个**目录**只查一次（同一目录下的文件共享结果），代价可以忽略。
    /// </para>
    /// </remarks>
    private string? FindReparsePoint(string targetRoot, string targetPath, HashSet<string> okDirectories)
    {
        // 目标文件自身：已存在且是符号链接时，`FileMode.Create` 会截断它**指向的真实文件**
        if (_fs.IsReparsePoint(targetPath))
        {
            return targetPath;
        }

        string? current = _normalizer.GetDirectoryName(targetPath);
        var checkedNow = new List<string>();
        string? hit = null;

        while (current is not null
               && !okDirectories.Contains(current)
               && _normalizer.IsSameOrUnder(current, targetRoot))
        {
            checkedNow.Add(current);

            // **探针必须与"存在性"解耦。** 实测（.NET 10 / Windows 10）：指向不存在目标的
            // **目录**链接在 `Directory.Exists` / `DirectoryInfo.Exists` 眼里是"不存在"
            // （所以 `GetDirectory` 返回 null），但它**仍然**是重解析点，
            // `Directory.CreateDirectory` 与 `FileMode.Create` 都会跟随它 ——
            // 于是"在白名单根里预先摆一个悬空的目录链接"照样能把写入引到白名单之外。
            // （悬空的**文件**链接没有这个问题：`FileInfo.Exists` 对它返回 true，属性里也带 ReparsePoint。）
            if (_fs.IsReparsePoint(current))
            {
                hit = current;
                break;
            }

            if (_normalizer.AreEqual(current, targetRoot))
            {
                break;
            }

            current = _normalizer.GetDirectoryName(current);
        }

        if (hit is null)
        {
            // 只有整条链都干净才登记：中途命中的话，中间那些段本身**是**干净的、
            // 但把它们记成"已确认"也没问题（命中点是更上面的那个），所以这里不登记更保守。
            okDirectories.UnionWith(checkedNow);
        }

        return hit;
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
        string targetRoot,
        RestoreConflictPolicy policy,
        HashSet<string> usedNames,
        HashSet<string> okDirectories,
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

        // **写路径上的重解析点校验。** 白名单校验（`IsWithinAllowedRoots`）是**纯字符串比较**，
        // 而 `Directory.CreateDirectory` 与 `FileMode.Create` 都会**跟随**重解析点：
        // 白名单根里只要有一个目录 junction、或目标位置上已有一个文件符号链接，
        // 写入就会落到白名单之外（`C:\Windows\System32`、服务启动目录、或任何服务账号
        // 可写的位置）。05 §1.1 把"任意文件写入"列在头号后果、白名单是头号缓解手段，
        // 而这道防线原来只落在**读**路径上（`FsController` 判最后一段）。
        if (FindReparsePoint(targetRoot, targetPath, okDirectories) is { } reparse)
        {
            return new PlannedAction(RestorePlanAction.Unavailable, null,
                $"目标路径上有重解析点（符号链接/junction），拒绝写入以免落到白名单之外：{reparse}");
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
