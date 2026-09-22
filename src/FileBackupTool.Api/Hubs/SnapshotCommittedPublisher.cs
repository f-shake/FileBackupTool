using System.Text.Json;
using System.Text.Json.Nodes;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace FileBackupTool.Api.Hubs;

/// <summary>
/// <see cref="IJobEventPublisher"/> 的装饰器：在 <c>job.completed</c> 之后补发
/// <c>snapshot.committed</c>（设计文档 06 §5 的事件清单）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要补这一发</b>：引擎在备份结束时只发 <c>job.completed</c>，
/// 它的负载是 <c>{ jobId, result }</c>——而前端契约里 <c>snapshot.committed</c> 要的是
/// <c>{ taskId, snapshotId }</c>。两者对不上，于是前端"备份成功后刷新快照列表"这条路径
/// 只能靠轮询。引擎是不得修改的（本层的约束），所以在宿主侧补这一个事件。
/// </para>
/// <para>
/// <b>为什么用装饰器而不是去改 JobQueue 的调用点</b>：那样要把"推事件"的业务知识
/// 塞进引擎。装饰器是唯一入口，且行为可被单测覆盖。
/// </para>
/// <para>
/// <b>为什么要惰性解析 <see cref="IJobQueue"/></b>：<c>JobQueue</c> 的构造函数需要
/// <c>IJobEventPublisher</c>，而本类要拿到 <c>JobQueue</c> 才能从 <c>jobId</c> 反查
/// <c>taskId</c>/<c>snapshotId</c>——直接构造注入会形成
/// <c>JobQueue → IJobEventPublisher → IJobQueue</c> 的循环。
/// 惰性解析（调用时才 <c>GetRequiredService</c>）把这个环打断：
/// 构造期不依赖它，而调用期队列早已构造完成。
/// </para>
/// <para>
/// <b>只对"真正提交了快照"的作业补发</b>：校验 / 回收类作业的结果里没有快照；
/// 被中止的备份虽然也有一行快照记录，但它的状态是 <c>Aborted</c>——
/// 把那种情况报成 <c>committed</c> 会让前端显示"已提交"这个错误的结论
/// （中止的快照由 <c>job.aborted</c> 已经告知了）。
/// </para>
/// <para>
/// <b>绝不抛异常</b>：引擎是以"发了就不管"的方式调用发布器的
/// （<c>_ = events.PublishAsync(...)</c>），抛出会成为无人观察的 Task 异常。
/// </para>
/// </remarks>
public sealed class SnapshotCommittedPublisher(
    SignalRJobEventPublisher inner,
    IServiceProvider services,
    ILogger<SnapshotCommittedPublisher> logger) : IJobEventPublisher
{
    private readonly SignalRJobEventPublisher _inner = inner;
    private readonly IServiceProvider _services = services;
    private readonly ILogger<SnapshotCommittedPublisher> _logger = logger;

    public async Task PublishAsync(string eventName, object payload, CancellationToken ct)
    {
        // 先原样转发，保证 job.* 事件的时序不受本装饰器影响。
        await _inner.PublishAsync(eventName, payload, ct).ConfigureAwait(false);

        if (!string.Equals(eventName, "job.completed", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await PublishSnapshotCommittedAsync(payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 停机中，前端会靠轮询补齐。
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "补发 snapshot.committed 失败（快照已在库里，前端轮询仍能看到）。");
        }
    }

    private async Task PublishSnapshotCommittedAsync(object payload, CancellationToken ct)
    {
        string? jobId = ReadJobId(payload);
        if (jobId is null)
        {
            return;
        }

        // 与 RealtimeLogSink 同样的取舍：解析失败一律当作"没有可补发的事件"。
        IJobQueue? queue = _services.GetService<IJobQueue>();
        if (queue is null)
        {
            return;
        }

        JobView? view = await queue.GetAsync(jobId, ct).ConfigureAwait(false);

        if (view?.BackupResult is not { } result || view.TaskId is not { } taskId)
        {
            // 校验 / 回收 / 还原类作业：没有新快照，无需补发。
            return;
        }

        if (result.Status != SnapshotStatus.Completed)
        {
            _logger.LogDebug(
                "作业 {JobId} 的快照 {SnapshotId} 状态为 {Status}，按未提交处理，不发 snapshot.committed。",
                jobId, result.SnapshotId, result.Status);
            return;
        }

        await _inner.SnapshotCommittedAsync(taskId, result.SnapshotId, ct).ConfigureAwait(false);
    }

    /// <summary>从 <c>job.completed</c> 的匿名负载里取出 <c>jobId</c>。</summary>
    /// <remarks>
    /// 负载是匿名对象，没有共同接口可依赖，只能按字段名读。用
    /// <see cref="JsonNode"/> 而不是反射：反射取的是编译期的属性名，
    /// 而序列化到前端用的是 camelCase——两者一旦不一致，这里会静默失效。
    /// </remarks>
    private static string? ReadJobId(object payload)
    {
        try
        {
            if (JsonSerializer.SerializeToNode(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                is JsonObject obj)
            {
                foreach ((string key, JsonNode? node) in obj)
                {
                    if (string.Equals(key, "jobId", StringComparison.OrdinalIgnoreCase) &&
                        node is JsonValue value &&
                        value.TryGetValue(out string? jobId))
                    {
                        return jobId;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 负载不可序列化（理论上不会发生）。返回 null = 不补发。
        }

        return null;
    }
}
