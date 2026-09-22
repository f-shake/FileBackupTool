using FileBackupTool.Core.Monitoring;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 对**可选**的监控实现的访问。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用 <c>GetService</c> 而不是构造注入</b>：监控模块由 Monitoring 项目提供，
/// 宿主只用它的抽象（<see cref="IChangeMonitor"/>，定义在 Core）。
/// 构造函数注入一个"没注册就解析失败"的服务，会让整个控制器在监控缺席时直接 500；
/// 而<i>监控缺席是完全正常的状态</i>（也可以说是刚部署还没配任务）。
/// 所以这里按需查询，缺席时返回 null，由调用方给出"功能不可用"的明确答复。
/// </para>
/// <para>
/// <b>注意</b>：本类只依赖 Core 里的抽象，不引用 Monitoring 项目的任何具体类型——
/// 接线由 Program.cs 里那唯一一处注释负责（见 06 §8 的服务清单）。
/// </para>
/// </remarks>
public sealed class MonitoringAccess(IServiceProvider services, ILogger<MonitoringAccess> logger)
{
    private readonly IServiceProvider _services = services;
    private readonly ILogger<MonitoringAccess> _logger = logger;

    /// <summary>监控是否已接入（用于区分"没接入"与"接入但没有根"）。</summary>
    public bool IsAvailable => _services.GetService<IChangeMonitor>() is not null;

    /// <summary>各根状态；未接入时返回 null。</summary>
    public IReadOnlyList<RootMonitorStatus>? GetStatuses()
    {
        IChangeMonitor? monitor = _services.GetService<IChangeMonitor>();
        if (monitor is null)
        {
            return null;
        }

        try
        {
            return monitor.GetStatuses();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 监控状态查询失败不该让任务列表整个 500——它只是几个附加字段。
            _logger.LogWarning(ex, "读取监控状态失败，本次按「无监控信息」返回。");
            return null;
        }
    }

    /// <summary>按任务取监控模式字符串（watcher / polling / error）。未接入或无该根时返回 null。</summary>
    public string? GetModeForTask(Guid taskId)
    {
        IReadOnlyList<RootMonitorStatus>? statuses = GetStatuses();
        RootMonitorStatus? status = statuses?.FirstOrDefault(s => s.TaskId == taskId);
        return status is null ? null : MapState(status.Mode);
    }

    /// <summary>
    /// 按当前任务配置重建监控（任务增删改后调用）。未接入时什么都不做。
    /// </summary>
    /// <remarks>
    /// 失败只记日志、不向上抛：任务已经存进数据库了（那是用户真正要的结果），
    /// 因为监控重建失败就把这次保存报成失败，会让用户以为配置没生效而反复重试。
    /// 监控的兜底是周期性全量扫描，晚一轮不影响数据正确性。
    /// </remarks>
    public async Task ReloadAsync(CancellationToken ct)
    {
        IChangeMonitor? monitor = _services.GetService<IChangeMonitor>();
        if (monitor is null)
        {
            return;
        }

        try
        {
            await monitor.ReloadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "重建变更监控失败，任务配置已保存但监控可能滞后。");
        }
    }

    /// <summary>
    /// 手动触发一次扫描。<c>null</c> 表示监控未接入（调用方应回落到"入队一次备份"）。
    /// </summary>
    public async Task<bool?> RequestScanAsync(Guid taskId, bool fullScan, CancellationToken ct)
    {
        IChangeMonitor? monitor = _services.GetService<IChangeMonitor>();
        if (monitor is null)
        {
            return null;
        }

        try
        {
            return await monitor.RequestScanAsync(taskId, fullScan, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "向监控请求扫描失败（任务 {TaskId}）。", taskId);
            return false;
        }
    }

    /// <summary>
    /// 监控模式 → 前端契约的 <c>watcher / polling / error</c>。
    /// </summary>
    /// <remarks>
    /// <c>Disabled</c>（该根没开监控）**不**映射成 <c>polling</c>——轮询是在监控，只是方式不同，
    /// 把两者混为一谈会让用户以为监控在跑。所以它返回 null，由调用方省略该字段，
    /// 前端据此显示"未启用监控"。
    /// </remarks>
    public static string? MapState(RootMonitorMode mode) => mode switch
    {
        RootMonitorMode.Watcher => "watcher",
        RootMonitorMode.Polling => "polling",
        RootMonitorMode.Failed => "error",
        _ => null,
    };
}
