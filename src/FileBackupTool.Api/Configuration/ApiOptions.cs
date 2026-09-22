namespace FileBackupTool.Api.Configuration;

/// <summary>
/// 只属于宿主层的配置节。
/// </summary>
/// <remarks>
/// Core 里的 <c>SecurityOptions</c> / <c>ReverseProxyOptions</c> / <c>RetentionOptions</c>
/// 是**引擎与监控也要用**的参数；而限流窗口、调度节拍、HSTS 有效期这些
/// **只有宿主关心**，放 Core 会让领域层背上一堆 ASP.NET 语义的字段。
/// 因此在这里就地定义，节名与 appsettings.json 对齐（同一个节可以被多个类型绑定，
/// 各自只取自己有的键——JsonConfigurationProvider 不会因为"多余的键"报错）。
/// </remarks>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>调度节拍（秒）。设计文档 06 §8：<c>SchedulerService</c> 每 60 秒检查一次。</summary>
    public int TickSeconds { get; set; } = 60;
}

/// <summary>保留策略的节拍（<c>Retention</c> 节里的 <c>TickMinutes</c>）。</summary>
public sealed class RetentionTickOptions
{
    public const string SectionName = "Retention";

    /// <summary>设计文档 06 §8：<c>RetentionService</c> 每小时一次。</summary>
    public int TickMinutes { get; set; } = 60;
}

/// <summary>登录限流（设计文档 05 §4：每 IP 每 5 分钟 5 次）。</summary>
public sealed class LoginRateLimitOptions
{
    public const string SectionName = "Security:LoginRateLimit";

    public int PermitLimit { get; set; } = 5;

    public int WindowMinutes { get; set; } = 5;
}

/// <summary>账户锁定（设计文档 05 §4：连续 5 次失败锁 15 分钟）。</summary>
/// <remarks>
/// 刻意不叫 <c>LockoutOptions</c>：Identity 自己有一个同名类型，
/// 两个都 <c>using</c> 的时候会歧义，而那种歧义报错很难一眼看出原因。
/// </remarks>
public sealed class AccountLockoutOptions
{
    public const string SectionName = "Security:Lockout";

    public int MaxFailedAttempts { get; set; } = 5;

    public int Minutes { get; set; } = 15;
}

/// <summary>全局 API 限流（设计文档 05 §4）。</summary>
public sealed class ApiRateLimitOptions
{
    public const string SectionName = "Security:ApiRateLimit";

    /// <summary>已认证用户的每分钟配额。单用户系统里这个值只需要挡住"前端死循环"。</summary>
    public int AuthenticatedPerMinute { get; set; } = 600;

    /// <summary>匿名（未登录）的每分钟配额。**必须远小于认证用户**：暴力破解与爬取都从匿名开始。</summary>
    public int AnonymousPerMinute { get; set; } = 60;
}

/// <summary>写操作 / 重型操作的限流（设计文档 05 §4）。</summary>
public sealed class WriteRateLimitOptions
{
    public const string SectionName = "Security:WriteRateLimit";

    public int PermitLimit { get; set; } = 30;

    public int WindowSeconds { get; set; } = 60;
}

/// <summary>HSTS 参数（<c>ReverseProxy</c> 节里的 <c>HstsMaxAgeDays</c>）。</summary>
public sealed class HstsApiOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>HSTS 有效期。一年是"预加载列表"的门槛值，短于它等于没设。</summary>
    public int HstsMaxAgeDays { get; set; } = 365;
}
