namespace FileBackupTool.Api.Hubs;

/// <summary>
/// SignalR 分组的命名规则（设计文档 06 §5：<c>task:{taskId}</c>）。
/// </summary>
/// <remarks>
/// 为什么要有这个类型：分组名是**字符串契约**，发布侧（后端）与订阅侧（前端
/// <c>realtime.subscribeTask(props.id)</c>）必须**逐字符相同**，否则事件会静默丢失——
/// 不报错、不抛异常，只是永远收不到。集中在一处并在两侧都做同一种归一化，
/// 是唯一能让"任务 id 用 <c>D</c> 还是 <c>N</c> 格式、大写还是小写"不成为隐患的办法。
/// </remarks>
public static class TaskGroupName
{
    private const string Prefix = "task:";

    /// <summary>用任务 id 生成分组名（统一成 Guid 的 <c>d</c> 小写格式）。</summary>
    public static string For(Guid taskId) => Prefix + taskId.ToString("d");

    /// <summary>
    /// 把任意来源的任务 id 字符串归一化成分组名。
    /// </summary>
    /// <remarks>
    /// 能解析成 Guid 的一律走 <see cref="Guid.ToString(string)"/>（形状统一）；
    /// 解析不了的（前端理论上不会发，但请求来自网络，不能假设）按原样小写使用。
    /// 这里**不做白名单校验**：分组名不构成安全边界——订阅到别人的分组只意味着
    /// "多收到一些自己本来就有权看的事件"（单用户系统里更是如此），
    /// 真正的边界是 Hub 上的 <c>[Authorize]</c>。
    /// </remarks>
    public static string Normalize(string? taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return Prefix;
        }

        string value = taskId.Trim();
        return Guid.TryParse(value, out Guid parsed) ? For(parsed) : Prefix + value.ToLowerInvariant();
    }

    /// <summary>从事件负载里取出 <c>taskId</c> 对应的分组名；没有则返回 null。</summary>
    public static string? FromPayload(System.Text.Json.Nodes.JsonNode? payload) =>
        payload is System.Text.Json.Nodes.JsonObject obj && TryGetString(obj, "taskId", out string? raw)
            ? Normalize(raw)
            : null;

    private static bool TryGetString(System.Text.Json.Nodes.JsonObject obj, string name, out string? value)
    {
        foreach ((string key, System.Text.Json.Nodes.JsonNode? node) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = node?.GetValue<string>();
                return true;
            }
        }

        value = null;
        return false;
    }
}
