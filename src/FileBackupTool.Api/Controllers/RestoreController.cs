using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 还原（设计文档 06 §4.4、04 §1）。
/// </summary>
/// <remarks>
/// <para>
/// <b>白名单是第一道闸门，不是最后一道</b>。引擎自己会再校验一次（<c>RestoreService</c>），
/// 但那道校验是以 <c>UnauthorizedAccessException</c> 的形式出现的——用户看到的会是一句
/// "操作被拒绝"。所以这里**先**用同一份白名单判一次，给出可操作的错误
/// （<c>RESTORE_TARGET_NOT_ALLOWED</c> + 具体原因 + 白名单内容），
/// 让用户知道该去设置里加哪个根，而不是猜。
/// </para>
/// <para>
/// <b>执行 = 先预演再入队</b>：引擎的作业队列按 <c>JobRequest.RestorePlan</c> 执行
/// （<c>JobQueue.ExecuteAsync</c> 只认这个字段），所以"执行"必然要先生成一份计划。
/// 这不是多余的一步：用户在向导里看到的计划与实际写入的东西因此是**同一个对象**，
/// 不存在"计划说跳过、执行却覆盖"的漂移。
/// </para>
/// <para>
/// <b>不做 <c>planToken</c></b>：设计文档 06 §4.4 的清单里没有它，引擎也没有任何"令牌"
/// 概念（计划本身就是唯一凭据）。前端把它当可选（<c>planToken?: string | null</c>），
/// 所以这里接受该字段并**原样忽略**，而不是报错——误报错会让前端无法提交。
/// 代价是"预演与执行之间目标目录变了"不会被检测；实际影响有限，
/// 因为引擎在执行时会对每个条目**重新判定冲突**（见 <c>RestoreService.ResolveAction</c>）。
/// </para>
/// </remarks>
[Route("api/restore")]
public sealed class RestoreController(
    IRestoreService restore,
    SnapshotChainService chains,
    TaskStore tasks,
    SettingsService settings,
    IJobQueue queue,
    IBackupLogSink logSink,
    AuditLogger audit,
    IFileSystem fs,
    IPathNormalizer normalizer,
    ILogger<RestoreController> logger) : ApiControllerBase
{
    /// <summary>预演结果里条目清单的截断上限。整快照还原时计划可能很大，界面只看冲突与异常。</summary>
    private const int MaxPlanItems = 2000;

    private readonly IRestoreService _restore = restore;
    private readonly SnapshotChainService _chains = chains;
    private readonly TaskStore _tasks = tasks;
    private readonly SettingsService _settings = settings;
    private readonly IJobQueue _queue = queue;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly AuditLogger _audit = audit;
    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly ILogger<RestoreController> _logger = logger;

    /// <summary>白名单还原根目录列表（含是否可用与剩余空间，供向导第 1 步显示）。</summary>
    [HttpGet("roots")]
    public async Task<IReadOnlyList<RestoreRootDto>> RootsAsync()
    {
        List<string> roots = await _settings.GetAllowedRestoreRootsAsync(Ct).ConfigureAwait(false);

        return
        [
            .. roots
                .Where(r => !string.IsNullOrWhiteSpace(r))
                .Select(root =>
                {
                    bool exists = _fs.DirectoryExists(root);
                    (long free, long total) = GetSpace(root);
                    return new RestoreRootDto(root, Label: null, Exists: exists, FreeBytes: free, TotalBytes: total);
                }),
        ];
    }

    /// <summary>预演：列出将写入的清单、冲突明细与不可还原清单。</summary>
    [HttpPost("plan")]
    public async Task<RestorePlanDto> PlanAsync([FromBody] RestoreApiRequestDto input)
    {
        (RestoreRequest request, string scope, string? subPath) =
            await BuildRequestAsync(input).ConfigureAwait(false);

        RestorePlan plan = await _restore.PlanAsync(request, Ct).ConfigureAwait(false);

        return Map(plan, scope, subPath, request);
    }

    /// <summary>执行（入全局队列），返回作业句柄。</summary>
    [HttpPost("execute")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<RestoreJobHandleDto> ExecuteAsync([FromBody] RestoreApiRequestDto input)
    {
        (RestoreRequest request, string scope, string? subPath) =
            await BuildRequestAsync(input).ConfigureAwait(false);

        BackupTask task = await RequireTaskAsync(_tasks, request.TaskId).ConfigureAwait(false);

        // 入队前先预演：队列认的是计划对象，而且用户已经看过一次计划（向导第 3 步）。
        RestorePlan plan = await _restore.PlanAsync(request, Ct).ConfigureAwait(false);

        string jobId = await _queue.EnqueueAsync(new JobRequest
        {
            Type = JobType.Restore,
            TaskId = request.TaskId,
            SnapshotId = request.SnapshotId,
            RestorePlan = plan,
            Description = $"还原 {task.Name} · 快照 {request.SnapshotId} · {DescribeScope(scope, request)}",
            Automatic = false,
        }, Ct).ConfigureAwait(false);

        string detail =
            $"快照 {request.SnapshotId} → {request.TargetRoot}；范围 {DescribeScope(scope, request)}；" +
            $"默认策略 {request.ConflictPolicy}；计划写入 {plan.PlannedCount} 个文件（{plan.PlannedBytes} 字节），" +
            $"冲突 {plan.ConflictCount}，不可还原 {plan.UnavailableCount}。";

        await _audit.WriteAsync(
            AuditEventType.RestoreExecuted,
            target: $"{task.Name}/{request.SnapshotId}",
            detail: detail,
            ct: Ct).ConfigureAwait(false);

        // 还原是写盘操作，必须在任务日志里留一条（用户事后要能回答"什么时候往哪儿写了什么"）。
        await _logSink.WriteAsync(
            request.TaskId,
            request.SnapshotId,
            LogLevel.Information,
            $"已排入一次还原（作业 {jobId}）。",
            detail,
            Ct).ConfigureAwait(false);

        _logger.LogInformation("还原作业 {JobId} 已入队：{Detail}", jobId, detail);

        return new RestoreJobHandleDto(jobId, PositionOf(jobId));
    }

    /// <summary>取消还原（排队中则作废，运行中则发取消信号）。</summary>
    [HttpDelete("{jobId}")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> CancelAsync([FromRoute] string jobId)
    {
        JobView? job = await _queue.GetAsync(jobId, Ct).ConfigureAwait(false);

        if (job is null || job.Type != JobType.Restore)
        {
            // 只允许通过这个端点取消**还原**作业：备份作业的取消入口是
            // POST /api/tasks/{id}/cancel，两条路径各自带自己的权限语义与提示文案。
            throw ApiException.NotFound(ApiErrorCodes.JobNotFound, $"还原作业 {jobId} 不存在。");
        }

        if (!await _queue.CancelAsync(jobId, Ct).ConfigureAwait(false))
        {
            throw ApiException.Conflict(
                ApiErrorCodes.Conflict,
                $"作业 {jobId} 当前状态为 {job.Status}，无法取消。");
        }

        return NoContent();
    }

    // ------------------------------------------------------------------

    /// <summary>把前端形状的请求翻译成引擎的 <see cref="RestoreRequest"/>，并完成白名单校验。</summary>
    private async Task<(RestoreRequest Request, string Scope, string? SubPath)> BuildRequestAsync(
        RestoreApiRequestDto input)
    {
        string scope = ParseScope(input.Scope);

        if (input.SnapshotId <= 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "必须指定 snapshotId。");
        }

        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, input.SnapshotId).ConfigureAwait(false);

        List<string> relativePaths = scope switch
        {
            RestoreScopeNames.Directory => [RequireRelative(input.Directory, "directory")],
            RestoreScopeNames.Files => NormalizePaths(input.Paths),
            _ => [],
        };

        string? subPath = string.IsNullOrWhiteSpace(input.TargetSubPath)
            ? null
            : input.TargetSubPath.Trim().Trim('\\', '/');

        string targetRoot = ResolveTarget(input.TargetRoot, subPath);

        List<string> allowed = await _settings.GetAllowedRestoreRootsAsync(Ct).ConfigureAwait(false);

        if (allowed.Count == 0)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.RestoreNoAllowedRoots,
                "尚未配置任何白名单还原根目录，还原功能不可用。",
                "请先在「设置」里添加至少一个还原根目录（设计文档 04 §1.3 的硬约束）。");
        }

        if (!DirectoryRelationshipValidator.IsWithinAllowedRoots(
                _normalizer, targetRoot, allowed, out string? reason))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.RestoreTargetNotAllowed,
                $"还原目标不在白名单内：{reason}",
                $"白名单：{string.Join("；", allowed)}");
        }

        (RestoreConflictPolicy policy, IReadOnlyDictionary<string, RestoreConflictPolicy> perPath) =
            ParseStrategies(input.DefaultStrategy, input.Strategies);

        var request = new RestoreRequest(
            snapshot.TaskId,
            snapshot.Id,
            relativePaths,
            targetRoot,
            policy,
            ApplyPolicyToAll: true,
            PerPathPolicies: perPath);

        return (request, scope, subPath);
    }

    /// <summary>
    /// 目标根 + 子路径 → 引擎认的绝对目标目录。
    /// </summary>
    /// <remarks>
    /// 子路径里出现 <c>.</c> / <c>..</c> 一律拒绝：<c>Combine</c> 之后再判白名单会**掩盖**
    /// 真实的意图（<c>D:\safe\..\..\Windows</c> 归一化后可能仍以 <c>D:\safe</c> 开头，
    /// 也可能不——取决于平台），所以穿越片段必须在拼接前就挡掉。
    /// </remarks>
    private string ResolveTarget(string? targetRoot, string? subPath)
    {
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            throw ApiException.BadRequest(ApiErrorCodes.RestoreTargetInvalid, "必须指定还原目标根 targetRoot。");
        }

        string root = targetRoot.Trim();

        if (!_normalizer.IsAbsolute(root))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.RestoreTargetInvalid,
                $"还原目标根必须是绝对路径：{root}");
        }

        if (subPath is null)
        {
            return _normalizer.TrimTrailingSeparator(root);
        }

        if (DirectoryRelationshipValidator.ContainsRelativeSegment(subPath))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.PathContainsRelativeSegment,
                "目标子路径中包含 '.' 或 '..' 片段，已被拒绝。");
        }

        return _normalizer.TrimTrailingSeparator(_normalizer.Combine(root, subPath));
    }

    private static string ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // 缺省整快照：向导默认就是从快照列表点进来的。
            return RestoreScopeNames.Snapshot;
        }

        string value = raw.Trim();

        return value.Equals(RestoreScopeNames.Snapshot, StringComparison.OrdinalIgnoreCase) ? RestoreScopeNames.Snapshot
            : value.Equals(RestoreScopeNames.Directory, StringComparison.OrdinalIgnoreCase) ? RestoreScopeNames.Directory
            : value.Equals(RestoreScopeNames.Files, StringComparison.OrdinalIgnoreCase) ? RestoreScopeNames.Files
            : throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"还原范围「{raw}」无法识别，可选：Snapshot / Directory / Files。");
    }

    private static string RequireRelative(string? path, string field)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, $"range=Directory 时必须提供 {field}。");
        }

        string value = path.Trim().Trim('\\', '/');

        if (value.Length == 0)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"{field} 不能是根目录。要还原整个快照请用 scope=Snapshot。");
        }

        if (DirectoryRelationshipValidator.ContainsRelativeSegment(value))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.PathContainsRelativeSegment,
                "路径中包含 '.' 或 '..' 片段，已被拒绝。");
        }

        return value.Replace('/', '\\');
    }

    private static List<string> NormalizePaths(IReadOnlyList<string>? paths)
    {
        if (paths is not { Count: > 0 })
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "scope=Files 时必须提供 paths。");
        }

        return [.. paths.Select(p => RequireRelative(p, "paths"))];
    }

    /// <summary>解析默认策略与逐文件覆盖。无法识别的策略名**报错**，不静默回落到默认值。</summary>
    private static (RestoreConflictPolicy Default, IReadOnlyDictionary<string, RestoreConflictPolicy> PerPath)
        ParseStrategies(string? defaultStrategy, IReadOnlyDictionary<string, string>? strategies)
    {
        RestoreConflictPolicy policy = ParsePolicy(defaultStrategy, "defaultStrategy", RestoreConflictPolicy.Skip);

        if (strategies is not { Count: > 0 })
        {
            return (policy, new Dictionary<string, RestoreConflictPolicy>());
        }

        var map = new Dictionary<string, RestoreConflictPolicy>(StringComparer.OrdinalIgnoreCase);

        foreach ((string path, string raw) in strategies)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            map[path.Trim().Replace('/', '\\')] = ParsePolicy(raw, $"strategies[{path}]", policy);
        }

        return (policy, map);
    }

    private static RestoreConflictPolicy ParsePolicy(string? raw, string field, RestoreConflictPolicy fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        return Enum.TryParse(raw, ignoreCase: true, out RestoreConflictPolicy policy) && Enum.IsDefined(policy)
            ? policy
            : throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"{field} 的冲突策略「{raw}」无法识别，可选：Skip / Overwrite / KeepBoth / NewerWins。");
    }

    private RestorePlanDto Map(RestorePlan plan, string scope, string? subPath, RestoreRequest request)
    {
        // 先算"需要注意的条目"（冲突 + 不可还原）优先，再补普通条目，最后统一截断。
        // 直接 Take(MaxPlanItems) 会让一个"前 2000 条全是新建"的整快照还原**看不到任何冲突**——
        // 那恰恰是用户唯一需要看的东西。
        List<RestorePlanEntry> important =
        [
            .. plan.Entries.Where(e => e.Action != RestorePlanAction.Restore),
        ];

        List<RestorePlanEntry> normal =
        [
            .. plan.Entries.Where(e => e.Action == RestorePlanAction.Restore),
        ];

        int budget = Math.Max(0, MaxPlanItems - important.Count);
        List<RestorePlanEntry> selected = [.. important, .. normal.Take(budget)];

        bool truncated = selected.Count < plan.Entries.Count;

        RestorePlanItemDto[] items = [.. selected.Select(e => Item(e, request))];
        RestorePlanItemDto[] blocked =
        [
            .. plan.Entries
                .Where(e => e.Action == RestorePlanAction.Unavailable)
                .Take(MaxPlanItems)
                .Select(e => Item(e, request)),
        ];

        return new RestorePlanDto
        {
            // 当前实现不签发也不校验令牌（见类注释）。前端按可空处理。
            PlanToken = null,
            Scope = scope,
            SnapshotId = plan.Request.SnapshotId,
            TargetRoot = plan.Request.TargetRoot,
            TargetSubPath = subPath,
            Items = items,
            WriteCount = plan.PlannedCount,
            SkipCount = plan.ConflictCount,
            ConflictCount = plan.ConflictCount,
            BlockedCount = plan.UnavailableCount,
            TotalBytes = plan.PlannedBytes,
            DefaultStrategy = plan.Request.ConflictPolicy.ToString(),
            BlockedItems = blocked,
            ItemsTruncated = truncated || plan.EntriesTruncated,
        };
    }

    private RestorePlanItemDto Item(RestorePlanEntry entry, RestoreRequest request)
    {
        bool conflict = entry.Action == RestorePlanAction.SkipByConflict;

        // 目标文件的现有大小 / 时间只在**冲突**时才去 stat：非冲突条目要么目标不存在，
        // 要么用户已经决定覆盖，多一次系统调用没有信息增益，而整快照还原会因此多出百万次 IO。
        long? targetLength = null;
        DateTime? targetTime = null;

        if (conflict && entry.TargetExists)
        {
            FileEntry? existing = _fs.GetFile(entry.TargetFullPath);
            targetLength = existing?.Length;
            targetTime = existing?.FileTimeUtc;
        }

        return new RestorePlanItemDto
        {
            RelativePath = entry.RelativePath.Replace('\\', '/'),
            TargetPath = entry.TargetFullPath,
            Length = entry.Length,
            FileTimeUtc = entry.FileTimeUtc == default ? null : entry.FileTimeUtc,
            Conflict = conflict,
            TargetLength = targetLength,
            TargetFileTimeUtc = targetTime,
            Action = ActionOf(entry),
            Strategy = EffectivePolicy(request, entry.RelativePath).ToString(),
            Restorable = entry.Action != RestorePlanAction.Unavailable,
            BlockedReason = entry.UnavailableReason,
        };
    }

    private static string ActionOf(RestorePlanEntry entry) => entry.Action switch
    {
        RestorePlanAction.Unavailable => "Blocked",
        RestorePlanAction.SkipByConflict => "Skip",

        // 目标被换到另一个名字上 = 用户选了"保留两者"。
        _ => entry.FinalTargetPath is not null
             && !string.Equals(entry.FinalTargetPath, entry.TargetFullPath, StringComparison.OrdinalIgnoreCase)
            ? "Rename"
            : entry.TargetExists ? "Overwrite" : "Create",
    };

    /// <summary>
    /// 这条记录**实际生效**的策略。
    /// </summary>
    /// <remarks>
    /// 与引擎的 <c>RestoreService.PolicyFor</c> 用同一套规则（先原样查、再按平台语义比较），
    /// 因为界面上的"策略"列必须显示真的会生效的那一个——显示全局默认而实际用了逐文件覆盖，
    /// 会让用户在"为什么这个文件被覆盖了"上完全找不到答案。
    /// </remarks>
    private RestoreConflictPolicy EffectivePolicy(RestoreRequest request, string relativePath)
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

    private static string DescribeScope(string scope, RestoreRequest request) => scope switch
    {
        RestoreScopeNames.Directory => $"目录 {request.RelativePaths.FirstOrDefault()}",
        RestoreScopeNames.Files => $"{request.RelativePaths.Count} 个文件",
        _ => "整快照",
    };

    private int? PositionOf(string jobId)
    {
        int queued = _queue.QueuedCount;
        return queued <= 0 ? null : queued;
    }

    private static (long Free, long Total) GetSpace(string root)
    {
        try
        {
            string? driveRoot = Path.GetPathRoot(root);
            if (string.IsNullOrEmpty(driveRoot))
            {
                return (0, 0);
            }

            var drive = new DriveInfo(driveRoot);
            return drive.IsReady ? (drive.AvailableFreeSpace, drive.TotalSize) : (0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 还原根本身不可读（盘没挂载 / 权限变了）：如实返回 0，让界面显示"不可用"，
            // 而不是编一个数字。
            return (0, 0);
        }
    }
}
