namespace FileBackupTool.Api.Contracts;

/// <summary>登录请求（06 §4.9）。</summary>
public sealed record LoginRequestDto
{
    public string? Username { get; init; }

    public string? Password { get; init; }

    /// <summary>已绑定 TOTP 时的动态码。</summary>
    public string? TotpCode { get; init; }

    /// <summary>或用一次性恢复码代替。</summary>
    public string? RecoveryCode { get; init; }
}

/// <summary>
/// 登录响应（05 §2.2）。
/// </summary>
/// <remarks>
/// 三态：<see cref="Authenticated"/>（已拿到会话）/ <see cref="RequiresTotp"/>（要动态码）/
/// <see cref="RequiresTotpSetup"/>（首次登录，必须先进强制绑定流程）。
/// 前端的路由守卫依赖后两者把用户挡在业务功能之外。
/// </remarks>
public sealed record LoginResultDto
{
    public bool RequiresTotp { get; init; }

    public bool RequiresTotpSetup { get; init; }

    public bool Authenticated { get; init; }

    public CurrentUserDto? User { get; init; }

    /// <summary>绑定完成后**一次性**返回的恢复码（仅此一次可见）。</summary>
    public IReadOnlyList<string>? RecoveryCodes { get; init; }

    public string? Message { get; init; }
}

/// <summary>当前用户信息（<c>GET /api/auth/me</c>）。</summary>
public sealed record CurrentUserDto
{
    public required Guid Id { get; init; }

    public required string Username { get; init; }

    /// <summary>是否已绑定 TOTP（Authenticator 密钥已存在）。</summary>
    public bool TotpEnabled { get; init; }

    /// <summary>
    /// 两步验证是否已确认。**这是强制绑定闸门的判据**：
    /// 为 false 时该用户不得访问任何业务端点（05 §2.2）。
    /// </summary>
    public bool TwoFactorConfirmed { get; init; }

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

/// <summary>TOTP 绑定的第一步结果。</summary>
public sealed record TotpSetupResultDto
{
    /// <summary>Base32 共享密钥，供手输。</summary>
    public required string SharedKey { get; init; }

    /// <summary>
    /// <c>otpauth://totp/...</c> URI，含 secret。
    /// </summary>
    /// <remarks>
    /// 设计决策：后端**不生成二维码图片**，只给 URI，由前端渲染。
    /// 这样后端不引入任何 QR/图像依赖，也少一条会泄露 secret 的响应体。
    /// </remarks>
    public required string AuthenticatorUri { get; init; }

    /// <summary>恒为 null：后端不生成图片（见 <see cref="AuthenticatorUri"/>）。</summary>
    public string? QrCodePngBase64 { get; init; }
}

/// <summary>
/// `POST /api/auth/totp/setup` 的请求体。
/// </summary>
/// <remarks>
/// <see cref="Password"/> **只在"已绑定情况下重绑"时必填**（首次绑定不需要：用户刚刚用密码登录过）。
/// 原因见 <c>AuthController.RequireCurrentPasswordAsync</c> 的说明。
/// </remarks>
public sealed record TotpSetupRequestDto(string? Password = null);

/// <summary>
/// `POST /api/auth/totp/confirm` 的请求体。
/// </summary>
/// <remarks><see cref="Password"/> 的必填规则同 <see cref="TotpSetupRequestDto"/>。</remarks>
public sealed record TotpConfirmRequestDto(string? Code, string? Password = null);

public sealed record RecoveryCodesRequestDto(string? Password);

public sealed record RecoveryCodesResultDto(IReadOnlyList<string> RecoveryCodes);

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
