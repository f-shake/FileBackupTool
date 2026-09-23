using FileBackupTool.Api.Configuration;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Api.Security;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Enums;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 认证（设计文档 06 §4.9、05 §2/§3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>单密码模式</b>：登录请求体里只有密码。账号不由用户提供，服务端按
/// <see cref="SecurityOptions.EffectiveSeedUserName"/> 解析出那唯一的账号并校验密码，
/// 校验通过即签发会话。没有第二因素，也没有"密码对了但还不能进"的中间态。
/// </para>
/// <para>
/// <b>为什么保留 Identity</b>：账号存储、PBKDF2 口令哈希、账户锁定都是 Identity 的既有能力，
/// 自己实现一遍只会引入"锁定恒不生效"或哈希参数写错这类静默故障。这里只做减法，不做替换。
/// </para>
/// <para>
/// <b>密码是唯一的一道门，所以登录的防护一个都没减</b>：登录端点有每 IP 每 5 分钟 5 次的限流，
/// 连续失败 5 次锁定账户 15 分钟（Identity 维护），失败一律写审计。
/// </para>
/// </remarks>
[Route("api/auth")]
public sealed class AuthController(
    UserManager<IdentityUser<Guid>> users,
    SignInManager<IdentityUser<Guid>> signIn,
    SessionRegistry sessions,
    AuditLogger audit,
    IAntiforgery antiforgery,
    IOptions<AntiforgeryOptions> antiforgeryOptions,
    IOptions<SecurityOptions> securityOptions,
    IOptions<AccountLockoutOptions> lockoutOptions,
    ILogger<AuthController> logger) : ApiControllerBase
{
    private readonly UserManager<IdentityUser<Guid>> _users = users;
    private readonly SignInManager<IdentityUser<Guid>> _signIn = signIn;
    private readonly SessionRegistry _sessions = sessions;
    private readonly AuditLogger _audit = audit;
    private readonly IAntiforgery _antiforgery = antiforgery;
    private readonly AntiforgeryOptions _antiforgeryOptions = antiforgeryOptions.Value;
    private readonly SecurityOptions _security = securityOptions.Value;
    private readonly AccountLockoutOptions _lockout = lockoutOptions.Value;
    private readonly ILogger<AuthController> _logger = logger;

    /// <summary>
    /// 下发 antiforgery **请求令牌**（写操作要放在 <c>X-XSRF-TOKEN</c> 头里）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么不能只靠 cookie</b>：ASP.NET Core 的 antiforgery 是"配对令牌"模型——
    /// Cookie 里存的是 <i>cookie token</i>，请求头里要带的是**另一个** <i>request token</i>，
    /// 校验时比较两者的配对关系。所以客户端**无法**从 cookie 值推出该往头上写什么
    /// （Angular 那套"把 XSRF-TOKEN cookie 的值回填到请求头"的惯例在这里不成立，
    /// 直接照抄会得到 <c>The cookie token and the request token were swapped</c>）。
    /// </para>
    /// <para>
    /// 因此请求令牌只能由**响应体**交给前端。这个端点就是那个来源：
    /// 匿名的（登录前就要能拿到）、安全的（GET，无副作用）。
    /// </para>
    /// <para>
    /// <b>它不泄露秘密</b>：令牌由 Data Protection 密钥环派生，且与调用方自己的
    /// <c>XSRF-TOKEN</c> cookie 绑定——攻击者站点读不到这个响应（同源策略），
    /// 也读不到那个 cookie（<c>SameSite=Strict</c>）。这正是 CSRF 防护的全部原理。
    /// </para>
    /// </remarks>
    [HttpGet("csrf")]
    [AllowAnonymous]
    public CsrfTokenDto Csrf()
    {
        // GetAndStoreTokens 会顺带保证 XSRF-TOKEN cookie 已经就位
        //（内部会复用已有的合法 cookie，重复调用是幂等的）。
        AntiforgeryTokenSet tokens = _antiforgery.GetAndStoreTokens(HttpContext);

        return new CsrfTokenDto(tokens.RequestToken ?? string.Empty);
    }

    /// <summary>
    /// 身份变化后必须作废 antiforgery 令牌：请求令牌与"签发时的用户身份"绑定，
    /// 身份一变旧的就再也过不了校验。删掉 cookie，下一个请求就会按新身份重发一对。
    /// </summary>
    /// <remarks>
    /// 不做这一步的症状很隐蔽：**登录之后所有写操作都 400**，
    /// 服务端日志说 <c>meant for a different claims-based user than the current user</c>，
    /// 而读操作（GET）全部正常——用户会以为"只有保存坏了"。
    /// 登出时同样要清：否则匿名身份会继承上一个已登录身份的令牌。
    /// </remarks>
    private void ResetAntiforgery()
    {
        Microsoft.AspNetCore.Http.CookieBuilder builder = _antiforgeryOptions.Cookie;
        string name = builder.Name ?? "XSRF-TOKEN";

        // **必须带上与签发时一致的 Path / Domain。**
        // 只 `Delete(name)` 时，浏览器收到的是一条**没有 Path** 的删除指令，于是它会按
        // "当前请求路径所在的目录"去匹配（登录是 `/api/auth/login` → `/api/auth`），
        // 而 antiforgery 的 cookie token 落在应用路径根（`/`）——两者对不上，
        // **这个 cookie 根本删不掉**（真正兜住的是前端那次 `resetCsrfToken()`）。
        //
        // 删除的匹配键是 **name + domain + path**，所以这两个字段必须与签发时一致；
        // 其余属性（Secure / SameSite / MaxAge）不参与匹配，不用管。
        var options = new CookieOptions
        {
            Path = builder.Path ?? "/",
            Domain = builder.Domain,
        };

        HttpContext.Response.Cookies.Delete(name, options);
    }

    /// <summary>登录。限流（每 IP 每 5 分钟 5 次）+ 账户锁定（连续 5 次失败锁 15 分钟）。</summary>
    /// <remarks>
    /// 单密码模式：请求体只有密码，账号由配置解析。刻意保留"先判锁定再验密码"的顺序
    /// （已锁定的账户连密码都不该被校验，少一次昂贵的哈希），以及把失败计进锁定——
    /// 单密码 + 公网暴露时，暴力破解是首要风险。
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<CurrentUserDto> LoginAsync([FromBody] LoginRequestDto input)
    {
        string password = input.Password ?? string.Empty;

        if (password.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "密码不能为空。");
        }

        string userName = _security.EffectiveSeedUserName;
        IdentityUser<Guid>? user = await _users.FindByNameAsync(userName).ConfigureAwait(false);

        if (user is null)
        {
            // 播种在宿主启动时完成，正常走不到这里。真走到了说明库被外部改动过——
            // 必须记 Error 并在审计里留痕，否则用户会对着正确的密码一直猜。
            _logger.LogError("配置的账号 {User} 在数据库中不存在，无法登录。", userName);

            await AuditLoginFailedAsync(userName, "账号不存在").ConfigureAwait(false);

            throw ApiException.Unauthorized(ApiErrorCodes.LoginFailed, "密码不正确。");
        }

        // 先判锁定再验密码：已锁定的账户连密码都不该被校验（少一次昂贵的哈希）。
        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await AuditLoginFailedAsync(userName, "账户已锁定").ConfigureAwait(false);
            throw AccountLocked(user);
        }

        if (!await _users.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            throw await RegisterFailureAsync(user, "密码不正确").ConfigureAwait(false);
        }

        await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        SessionRecord record = await CreateSessionAsync(user).ConfigureAwait(false);

        return BuildCurrentUser(user, record);
    }

    /// <summary>登出（只结束当前会话，其他设备的会话保持有效）。</summary>
    [HttpPost("logout")]
    public async Task<IActionResult> LogoutAsync()
    {
        string? sessionId = CurrentSessionId;

        if (sessionId is not null)
        {
            await _sessions.RemoveAsync(sessionId, Ct).ConfigureAwait(false);
        }

        await _signIn.SignOutAsync().ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.SessionRevoked,
            target: CurrentUserName,
            detail: $"用户主动登出（会话 {sessionId ?? "(无)"}）。",
            ct: Ct).ConfigureAwait(false);

        // 身份从"已登录"变回"匿名"，令牌的绑定身份跟着变，必须重发。
        ResetAntiforgery();

        return NoContent();
    }

    /// <summary>当前用户信息。前端路由守卫靠它判断"是否已登录"。</summary>
    [HttpGet("me")]
    public async Task<CurrentUserDto> MeAsync()
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        return BuildCurrentUser(user, _sessions.Get(CurrentSessionId));
    }

    /// <summary>改密码。成功后**全部会话（含当前）立即失效**，用户需重新登录。</summary>
    [HttpPost("password")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> ChangePasswordAsync([FromBody] ChangePasswordRequestDto input)
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        string current = input.CurrentPassword ?? string.Empty;
        string next = input.NewPassword ?? string.Empty;

        if (next.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "新密码不能为空。");
        }

        if (!string.Equals(next, input.ConfirmPassword ?? string.Empty, StringComparison.Ordinal))
        {
            // 服务端也查一遍：前端的两框一致校验挡不住直接调 API 的脚本，
            // 而"新密码打错了但保存成功"对用户来说是灾难（他再也登不进来）。
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "两次输入的新密码不一致。");
        }

        IdentityResult result = await _users.ChangePasswordAsync(user, current, next).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw MapPasswordFailure(result);
        }

        // Identity 的安全戳已经让所有旧票据作废，但它的校验有间隔；
        // 这里再把登记表清空，让"其他设备立刻掉线"变成立即成立（05 §3）。
        int revoked = await _sessions.RevokeAllForUserAsync(user.Id, Ct).ConfigureAwait(false);

        await _signIn.SignOutAsync().ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.PasswordChanged,
            target: user.UserName,
            detail: $"密码已修改，{revoked} 个会话被强制失效（含当前会话）。",
            actor: user.UserName,
            ct: Ct).ConfigureAwait(false);

        _logger.LogInformation("用户 {User} 修改了密码，{Count} 个会话被失效。", user.UserName, revoked);

        return NoContent();
    }

    /// <summary>会话列表。</summary>
    [HttpGet("sessions")]
    public IReadOnlyList<SessionInfoDto> ListSessions()
    {
        string? current = CurrentSessionId;

        return
        [
            .. _sessions.List(CurrentUserId).Select(s => new SessionInfoDto(
                s.Id,
                s.CreatedAtUtc,
                s.LastSeenUtc,
                s.Ip,
                s.UserAgent,
                // 前端用这一位禁用"踢出"按钮：踢掉自己会立刻掉线，
                // 那不是用户在这个界面上的意图（他的意图是登出）。
                Current: string.Equals(s.Id, current, StringComparison.Ordinal))),
        ];
    }

    /// <summary>踢出某个会话。</summary>
    [HttpDelete("sessions/{id}")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> RevokeSessionAsync([FromRoute] string id)
    {
        Guid userId = CurrentUserId;

        if (string.Equals(id, CurrentSessionId, StringComparison.Ordinal))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "不能踢出当前会话。要结束它请使用登出。");
        }

        if (!await _sessions.RevokeAsync(userId, id, Ct).ConfigureAwait(false))
        {
            throw ApiException.NotFound(
                ApiErrorCodes.SessionNotFound,
                $"会话 {id} 不存在或不属于当前用户。");
        }

        await _audit.WriteAsync(
            AuditEventType.SessionRevoked,
            target: CurrentUserName,
            detail: $"踢出会话 {id}。",
            ct: Ct).ConfigureAwait(false);

        return NoContent();
    }

    // ------------------------------------------------------------------

    /// <summary>建会话登记 + 签发带会话 id 的 Cookie 票据。</summary>
    private async Task<SessionRecord> CreateSessionAsync(IdentityUser<Guid> user)
    {
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = Request.Headers.UserAgent.ToString();

        SessionRecord record = await _sessions
            .CreateAsync(user.Id, ip, userAgent, Ct)
            .ConfigureAwait(false);

        // 会话 id 必须**写进票据**：它是对"这个 Cookie 是否已被踢出"的唯一联系
        //（SessionValidationEvents 逐请求按它查登记表）。本仓库不注册自定义
        // IUserClaimsPrincipalFactory（原先那个只为附加 TOTP claim 而存在，已随 TOTP 删除），
        // 所以 SignInWithClaimsAsync 是附加这个 claim 的入口。
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties
        {
            IsPersistent = true,

            // 显式给出滑动窗口长度；Cookie 中间件的 SlidingExpiration 会在此之上续期。
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(_security.CookieSlidingDays),
        };

        await _signIn.SignInWithClaimsAsync(
            user,
            properties,
            [new System.Security.Claims.Claim(SecurityClaims.SessionId, record.Id)]).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.SessionCreated,
            target: user.UserName,
            detail: $"新会话 {record.Id} 建立（来源 {ip ?? "未知"}）。",
            actor: user.UserName,
            ct: Ct).ConfigureAwait(false);

        _logger.LogInformation(
            "用户 {User} 登录成功（会话 {Session}，来源 {Ip}）。", user.UserName, record.Id, ip ?? "未知");

        // 身份刚刚从"匿名"变成"已登录"，而 antiforgery 令牌与签发时的身份绑定：
        // 不清掉旧 cookie，客户端之后拿到的仍是匿名签发的令牌，**登录后所有写操作都会 400**。
        ResetAntiforgery();

        return record;
    }

    private CurrentUserDto BuildCurrentUser(IdentityUser<Guid> user, SessionRecord? record) => new()
    {
        Id = user.Id,
        Username = user.UserName ?? string.Empty,

        // Identity 不保存"上次登录"历史。这里给的是**本会话**的建立时间与来源 IP——
        // 它是用户点进设置页时真正想确认的东西（"现在这次是谁、从哪登的"）。
        // 完整的历史在审计日志里（GET /api/audit），不去猜一个"上一次"出来。
        LastLoginTime = record?.CreatedAtUtc,
        LastLoginIp = record?.Ip,
    };

    /// <summary>
    /// 记一次失败并算出"还剩几次"；已因这次失败被锁定时直接抛 <c>ACCOUNT_LOCKED</c>。
    /// </summary>
    private async Task<ApiException> RegisterFailureAsync(IdentityUser<Guid> user, string reason)
    {
        await AuditLoginFailedAsync(user.UserName ?? string.Empty, reason).ConfigureAwait(false);

        IdentityResult failed = await _users.AccessFailedAsync(user).ConfigureAwait(false);
        if (!failed.Succeeded)
        {
            _logger.LogWarning("记录登录失败次数时出错：{Errors}", Describe(failed));
        }

        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return AccountLocked(user);
        }

        int limit = _lockout.MaxFailedAttempts;
        int used = await _users.GetAccessFailedCountAsync(user).ConfigureAwait(false);

        // **响应体里不带"还剩几次"**：那等于把锁定阈值与时长直接告诉攻击者。
        // 剩余次数只写日志，不交给调用方。
        _logger.LogInformation(
            "登录失败，已连续失败 {Used}/{Limit} 次。", used, limit);

        return ApiException.Unauthorized(
            ApiErrorCodes.LoginFailed,
            "密码不正确。");
    }

    private ApiException AccountLocked(IdentityUser<Guid> user) =>
        ApiException.Forbidden(
            ApiErrorCodes.AccountLocked,
            $"账户已被临时锁定，请在 {_lockout.Minutes} 分钟后重试。",
            "连续登录失败次数过多触发了锁定。锁定由 ASP.NET Core Identity 维护，重启服务不会解除。");

    private Task AuditLoginFailedAsync(string userName, string reason) =>
        _audit.WriteAsync(
            AuditEventType.LoginFailed,
            target: userName,
            detail: reason,
            actor: userName,
            ct: Ct);

    /// <summary>Identity 的密码错误 → 稳定 code。策略类错误与非策略类错误的处置完全不同。</summary>
    private static ApiException MapPasswordFailure(IdentityResult result)
    {
        if (result.Errors.Any(e => e.Code == "PasswordMismatch"))
        {
            return ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "当前密码不正确。");
        }

        // 策略类错误必须与"其他错误"区分：前端要把它们直接显示在密码输入框下面，
        // 而不是弹一个笼统的"修改失败"（用户会以为是自己点错了）。
        bool policy = result.Errors.Any(e => e.Code.StartsWith("Password", StringComparison.Ordinal)
                                             && e.Code != "PasswordMismatch");

        return ApiException.BadRequest(
            policy ? ApiErrorCodes.PasswordPolicyViolation : ApiErrorCodes.InvalidRequest,
            policy ? "新密码不符合密码策略。" : "修改密码失败。",
            Describe(result));
    }

    private static string Describe(IdentityResult result) =>
        string.Join("；", result.Errors.Select(e => $"[{e.Code}] {e.Description}"));
}
