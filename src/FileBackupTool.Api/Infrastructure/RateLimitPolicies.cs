namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 限流策略名（设计文档 05 §4）。
/// </summary>
/// <remarks>
/// 收敛成常量，避免"控制器里写 <c>"login"</c>、Program 里写 <c>"Login"</c>"
/// 这种只会在运行时以 500 形式暴露的拼写错位——那时限流已经形同没配。
/// </remarks>
public static class RateLimitPolicies
{
    /// <summary>全局配额：已认证 / 匿名两档（离线注册在 Program.cs 的 GlobalLimiter）。</summary>
    public const string Global = "global";

    /// <summary>登录：每 IP 每 5 分钟 5 次（设计文档 05 §4）。防止在线暴力破解。</summary>
    public const string Login = "login";

    /// <summary>写 / 重型操作：每分钟 30 次。防止误点与前端死循环造出大量备份作业。</summary>
    public const string Write = "write";
}
