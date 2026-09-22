using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Monitoring;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 实时监控总览（设计文档 06 §4.5 的 <c>GET /api/monitoring</c>、07 §4.6）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么监控缺席时返回 503 而不是空列表</b>：空列表看起来与"确实一个受监控的根都没有"
/// 完全一样，用户会以为监控正常但没任务。这两件事的处置方式截然不同（前者要去看日志里
/// 监控为什么没起来，后者要去建任务），所以必须能区分。
/// </para>
/// <para>
/// <b>Disabled 的根也返回</b>：任务列表里"这个任务没开监控"本身就是信息，
/// 去掉它前端只能自己再查一遍任务表才能把两种来源拼起来。代价是
/// <c>state</c> 会出现第四个取值 <c>disabled</c>——前端契约里的
/// <c>MonitorRootState</c> 只有三个值，这一处**需要前端把联合类型补上**（已写入交付说明）。
/// 刻意不把 <c>disabled</c> 折成 <c>polling</c>：轮询是在监控，只是方式不同。
/// </para>
/// </remarks>
[Route("api/monitoring")]
public sealed class MonitoringController(
    MonitoringAccess monitoring,
    IJobQueue queue,
    TaskStore tasks,
    IClock clock,
    ILogger<MonitoringController> logger) : ApiControllerBase
{
    private readonly MonitoringAccess _monitoring = monitoring;
    private readonly IJobQueue _queue = queue;
    private readonly TaskStore _tasks = tasks;
    private readonly IClock _clock = clock;
    private readonly ILogger<MonitoringController> _logger = logger;

    [HttpGet]
    public async Task<MonitoringSnapshotDto> GetAsync()
    {
        IReadOnlyList<RootMonitorStatus>? statuses = _monitoring.GetStatuses();

        if (statuses is null)
        {
            throw new ApiException(
                ApiErrorCodes.MonitoringUnavailable,
                "变更监控不可用。",
                System.Net.HttpStatusCode.ServiceUnavailable,
                "监控模块未接入，或读取状态时发生错误（详见服务器日志）。此时不会自动触发备份，"
                + "只有定时备份仍在工作。");
        }

        IReadOnlyList<JobView> jobs = await _queue.ListAsync(Ct).ConfigureAwait(false);

        // 任务名由宿主补齐：监控器自己只带一个快照式的名字（它是在 Reload 那一刻取的），
        // 而用户刚改过名字时以数据库为准更不容易困惑。
        Dictionary<Guid, string> names =
            await JobQueueQuery.GetTaskNamesAsync(_tasks, jobs, Ct).ConfigureAwait(false);

        if (statuses.Count > 0)
        {
            _logger.LogDebug("监控快照：{Count} 个根。", statuses.Count);
        }

        return new MonitoringSnapshotDto(
            [.. statuses.Select(s => Map(s, names))],
            await JobQueueQuery.BuildSnapshotAsync(_queue, _tasks, Ct).ConfigureAwait(false),

            // 引擎的 IChangeMonitor 不提供"最近触发记录"（它只有当前状态，没有历史）。
            // 如实返回空数组，而不是从任务日志里猜几条出来充数。
            RecentTriggers: [],
            ServerTime: _clock.UtcNow);
    }

    // ------------------------------------------------------------------

    private static RootMonitorStatusDto Map(RootMonitorStatus status, Dictionary<Guid, string> names) => new()
    {
        TaskId = status.TaskId,
        TaskName = names.TryGetValue(status.TaskId, out string? name) ? name : status.TaskName,
        SourceDir = status.SourceDir,

        // Disabled 是"没开监控"，不是"在轮询"。见类注释。
        State = MonitoringAccess.MapState(status.Mode) ?? "disabled",
        LastScanTime = status.LastScanUtc,

        // 引擎不提供"下次扫描时间"（轮询的下一次由它自己的节拍决定，不对外表达）。
        NextScanTime = null,
        PendingCount = status.PendingChangeCount,

        // 引擎只给出**待处理变更数**这一个总数，没有按目录拆分的清单
        //（待定表在监控模块内部，静默期倒计时是逐事件的）。所以这里为空数组，
        // 前端据此显示"共 N 个待处理变更"而不是逐目录倒计时。
        PendingDirs = [],
        LastFullScanTime = status.LastFullScanUtc,
        LastTriggerTime = status.LastTriggerUtc,
        LastTriggerReason = null,
        Error = status.Error,
    };
}
