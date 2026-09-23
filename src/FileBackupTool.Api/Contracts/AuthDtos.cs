namespace FileBackupTool.Api.Contracts;

/// <summary>登录请求（06 §4.9）。</summary>
/// <remarks>
/// 单密码模式：请求体里**只有密码**。账号不由用户提供，服务端按
/// <c>SecurityOptions.EffectiveSeedUserName</c> 解析出那唯一的账号。
/// </remarks>
public sealed record LoginRequestDto
{
    public string? Password { get; init; }
}

/// <summary>当前用户信息（<c>GET /api/auth/me</c>，也是登录成功的响应体）。</summary>
public sealed record CurrentUserDto
{
    public required Guid Id { get; init; }

    public required string Username { get; init; }

    public DateTime? LastLoginTime { get; init; }

    public string? LastLoginIp { get; init; }
}

/// <summary>改密码（靠 Identity 安全戳一次性失效全部会话，05 §3）。</summary>
public sealed record ChangePasswordRequestDto
{
    public string? CurrentPassword { get; init; }

    public string? NewPassword { get; init; }

    public string? ConfirmPassword { get; init; }
}

/// <summary>一个登录会话（05 §3：允许多会话，但必须能看见并单独踢出）。</summary>
public sealed record SessionInfoDto(
    string Id,
    DateTime CreatedAt,
    DateTime LastSeenAt,
    string? SourceIp,
    string? UserAgent,
    bool Current);

/// <summary>
/// <c>GET /api/auth/csrf</c> 的响应：写操作要放进 <c>X-XSRF-TOKEN</c> 头的**请求令牌**。
/// </summary>
/// <remarks>
/// 它是 request token，**不是** XSRF-TOKEN cookie 的值——两者配对但不同，
/// 拿 cookie 的值当请求头会被判为 "cookie token and the request token were swapped"。
/// 令牌与调用方的 cookie 绑定，所以客户端必须同时带上 cookie（前端已开 withCredentials）。
/// </remarks>
public sealed record CsrfTokenDto(string Token);
