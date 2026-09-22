using System.Text.Json;
using System.Text.Json.Nodes;
using FileBackupTool.Core.Abstractions;
using Microsoft.AspNetCore.SignalR;

namespace FileBackupTool.Api.Hubs;

/// <summary>
/// 把引擎 / 后台服务发出的事件推到 SignalR（设计文档 06 §5 的十种事件全部在此落地）。
/// </summary>
/// <remarks>
/// <para>
/// <b>负载整形</b>：引擎发 <c>job.completed</c> 时带的是**引擎自己的结果记录**
/// （<c>BackupRunResult</c> / <c>RestoreResult</c> / <c>GcResult</c> / <c>IntegrityReport</c>），
/// 而前端契约要的是统一的 <c>{ success, snapshotId, failedFileCount, … }</c> 摘要。
/// 整形放在这里而不是去改引擎，是因为：引擎的结果类型是内部模型，一旦让它迁就线上形状，
/// 以后每加一个内部字段都得考虑"会不会泄露到前端"。这里是唯一的出口，只放该放的东西。
/// </para>
/// <para>
/// <b>投递范围</b>：默认广播给所有连接，只有 <c>log.appended</c> 走 <c>task:{taskId}</c> 分组。
/// 理由（这是对文档的一处有意取舍，已在交付说明中写明）：
/// <list type="bullet">
/// <item>前端的作业类事件（<c>job.*</c> / <c>backup.progress</c>）是在**全局 store** 上注册的
/// （<c>web/src/stores/tasks.ts</c>），任务列表页、监控页都要用；若只投给分组，
/// 没订阅该任务的页面会收不到作业状态，反而更糟。</item>
/// <item>前端的任务级事件（<c>monitor.changed</c> / <c>snapshot.committed</c>）自己会用
/// <c>payload.taskId</c> 过滤，广播给它是安全的。</item>
/// <item>唯独 <c>log.appended</c> 在文档里写明"可按任务订阅"，而且日志行数量级最大，
/// 所以它走分组，避免把别的任务的日志刷到当前页面。</item>
/// </list>
/// 广播而不是"All + Group 双发"是为了**避免重复投递**——同一个事件到同一个连接两次，
/// 会让前端的日志流出现重复行、完成提示弹两次。
/// </para>
/// <para>
/// <b>绝不抛异常</b>：引擎是以"发了就不管"的方式调用它的（<c>_ = events.PublishAsync(...)</c>，
/// 并把返回的 Task 丢掉）。这里若抛出，就会变成无人观察的 Task 异常，
/// 甚至可能让 SignalR 的发送失败反向影响备份作业。所以全部吞掉并记 Warning。
/// </para>
/// </remarks>
public sealed class SignalRJobEventPublisher(
    IHubContext<EventsHub> hub,
    IClock clock,
    ILogger<SignalRJobEventPublisher> logger) : IJobEventPublisher
{
    /// <summary>与线上形状一致的序列化设置（camelCase，与 SignalR 自己的默认一致）。</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHubContext<EventsHub> _hub = hub;
    private readonly IClock _clock = clock;
    private readonly ILogger<SignalRJobEventPublisher> _logger = logger;

    /// <summary>走 <c>task:{taskId}</c> 分组投递的事件。见类注释的取舍说明。</summary>
    private static readonly HashSet<string> TaskScopedEvents = new(StringComparer.Ordinal)
    {
        "log.appended",
    };

    public async Task PublishAsync(string eventName, object payload, CancellationToken ct)
    {
        try
        {
            JsonNode? node = Shape(eventName, JsonSerializer.SerializeToNode(payload, Json));

            if (TaskScopedEvents.Contains(eventName))
            {
                string? group = TaskGroupName.FromPayload(node);
                if (group is not null)
                {
                    await _hub.Clients.Group(group).SendAsync(eventName, node, ct).ConfigureAwait(false);
                    return;
                }
            }

            await _hub.Clients.All.SendAsync(eventName, node, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "推送实时事件 {Event} 失败（该事件已丢失，前端会靠轮询补齐）。", eventName);
        }
    }

    /// <summary>快照提交（前端据此刷新快照列表）。备份成功后由作业事件触发。</summary>
    public Task SnapshotCommittedAsync(Guid taskId, int snapshotId, CancellationToken ct) =>
        PublishAsync("snapshot.committed", new { taskId, snapshotId }, ct);

    /// <summary>业务日志新增（日志流）。</summary>
    public Task LogAppendedAsync(
        Guid taskId,
        int? snapshotId,
        string level,
        DateTime timeUtc,
        string message,
        CancellationToken ct) =>
        PublishAsync("log.appended", new { taskId, snapshotId, level, time = timeUtc, message }, ct);

    /// <summary>监控状态 / 待处理变更数变化。</summary>
    public Task MonitorChangedAsync(Guid taskId, string rootState, int pendingCount, CancellationToken ct) =>
        PublishAsync("monitor.changed", new { taskId, rootState, pendingCount }, ct);

    // ------------------------------------------------------------------
    // 负载整形
    // ------------------------------------------------------------------

    private JsonNode? Shape(string eventName, JsonNode? payload)
    {
        if (eventName != "job.completed" || payload is not JsonObject root)
        {
            return payload;
        }

        if (TryGet(root, "result", out JsonNode? raw) && raw is JsonObject result)
        {
            root["result"] = Summarize(result);
        }

        return root;
    }

    /// <summary>
    /// 把引擎的结果记录压成前端契约的摘要。
    /// </summary>
    /// <remarks>
    /// 按**字段是否存在**来判别类型，而不是按类型名：负载是序列化后的 JSON，
    /// 走到这里已经没有运行时类型了。四种结果的字段互不重叠（各自的主计数名不同），
    /// 所以判别是可靠的。
    /// </remarks>
    private JsonObject Summarize(JsonObject result)
    {
        var summary = new JsonObject
        {
            ["success"] = true,
            ["failedFileCount"] = 0,
        };

        if (TryLong(result, "failedFileCount", out long failed))
        {
            // 备份：BackupRunResult。
            bool aborted = TryString(result, "status", out string? status)
                           && string.Equals(status, "Aborted", StringComparison.OrdinalIgnoreCase);

            summary["success"] = !aborted && failed == 0;
            summary["failedFileCount"] = failed;

            if (TryLong(result, "snapshotId", out long snapshotId))
            {
                summary["snapshotId"] = snapshotId;
            }

            if (TryLong(result, "createdFileCount", out long created))
            {
                summary["createdFileCount"] = created;
            }

            if (TryLong(result, "modifiedFileCount", out long modified))
            {
                summary["modifiedFileCount"] = modified;
            }

            if (TryLong(result, "deletedFileCount", out long deleted))
            {
                summary["deletedFileCount"] = deleted;
            }

            if (TryLong(result, "totalBytes", out long bytes))
            {
                summary["totalBytes"] = bytes;
            }

            summary["message"] = TryString(result, "abortReason", out string? reason) ? reason : null;
            return summary;
        }

        if (TryLong(result, "restoredCount", out long restored))
        {
            // 还原：RestoreResult。失败的语义是"未能写出的文件数"。
            _ = restored;
            long restoreFailed = TryLong(result, "failedCount", out long f) ? f : 0;
            summary["success"] = restoreFailed == 0;
            summary["failedFileCount"] = restoreFailed;

            if (TryLong(result, "restoredBytes", out long restoredBytes))
            {
                summary["totalBytes"] = restoredBytes;
            }

            long unavailable = TryLong(result, "unavailableCount", out long u) ? u : 0;
            summary["message"] = unavailable > 0
                ? $"有 {unavailable} 个文件不存在实际备份文件，已跳过。"
                : null;
            return summary;
        }

        if (TryLong(result, "recycledBlobCount", out long recycled))
        {
            // 回收：GcResult。丢失的 blob 数就是"这次回收发现的问题数"。
            long lost = CountOf(result, "lostBlobs");
            summary["success"] = lost == 0;
            summary["failedFileCount"] = lost;

            if (TryLong(result, "recycledBytes", out long recycledBytes))
            {
                summary["totalBytes"] = recycledBytes;
            }

            summary["message"] = $"已回收 {recycled} 个文件块。";
            return summary;
        }

        if (TryLong(result, "checkedBlobCount", out long checkedBlobs))
        {
            // 完整校验：IntegrityReport。
            long redundant = CountOf(result, "redundantBlobs");
            long lost = CountOf(result, "lostBlobs");
            long broken = CountOf(result, "brokenChains");
            long corrupted = CountOf(result, "corruptedBlobs");
            long inconsistent = CountOf(result, "mirrorInconsistencies");

            summary["success"] = redundant + lost + broken + corrupted + inconsistent == 0;
            summary["failedFileCount"] = lost;
            summary["message"] =
                $"校验了 {checkedBlobs} 个文件块：多余 {redundant}、丢失 {lost}、链断裂 {broken}、" +
                $"哈希不符 {corrupted}、镜像不一致 {inconsistent}。";
            return summary;
        }

        return summary;
    }

    private static long CountOf(JsonObject obj, string name) =>
        TryGet(obj, name, out JsonNode? node) && node is JsonArray array ? array.Count : 0;

    private static bool TryGet(JsonObject obj, string name, out JsonNode? value)
    {
        foreach ((string key, JsonNode? node) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = node;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryLong(JsonObject obj, string name, out long value)
    {
        value = 0;
        return TryGet(obj, name, out JsonNode? node)
               && node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value);
    }

    private static bool TryString(JsonObject obj, string name, out string? value)
    {
        value = null;
        return TryGet(obj, name, out JsonNode? node)
               && node is JsonValue jsonValue
               && jsonValue.TryGetValue(out value);
    }
}
