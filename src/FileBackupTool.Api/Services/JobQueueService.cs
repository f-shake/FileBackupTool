using FileBackupTool.Core.Abstractions;
using FileBackupTool.Engine;
using Microsoft.Extensions.Hosting;

namespace FileBackupTool.Api.Services;

/// <summary>
/// 把全局串行 IO 作业队列的消费循环挂到宿主生命周期上（设计文档 06 §8 的 <c>JobQueueService</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么引擎自己不做成 <c>BackgroundService</c></b>：引擎层不引用
/// <c>Microsoft.Extensions.Hosting</c>（见 <c>JobQueue</c> 的类注释），它只提供
/// "启动/停止消费者循环"的纯逻辑，方便单元测试直接驱动。宿主化的这层薄壳属于 Api 层。
/// </para>
/// <para>
/// <b>为什么要强转成具体类型</b>：<c>Start(CancellationToken)</c> 不在
/// <see cref="IJobQueue"/> 接口上（它是宿主接线用的方法，不是队列的业务契约）。
/// 这里从 DI 取回的是同一个单例（引擎以 <c>TryAddSingleton&lt;IJobQueue, JobQueue&gt;</c> 注册，
/// 所以 <c>IJobQueue</c> 解析出来的实例**就是** <c>JobQueue</c>），
/// 强转成功即证明它确实是那个实现；不成功说明有人替换了实现，
/// 那就明确报错并让宿主起不来，而不是静默地"队列永远不消费"。
/// 静默的后果是所有备份永远停在"排队中"——这是最糟的失败模式。
/// </para>
/// <para>
/// <see cref="BackgroundService.ExecuteAsync"/> **立即返回**是正确的：队列是事件驱动的
/// （单消费者 Channel），没有需要轮询的东西。这里只负责把宿主的停止信号接进去，
/// 让消费循环在停机时能干净退出（当前作业会被取消，快照由启动自愈标记为 Aborted）。
/// </para>
/// </remarks>
public sealed class JobQueueService(IJobQueue queue, ILogger<JobQueueService> logger) : BackgroundService
{
    private readonly IJobQueue _queue = queue;
    private readonly ILogger<JobQueueService> _logger = logger;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_queue is not JobQueue engineQueue)
        {
            throw new InvalidOperationException(
                $"作业队列的实现不是 {nameof(JobQueue)}（实际为 {_queue.GetType().FullName}），" +
                "无法启动消费循环。请检查 DI 注册。");
        }

        engineQueue.Start(stoppingToken);
        _logger.LogInformation("全局串行 IO 作业队列已启动（任意时刻最多一个后台 IO 作业）。");

        return Task.CompletedTask;
    }
}
