using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Monitoring;

/// <summary>
/// 把 <see cref="ChangeMonitor"/> 接到宿主生命周期上的 <c>BackgroundService</c>。
/// </summary>
/// <remarks>
/// <b>宿主化方式的取舍</b>：<see cref="IChangeMonitor"/> 的契约里只有
/// <c>GetStatuses</c> / <c>ReloadAsync</c> / <c>RequestScanAsync</c>，没有任何"启动/停止"——
/// 这意味着<b>监控循环怎么跑是宿主的事</b>。设计文档 01 §4.3 要求"每个源目录一个监控器，
/// 运行在独立的 <c>BackgroundService</c> 中"，所以这里提供一个现成的包装，
/// 让 Api 层一行注册就能满足该要求：
/// <code>
/// services.AddFileBackupMonitoring();
/// </code>
/// 若宿主想自己控制（例如要先等数据库迁移完成再开始监控），也可以不注册本类，
/// 直接注入 <see cref="ChangeMonitor"/> 并自行调用
/// <see cref="ChangeMonitor.ReloadAsync"/> + <see cref="ChangeMonitor.RunAsync"/>——
/// 这两个方法是公开的，就是为了这条退路。
/// <para>
/// 顺序是刻意固定的：<b>先 ReloadAsync（拿到根）→ 再启动自检（钩子 ③）→ 才进入循环</b>。
/// 自检必须在循环之前：它的语义是"启动时先扫一次"，而不是"启动后随便什么时候扫一次"。
/// </para>
/// </remarks>
public sealed class MonitoringHostedService(
    ChangeMonitor monitor,
    ILogger<MonitoringHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await monitor.ReloadAsync(stoppingToken).ConfigureAwait(false);

            // 钩子 ③：无条件全量扫描。它同时是"监控到底有没有起来"的唯一可靠证据——
            // 参考项目那次事故（InitializeAsync 没被调用）就是靠这种自检才能被发现。
            await monitor.StartupSelfCheckAsync(stoppingToken).ConfigureAwait(false);

            await monitor.RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("变更监控服务正在停止。");
        }
        catch (Exception ex)
        {
            // 监控停摆是**静默失效**的温床：进程还活着、UI 还显示"运行中"，但一个事件都不会来。
            // 所以这里必须留下 Critical 级的痕迹。
            // 但**不 rethrow**：默认行为会把整个宿主停掉，而用户此刻正需要 UI 去查发生了什么。
            // 实时备份失效不等于系统失效——调度触发的备份、浏览、还原都还能用。
            logger.LogCritical(ex, "变更监控服务异常终止。实时备份已停止，只能依赖调度触发的备份。");
        }
    }
}
