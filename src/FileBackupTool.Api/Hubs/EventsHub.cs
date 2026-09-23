using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FileBackupTool.Api.Hubs;

/// <summary>
/// 实时事件 Hub（设计文档 06 §5）。路径 <c>/hubs/events</c>。
/// </summary>
/// <remarks>
/// <para>
/// <b>必须认证</b>：Hub 是业务数据的实时通道（进度、日志、监控状态、快照提交），
/// 匿名能连等于绕过 REST 的鉴权。因此类上带 <c>[Authorize]</c>，
/// 未登录时 negotiate 会失败，前端据此降级为轮询，而轮询同样被 401 挡住，行为一致。
/// </para>
/// <para>
/// <b>为什么用 Cookie 认证而不是 access_token 查询串</b>：SignalR 的
/// <c>access_token</c> 方案用于浏览器之外的客户端；浏览器的 WebSocket 握手会自动带上
/// 同源 Cookie（前端 <c>withCredentials: true</c>），无需把凭据放进 URL。
/// 放进 URL 的凭据会进访问日志、进 Referer、进浏览器历史——那是不必要的暴露面。
/// </para>
/// </remarks>
[Authorize]
public sealed class EventsHub(ILogger<EventsHub> logger) : Hub
{
    private readonly ILogger<EventsHub> _logger = logger;

    /// <summary>订阅某个任务的分组（前端 <c>realtime.subscribeTask()</c>）。</summary>
    /// <remarks>
    /// 方法名是**前端契约**（<c>web/src/realtime/client.ts</c> 里的
    /// <c>METHOD_SUBSCRIBE_TASK</c>），改名必须两端一起改。
    /// </remarks>
    public async Task SubscribeTask(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        string group = TaskGroupName.Normalize(taskId);
        await Groups.AddToGroupAsync(Context.ConnectionId, group).ConfigureAwait(false);
        _logger.LogDebug("连接 {ConnectionId} 订阅了分组 {Group}。", Context.ConnectionId, group);
    }

    public async Task UnsubscribeTask(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return;
        }

        string group = TaskGroupName.Normalize(taskId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, group).ConfigureAwait(false);
        _logger.LogDebug("连接 {ConnectionId} 退订了分组 {Group}。", Context.ConnectionId, group);
    }

    public override Task OnConnectedAsync()
    {
        // 不记审计：连接与断开是秒级高频事件，写进审计表会把真正重要的登录/改配置
        // 淹没在噪声里。连接的身份信息在 Serilog 的请求日志里已经有了。
        _logger.LogInformation("实时通道已连接：{ConnectionId}（用户 {User}）。",
            Context.ConnectionId, Context.User?.Identity?.Name ?? "(匿名)");

        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is not null)
        {
            _logger.LogDebug(exception, "实时通道断开（异常）：{ConnectionId}。", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
