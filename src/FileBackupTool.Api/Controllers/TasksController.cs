using System.Text.Json;
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
/// 任务端点（设计文档 06 §4.1）。
/// </summary>
[Route("api/tasks")]
public sealed class TasksController(
    TaskStore tasks,
    DirectoryRelationshipValidator validator,
    SettingsService settings,
    TaskProjection projection,
    IJobQueue queue,
    MonitoringAccess monitoring,
    IBackupLogSink logSink,
    AuditLogger audit,
    ILogger<TasksController> logger) : ApiControllerBase
{
    private readonly TaskStore _tasks = tasks;
    private readonly DirectoryRelationshipValidator _validator = validator;
    private readonly SettingsService _settings = settings;
    private readonly TaskProjection _projection = projection;
    private readonly IJobQueue _queue = queue;
    private readonly MonitoringAccess _monitoring = monitoring;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly AuditLogger _audit = audit;
    private readonly ILogger<TasksController> _logger = logger;

    /// <summary>任务列表。**返回裸数组**（前端契约如此，不分页——任务数是人力维护的，不会到需要分页的量级）。</summary>
    [HttpGet]
    public async Task<IReadOnlyList<BackupTaskDto>> ListAsync()
    {
        List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, Ct).ConfigureAwait(false);
        return await _projection.BuildListAsync(all, Ct).ConfigureAwait(false);
    }

    [HttpGet("{id:guid}")]
    public async Task<BackupTaskDto> GetAsync(Guid id)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);
        return await _projection.BuildOneAsync(task, Ct).ConfigureAwait(false);
    }

    /// <summary>新建任务。**先做目录关系校验**（02 §7），冲突时返回 400 + 结构化清单。</summary>
    [HttpPost]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<ActionResult<BackupTaskDto>> CreateAsync([FromBody] BackupTaskInputDto input)
    {
        string name = RequireName(input.Name);
        string sourceDir = RequirePath(input.SourceDir, "源目录");
        string backupDir = RequirePath(input.BackupDir, "备份目录");
        string filterJson = NormalizeFilter(input.FilterJson);

        await ValidateDirsAsync(sourceDir, backupDir, excludeTaskId: null, await GetAllowedRootsAsync().ConfigureAwait(false))
            .ConfigureAwait(false);

        var task = new BackupTask
        {
            Id = Guid.NewGuid(),
            Name = name,
            SourceDir = sourceDir,
            BackupDir = backupDir,
            FilterJson = filterJson,
            ByTimeInterval = input.ByTimeInterval,
            TimeIntervalMinutes = NormalizeInterval(input.TimeIntervalMinutes),
            MaxAutoIncrementBackupCount = NormalizeMaxIncrement(input.MaxAutoIncrementBackupCount),
            IsDefaultVirtualBackup = input.IsDefaultVirtualBackup,
            ByWatching = input.ByWatching,
            IsEnabled = input.IsEnabled,
        };

        await _tasks.AddAsync(task, Ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.TaskCreated,
            target: task.Name,
            detail: $"源 {task.SourceDir}；备份 {task.BackupDir}；定时 {task.ByTimeInterval}；监控 {task.ByWatching}",
            ct: Ct).ConfigureAwait(false);

        // 监控是按任务配置建立 watcher 的，新增任务必须让它知道。
        await _monitoring.ReloadAsync(Ct).ConfigureAwait(false);

        return await _projection.BuildOneAsync(task, Ct).ConfigureAwait(false);
    }

    [HttpPut("{id:guid}")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<BackupTaskDto> UpdateAsync(Guid id, [FromBody] BackupTaskInputDto input)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        string name = RequireName(input.Name);
        string sourceDir = RequirePath(input.SourceDir, "源目录");
        string backupDir = RequirePath(input.BackupDir, "备份目录");
        string filterJson = NormalizeFilter(input.FilterJson);

        // 校验时必须排除自身，否则"备份目录等于自己的备份目录"会永远报冲突。
        await ValidateDirsAsync(sourceDir, backupDir, excludeTaskId: id, await GetAllowedRootsAsync().ConfigureAwait(false))
            .ConfigureAwait(false);

        task.Name = name;
        task.SourceDir = sourceDir;
        task.BackupDir = backupDir;
        task.FilterJson = filterJson;
        task.ByTimeInterval = input.ByTimeInterval;
        task.TimeIntervalMinutes = NormalizeInterval(input.TimeIntervalMinutes);
        task.MaxAutoIncrementBackupCount = NormalizeMaxIncrement(input.MaxAutoIncrementBackupCount);
        task.IsDefaultVirtualBackup = input.IsDefaultVirtualBackup;
        task.ByWatching = input.ByWatching;
        task.IsEnabled = input.IsEnabled;

        await _tasks.UpdateAsync(task, Ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.TaskUpdated,
            target: task.Name,
            detail: $"源 {task.SourceDir}；备份 {task.BackupDir}；定时 {task.ByTimeInterval}；监控 {task.ByWatching}；启用 {task.IsEnabled}",
            ct: Ct).ConfigureAwait(false);

        await _monitoring.ReloadAsync(Ct).ConfigureAwait(false);

        return await _projection.BuildOneAsync(task, Ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 软删除任务（设计文档 08 §4.2：**快照保留、blob 交 GC、不物理删备份目录**）。
    /// </summary>
    [HttpDelete("{id:guid}")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> DeleteAsync()
    {
        Guid id = RouteTaskId();
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        // 先取消作业：不然一个正在跑的备份会把快照写在一个"已删除"的任务上，
        // 用户会看到已删除的任务又长出一条新记录。
        if (await _queue.CancelForTaskAsync(id, Ct).ConfigureAwait(false))
        {
            _logger.LogInformation("删除任务 {Task} 前取消了它的进行中作业。", task.Name);
        }

        await _tasks.SoftDeleteAsync(id, Ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.TaskDeleted,
            target: task.Name,
            detail: $"软删除；快照与 blob 保留，备份目录 {task.BackupDir} 未被删除。",
            ct: Ct).ConfigureAwait(false);

        await _monitoring.ReloadAsync(Ct).ConfigureAwait(false);

        return NoContent();
    }

    /// <summary>启用 / 禁用任务。</summary>
    [HttpPost("{id:guid}/enable")]
    public async Task<BackupTaskDto> SetEnabledAsync([FromBody] SetEnabledDto input)
    {
        Guid id = RouteTaskId();
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        task.IsEnabled = input.Enabled;

        // 禁用时顺手关掉监控开关，否则监控仍会为它建 watcher 并触发备份——
        // "禁用"在用户眼里是"别再动它了"，只关定时不关监控不符合这个预期。
        if (!input.Enabled)
        {
            task.ByWatching = false;
        }

        await _tasks.UpdateAsync(task, Ct).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.TaskUpdated,
            target: task.Name,
            detail: input.Enabled ? "启用任务" : "禁用任务（同时关闭变更监控）",
            ct: Ct).ConfigureAwait(false);

        await _monitoring.ReloadAsync(Ct).ConfigureAwait(false);

        return await _projection.BuildOneAsync(task, Ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 目录关系预校验（表单实时反馈用）。**不落库、不抛错**——把冲突清单原样返回，
    /// 让表单在用户还没点保存时就标红。
    /// </summary>
    [HttpPost("validate-dirs")]
    public async Task<DirValidationResultDto> ValidateDirsEndpointAsync([FromBody] ValidateDirsRequestDto input)
    {
        List<BackupTask> others = await _tasks.GetForValidationAsync(input.TaskId, Ct).ConfigureAwait(false);

        IReadOnlyList<string> roots = input.RestoreRoots is { Count: > 0 }
            ? input.RestoreRoots
            : await GetAllowedRootsAsync().ConfigureAwait(false);

        IReadOnlyList<DirectoryValidationIssue> issues = _validator.ValidateTask(
            input.SourceDir ?? string.Empty,
            input.BackupDir ?? string.Empty,
            others,
            roots);

        return ValidationIssueMapper.ToResult(issues);
    }

    /// <summary>立即备份（body 指定 Full / VirtualFull / Increment；null 表示按策略自动决定）。</summary>
    [HttpPost("{id:guid}/backup")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<ActionResult<JobHandleDto>> StartBackupAsync([FromBody] BackupRequestDto? input)
    {
        Guid id = RouteTaskId();
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        SnapshotType? requested = ParseSnapshotType(input?.Type);

        // 同一任务不允许有两个活跃备份作业：第二个会把同一批文件再写一遍 blob，
        // 除了浪费 IO 没有任何收益（blob 内容寻址，结果完全相同）。
        if (await JobQueueQuery.HasActiveJobForTaskAsync(_queue, id, Ct).ConfigureAwait(false))
        {
            throw ApiException.Conflict(
                ApiErrorCodes.JobQueueBusy,
                $"任务「{task.Name}」已有备份作业在排队或运行中，请等它结束。");
        }

        string typeLabel = requested switch
        {
            SnapshotType.Full => "全量",
            SnapshotType.VirtualFull => "虚拟全量",
            SnapshotType.Increment => "增量",
            _ => "自动",
        };

        string jobId = await _queue.EnqueueAsync(new JobRequest
        {
            Type = JobType.Backup,
            TaskId = id,
            BackupType = requested,
            Description = $"{task.Name} · {typeLabel}（手动）",
            Automatic = false,
        }, Ct).ConfigureAwait(false);

        // 入队后立刻告诉监控"这个任务暂时不用触发了"，否则 watcher 会在备份期间
        // 把备份自身写入的文件当成新变更再排一次队。
        await _logSink.WriteAsync(id, null, LogLevel.Information,
            $"已手动排入一次{typeLabel}备份。", null, Ct).ConfigureAwait(false);

        return new JobHandleDto(jobId, PositionOf(jobId));
    }

    /// <summary>取消当前备份。</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelAsync()
    {
        Guid id = RouteTaskId();
        await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        bool cancelled = await _queue.CancelForTaskAsync(id, Ct).ConfigureAwait(false);

        if (!cancelled)
        {
            throw ApiException.NotFound(ApiErrorCodes.JobNotFound, "该任务当前没有正在运行或排队的作业。");
        }

        return NoContent();
    }

    /// <summary>
    /// 手动触发扫描（06 §4.5）。监控已接入时交给它（它会走静默期/合并窗口再触发备份），
    /// 未接入时退化为"直接排一次备份"——这样这个按钮在两种状态下都真的有用。
    /// </summary>
    [HttpPost("{id:guid}/scan")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<ActionResult<JobHandleDto>> ScanAsync([FromBody] ScanRequestDto? input)
    {
        Guid id = RouteTaskId();
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        bool fullScan = input?.Full ?? false;

        bool? handled = await _monitoring.RequestScanAsync(id, fullScan, Ct).ConfigureAwait(false);

        if (handled is true)
        {
            await _logSink.WriteAsync(id, null, LogLevel.Information,
                $"已请求监控执行一次{(fullScan ? "全量" : "局部")}扫描。", null, Ct).ConfigureAwait(false);

            // 监控自己决定何时入队，所以这里没有 jobId。
            return new JobHandleDto(string.Empty, null);
        }

        if (handled is false)
        {
            throw ApiException.Conflict(
                ApiErrorCodes.MonitoringRootNotFound,
                $"任务「{task.Name}」当前没有可扫描的监控根（可能是监控未启用、或该根正处在错误状态）。");
        }

        // 监控未接入：直接排一次备份，路径非空时按局部扫描处理。
        if (await JobQueueQuery.HasActiveJobForTaskAsync(_queue, id, Ct).ConfigureAwait(false))
        {
            throw ApiException.Conflict(
                ApiErrorCodes.JobQueueBusy,
                $"任务「{task.Name}」已有作业在排队或运行中。");
        }

        IReadOnlyList<string>? dirty = fullScan || string.IsNullOrWhiteSpace(input?.Path)
            ? null
            : [input!.Path!];

        string jobId = await _queue.EnqueueAsync(new JobRequest
        {
            Type = JobType.Backup,
            TaskId = id,
            DirtyScopes = dirty,
            Description = $"{task.Name} · {(dirty is null ? "全量扫描" : "局部扫描")}（手动扫描）",
            Automatic = false,
        }, Ct).ConfigureAwait(false);

        return new JobHandleDto(jobId, PositionOf(jobId));
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 路由里的 id。
    /// </summary>
    /// <remarks>
    /// 单独抽出来是因为 <c>[ApiController]</c> 会把 <c>Guid</c> 参数默认当成查询参数
    /// （除非显式标 <c>[FromRoute]</c>），而路由模板里明明有 <c>{id}</c>。
    /// 显式读 <c>RouteData</c> 比在十几个动作上逐个标特性更不容易漏。
    /// </remarks>
    private Guid RouteTaskId()
    {
        object? raw = RouteData.Values["id"];

        return Guid.TryParse(raw?.ToString(), out Guid id)
            ? id
            : throw ApiException.NotFound(ApiErrorCodes.TaskNotFound, "任务不存在。");
    }

    private Task<List<string>> GetAllowedRootsAsync() => _settings.GetAllowedRestoreRootsAsync(Ct);

    private async Task ValidateDirsAsync(
        string sourceDir,
        string backupDir,
        Guid? excludeTaskId,
        IReadOnlyList<string> restoreRoots)
    {
        List<BackupTask> others = await _tasks.GetForValidationAsync(excludeTaskId, Ct).ConfigureAwait(false);

        IReadOnlyList<DirectoryValidationIssue> issues = _validator.ValidateTask(
            sourceDir, backupDir, others, restoreRoots);

        if (issues.Count > 0)
        {
            // 稳定 code 由 DirectoryRelationshipValidator 给出（如 BACKUP_INSIDE_SOURCE），
            // 异常的 detail 里带完整清单，前端据此高亮是哪两个路径打架。
            throw DirectoryValidationFailedException.From(issues);
        }
    }

    private int? PositionOf(string jobId)
    {
        // 入队刚返回时"前面还有几个"就是排队位置；引擎内部 0 基，这里换成 1 基给前端。
        int queued = _queue.QueuedCount;
        return queued <= 0 ? null : queued;
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw ApiException.BadRequest(ApiErrorCodes.TaskNameRequired, "任务名称不能为空。");
        }

        return name.Trim();
    }

    private static string RequirePath(string? path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // 空路径的稳定 code 就是校验器的那一个，保持"校验器说什么、API 就说什么"。
            throw ApiException.BadRequest(DirectoryRelationshipValidator.CodePathEmpty, $"{label}不能为空。");
        }

        return path.Trim();
    }

    /// <summary>
    /// 归一化筛选规则 JSON。
    /// </summary>
    /// <remarks>
    /// <b>非法 JSON 必须报错，不能像 <c>FilterRuleSet.FromJson</c> 那样退化为"不过滤"</b>：
    /// 用户手写筛选规则时打错一个引号，如果被静默当成"没有规则"，他会以为筛选生效了，
    /// 而实际上**把所有文件都备份了**——这既不危险也不符合预期，只是很难发现。
    /// </remarks>
    private static string NormalizeFilter(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return "{}";
        }

        if (filterJson.Length > 64 * 1024)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "筛选规则过长（上限 64 KB）。");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(filterJson);

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "筛选规则必须是一个 JSON 对象。");
            }
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "筛选规则不是合法的 JSON，已拒绝保存。",
                ex.Message);
        }

        return filterJson;
    }

    private static int NormalizeInterval(int minutes) => minutes switch
    {
        < 1 => 1440,
        // 上限一年：更大的值等价于"不定时"，而 UI 上那个输入框该有边界。
        > 525600 => 525600,
        _ => minutes,
    };

    private static int NormalizeMaxIncrement(int count) => count switch
    {
        < 1 => 1,
        > 10000 => 10000,
        _ => count,
    };

    /// <summary>解析 body 里的备份类型。无法识别时报 400 而不是静默按自动处理。</summary>
    private static SnapshotType? ParseSnapshotType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse(raw, ignoreCase: true, out SnapshotType type) && Enum.IsDefined(type)
            ? type
            : throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"备份类型「{raw}」无法识别，可选：Full / VirtualFull / Increment。");
    }
}
