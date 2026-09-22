using FileBackupTool.Core.Monitoring;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FileBackupTool.Monitoring;

/// <summary>变更监控模块的 DI 注册。</summary>
/// <remarks>
/// <b>宿主必须先注册的两样东西</b>（本模块不引用 Data，因此无法自己提供）：
/// <list type="bullet">
/// <item><see cref="IMonitoringTaskSource"/>——把 <c>BackupTask</c> 投影成
/// <see cref="MonitoredRoot"/>（见该接口的备注里有现成实现）；</item>
/// <item><see cref="FileBackupTool.Core.Monitoring.IBackupTrigger"/>——把"该根变脏了"
/// 投进全局作业队列；</item>
/// <item>以及 <c>AddOptions</c> 绑定 <c>Monitoring</c> / <c>Jobs</c> / <c>Storage</c> 三节。</item>
/// </list>
/// </remarks>
public static class MonitoringServiceCollectionExtensions
{
    /// <summary>注册变更监控（含它自己的 <c>BackgroundService</c> 宿主）。</summary>
    public static IServiceCollection AddFileBackupMonitoring(this IServiceCollection services)
    {
        services.TryAddSingleton<IFileWatcherFactory, PhysicalFileWatcherFactory>();

        // 单例，而且 IChangeMonitor 必须解析到**同一个实例**：
        // GetStatuses 看到的状态与后台循环正在维护的状态必须是同一份。
        services.TryAddSingleton<ChangeMonitor>();
        services.TryAddSingleton<IChangeMonitor>(sp => sp.GetRequiredService<ChangeMonitor>());

        services.AddHostedService<MonitoringHostedService>();

        return services;
    }
}
