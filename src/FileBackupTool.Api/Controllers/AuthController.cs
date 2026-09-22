using System.Globalization;
using System.Text;
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
/// <b>TOTP 全部走 Identity 内置能力</b>（<c>GetAuthenticatorKeyAsync</c> /
/// <c>VerifyTwoFactorTokenAsync</c> / <c>GenerateNewTwoFactorRecoveryCodesAsync</c> /
/// <c>RedeemTwoFactorRecoveryCodeAsync</c>）：这些方法的密钥格式、恢复码格式、
/// 一次性语义都是 Identity 的既定行为，自己实现一遍只会引入不兼容与"恢复码能重复用"这类漏洞。
/// </para>
/// <para>
/// <b>登录为什么不是"用户名密码换一个会话令牌"</b>：这是 Cookie 认证的两步式登录
/// （密码 → 动态码），但**第二步不发任何中间态凭据**。前端把用户名与密码**再发一次**
/// （见 <c>LoginView.submitTotp</c>），服务端每次都用密码重新验证一遍。
/// 这样做比 Identity 默认的"两因素 cookie"少一份状态：没有"密码已验证但还没过第二关"
/// 的中间票据可以被人捡走。代价是密码在第二次请求里又传一遍——它本来就在同一条 TLS 连接上，
/// 而 <c>SameSite=Strict</c> + 同源前端让跨站重放无从下手。
/// </para>
/// <para>
/// <b>动态码错误时返回 200 而不是 401</b>：密码本身是对的，错的只是第二因素。
/// 而前端的 401 拦截器会全局登出并跳登录页——那会把用户正在输入的动态码步骤整个清空，
/// 让"码打错一位"变成一个看起来像掉线的错误。所以这里用 <c>RequiresTotp + message</c>
/// 表达"再来一次"，失败**照样**计数与审计（<c>TOTP_FAILED</c>），安全语义没有放松。
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
    /// <summary>绑定成功时一次性签发的恢复码个数。</summary>
    private const int RecoveryCodeCount = 10;

    /// <summary>otpauth URI 里的发行方名称（验证器里显示的条目名）。</summary>
    private const string Issuer = "FileBackupTool";

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
        // GetAndStoreTokens 会顺带保证 XSRF-TOKEN cookie 已经就位（AntiforgeryTokenMiddleware
        // 在安全方法上也做了一次，两处都调是幂等的，内部会复用已有的合法 cookie）。
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
        string name = _antiforgeryOptions.Cookie.Name ?? "XSRF-TOKEN";
        HttpContext.Response.Cookies.Delete(name);
    }

    /// <summary>登录。限流（每 IP 每 5 分钟 5 次）+ 账户锁定（连续 5 次失败锁 15 分钟）。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    public async Task<LoginResultDto> LoginAsync([FromBody] LoginRequestDto input)
    {
        string userName = input.Username?.Trim() ?? string.Empty;
        string password = input.Password ?? string.Empty;

        if (userName.Length == 0 || password.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "用户名与密码都不能为空。");
        }

        IdentityUser<Guid>? user = await _users.FindByNameAsync(userName).ConfigureAwait(false);

        // 用户名不存在时**不做**任何"用户不存在"的区分：报同样的 401。
        // 单用户系统里用户名往往就是默认的 admin，泄露"这个名字存在"没有意义，
        // 但保持与密码错误一致可以让日志和前端分支都少一种特例。
        if (user is null)
        {
            await AuditLoginFailedAsync(userName, "用户不存在").ConfigureAwait(false);
            throw ApiException.Unauthorized(
                ApiErrorCodes.LoginFailed,
                "用户名或密码不正确。");
        }

        // 先判锁定再验密码：已锁定的账户连密码都不该被校验（少一次昂贵的哈希）。
        if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            await AuditLoginFailedAsync(userName, "账户已锁定").ConfigureAwait(false);
            throw AccountLocked(user);
        }

        if (!await _users.CheckPasswordAsync(user, password).ConfigureAwait(false))
        {
            throw await RegisterFailureAsync(user, AuditEventType.LoginFailed, "密码不正确").ConfigureAwait(false);
        }

        // 密码对了：清零失败计数。**必须在第二因素之前清零**——
        // 否则"密码打错 4 次后终于对了，然后动态码打错 1 次"会被算成第 5 次而锁定，
        // 而用户完全无法理解为什么。
        await _users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);

        bool totpBound = await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false);

        if (totpBound)
        {
            string? code = input.TotpCode?.Trim();
            string? recoveryCode = input.RecoveryCode?.Trim();

            if (!string.IsNullOrEmpty(recoveryCode))
            {
                return await CompleteRecoveryCodeLoginAsync(user, recoveryCode).ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(code))
            {
                // 第二因素还没提供。**不签发任何会话**（见类注释）。
                return new LoginResultDto
                {
                    RequiresTotp = true,
                    Message = "请输入验证器上的 6 位动态码。",
                };
            }

            bool ok = await _users
                .VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)
                .ConfigureAwait(false);

            if (!ok)
            {
                await AuditLoginFailedAsync(userName, "动态码不正确", AuditEventType.TwoFactorFailed)
                    .ConfigureAwait(false);

                // 动态码错误同样计入锁定：不计数的话，"密码已知 + 猜动态码"就变成了
                // 一条没有次数上限的无限尝试通道（6 位码在窗口内只有 100 万种，但可以一直试）。
                await _users.AccessFailedAsync(user).ConfigureAwait(false);

                if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
                {
                    throw AccountLocked(user);
                }

                // 刻意返回 200（见类注释）：前端要留在动态码这一步。
                return new LoginResultDto
                {
                    RequiresTotp = true,
                    Message = "动态码不正确，请检查设备时间是否准确后重试。",
                };
            }

            await _audit.WriteAsync(
                AuditEventType.TwoFactorSucceeded,
                target: user.UserName,
                detail: "登录时的两步验证通过。",
                actor: user.UserName,
                ct: Ct).ConfigureAwait(false);
        }

        // 到这里：密码对，且（若已绑定）动态码也对。
        SessionRecord record = await CreateSessionAsync(user).ConfigureAwait(false);

        return new LoginResultDto
        {
            // 已绑定 TOTP：正常登录完成。
            // 未绑定：也发会话，但**不是**"登录成功"——前端据此进入强制绑定流程。
            // 服务端的闸门在 TotpRequiredMiddleware：这种会话访问任何业务端点都是 403。
            Authenticated = totpBound,
            RequiresTotpSetup = !totpBound,
            User = BuildCurrentUser(user, record),
            Message = totpBound ? null : "这是首次登录，必须先绑定两步验证（TOTP）才能使用其他功能。",
        };
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

    /// <summary>当前用户信息。前端路由守卫靠它判断"要不要去绑定 TOTP"。</summary>
    [HttpGet("me")]
    public async Task<CurrentUserDto> MeAsync()
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        return BuildCurrentUser(user, _sessions.Get(CurrentSessionId));
    }

    /// <summary>
    /// TOTP 绑定第一步：生成（或取回）共享密钥与 <c>otpauth://</c> URI。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>后端不生成二维码图片</b>（05 §2.2 的实现约定）：只给 URI，由前端渲染。
    /// 这样服务端不引入任何 QR/图像依赖，也少一处会把 secret 写进日志/缓存的代码。
    /// 因此 <c>qrCodePngBase64</c> 恒为 <c>null</c>——前端已经按"没有图片就显示密钥手输"处理。
    /// </para>
    /// <para>
    /// <b>重复调用不会作废已确认的密钥</b>：<c>GetAuthenticatorKeyAsync</c> 只读不写，
    /// 密钥只在**不存在**时才生成一次。这一点很关键——如果每次打开绑定页都换一把新密钥，
    /// 用户刷新一次页面就会把刚在手机里存好的条目变成废码。
    /// </para>
    /// <para>
    /// <b>已绑定后再次调用 = 重绑</b>：这需要重新扫一次新密钥，属于"换手机"的正常路径。
    /// 但它也会让旧设备的动态码立刻失效，所以这里记一条 Warning + 审计；
    /// 若用户在 <c>confirm</c> 之前放弃，就得用恢复码登录（这正是恢复码存在的意义）。
    /// </para>
    /// </remarks>
    [HttpPost("totp/setup")]
    public async Task<TotpSetupResultDto> SetupTotpAsync()
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        bool alreadyBound = await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false);

        if (alreadyBound)
        {
            _logger.LogWarning("用户 {User} 在已绑定 TOTP 的情况下重新请求了绑定密钥（重绑）。", user.UserName);
        }

        string? key = await _users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);

        if (string.IsNullOrEmpty(key))
        {
            await _users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            key = await _users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
        }

        if (string.IsNullOrEmpty(key))
        {
            // 理论上不可达（刚重置过）。真发生了说明存储层出了问题，必须明说而不是返回空密钥。
            throw ApiException.Conflict(
                ApiErrorCodes.Conflict,
                "无法生成两步验证密钥，请查看服务器日志。",
                "Identity 的 AuthenticatorKey 存储未能返回密钥。");
        }

        return new TotpSetupResultDto
        {
            // 分组显示只是为了好念好抄；URI 里用未分组的原始密钥。
            SharedKey = FormatKey(key),
            AuthenticatorUri = BuildAuthenticatorUri(user.UserName ?? string.Empty, key),

            // 后端不生成图片（见 remarks）。
            QrCodePngBase64 = null,
        };
    }

    /// <summary>TOTP 绑定第二步：确认动态码，成功后一次性返回恢复码。</summary>
    [HttpPost("totp/confirm")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<RecoveryCodesResultDto> ConfirmTotpAsync([FromBody] TotpConfirmRequestDto input)
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        string code = input.Code?.Trim() ?? string.Empty;

        if (code.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "必须提供动态码。");
        }

        bool ok = await _users
            .VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)
            .ConfigureAwait(false);

        if (!ok)
        {
            await AuditLoginFailedAsync(user.UserName ?? string.Empty, "绑定时动态码校验失败",
                AuditEventType.TwoFactorFailed).ConfigureAwait(false);

            await _users.AccessFailedAsync(user).ConfigureAwait(false);

            throw ApiException.BadRequest(
                ApiErrorCodes.TotpCodeInvalid,
                "动态码不正确，请检查设备时间是否准确后重试。");
        }

        bool wasBound = await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false);

        user.TwoFactorEnabled = true;

        IdentityResult updated = await _users.UpdateAsync(user).ConfigureAwait(false);
        if (!updated.Succeeded)
        {
            throw ApiException.Conflict(
                ApiErrorCodes.Conflict,
                "保存两步验证设置失败。",
                Describe(updated));
        }

        // 刻意**不**更新安全戳：安全戳一变，当前会话的票据立刻失效（本宿主每个请求都校验它），
        // 用户会在"绑定成功"的瞬间被踢到登录页，而恢复码还没看到。
        // 票据里的 fbt:totp 仍是 false，但闸门中间件在快路径未命中时会回落到数据库，
        // 所以下一秒的请求就是通的。

        await _audit.WriteAsync(
            wasBound ? AuditEventType.TwoFactorRebound : AuditEventType.TwoFactorSucceeded,
            target: user.UserName,
            detail: wasBound
                ? "在已绑定的情况下重新绑定了两步验证（旧设备的动态码已失效）。"
                : "两步验证绑定完成。",
            actor: user.UserName,
            ct: Ct).ConfigureAwait(false);

        if (wasBound)
        {
            // 重绑等同于换了一把钥匙：其他设备必须重新登录（保留当前会话，
            // 否则用户看不到下面这份新的恢复码）。
            await RevokeOtherSessionsAsync(user.Id).ConfigureAwait(false);
        }

        IEnumerable<string>? codes = await _users
            .GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount)
            .ConfigureAwait(false);

        return new RecoveryCodesResultDto([.. codes ?? []]);
    }

    /// <summary>重新生成恢复码（**必须提供当前密码**——恢复码是绕过 TOTP 的钥匙）。</summary>
    /// <remarks>
    /// 要求密码而不是要求动态码：恢复码的用途正是"验证器丢了"，此时让用户提供动态码是自相矛盾的。
    /// 而一个只有会话 Cookie 的攻击者不该能给自己签发一套永久钥匙。
    /// （旧的那一套在生成时即全部作废，Identity 的 <c>GenerateNewTwoFactorRecoveryCodesAsync</c>
    /// 会整体替换，不存在"新旧都能用"的窗口。）
    /// </remarks>
    [HttpPost("totp/recovery-codes")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<RecoveryCodesResultDto> RegenerateRecoveryCodesAsync(
        [FromBody] RecoveryCodesRequestDto input)
    {
        IdentityUser<Guid> user = await RequireUserAsync(_users).ConfigureAwait(false);

        if (!await _users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.TotpNotBound,
                "尚未绑定两步验证，没有恢复码可重新生成。");
        }

        if (!await _users.CheckPasswordAsync(user, input.Password ?? string.Empty).ConfigureAwait(false))
        {
            // 不用 AccessFailedAsync：这是一条需要**已认证会话**才能到达的路径，
            // 密码在这里是"二次确认"而不是登录凭据；把打错密码算进锁定会让用户在
            // 设置页里误锁自己。失败仍然留痕（审计 + 日志）。
            await _audit.WriteAsync(
                AuditEventType.LoginFailed,
                target: user.UserName,
                detail: "重新生成恢复码时密码校验失败。",
                actor: user.UserName,
                ct: Ct).ConfigureAwait(false);

            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "当前密码不正确。");
        }

        IEnumerable<string>? codes = await _users
            .GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount)
            .ConfigureAwait(false);

        string[] result = [.. codes ?? []];

        await _audit.WriteAsync(
            AuditEventType.RecoveryCodesRegenerated,
            target: user.UserName,
            detail: $"已重新生成 {result.Length} 个恢复码，旧的恢复码全部作废。",
            actor: user.UserName,
            ct: Ct).ConfigureAwait(false);

        _logger.LogWarning("用户 {User} 重新生成了恢复码（旧恢复码已作废）。", user.UserName);

        return new RecoveryCodesResultDto(result);
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

    /// <summary>用一次性恢复码完成登录（恢复码用掉即作废，由 Identity 保证）。</summary>
    private async Task<LoginResultDto> CompleteRecoveryCodeLoginAsync(IdentityUser<Guid> user, string recoveryCode)
    {
        // RedeemTwoFactorRecoveryCodeAsync 返回的是 IdentityResult（不是 SignInResult）。
        IdentityResult result = await _users
            .RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode)
            .ConfigureAwait(false);

        if (!result.Succeeded)
        {
            await AuditLoginFailedAsync(user.UserName ?? string.Empty, "恢复码无效",
                AuditEventType.TwoFactorFailed).ConfigureAwait(false);

            await _users.AccessFailedAsync(user).ConfigureAwait(false);

            if (await _users.IsLockedOutAsync(user).ConfigureAwait(false))
            {
                throw AccountLocked(user);
            }

            // 与动态码错误一致：留在动态码/恢复码这一步，不把用户弹回登录页。
            return new LoginResultDto
            {
                RequiresTotp = true,
                Message = "恢复码无效或已被使用过。",
            };
        }

        int remaining = await _users.CountRecoveryCodesAsync(user).ConfigureAwait(false);

        await _audit.WriteAsync(
            AuditEventType.RecoveryCodeUsed,
            target: user.UserName,
            detail: $"使用一次性恢复码登录，剩余 {remaining} 个。",
            actor: user.UserName,
            ct: Ct).ConfigureAwait(false);

        // 恢复码快用完时必须提醒：它用完的那一刻，验证器再丢就彻底锁死了。
        if (remaining <= 2)
        {
            _logger.LogWarning("用户 {User} 的恢复码只剩 {Count} 个，请尽快重新生成。", user.UserName, remaining);
        }

        SessionRecord record = await CreateSessionAsync(user).ConfigureAwait(false);

        return new LoginResultDto
        {
            Authenticated = true,
            User = BuildCurrentUser(user, record),
            Message = remaining <= 2 ? $"恢复码只剩 {remaining} 个，请尽快重新生成。" : null,
        };
    }

    /// <summary>建会话登记 + 签发带会话 id 的 Cookie 票据。</summary>
    private async Task<SessionRecord> CreateSessionAsync(IdentityUser<Guid> user)
    {
        string? ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        string? userAgent = Request.Headers.UserAgent.ToString();

        SessionRecord record = await _sessions
            .CreateAsync(user.Id, ip, userAgent, Ct)
            .ConfigureAwait(false);

        // 会话 id 必须**写进票据**：它是对"这个 Cookie 是否已被踢出"的唯一联系
        //（SessionValidationEvents 逐请求按它查登记表）。SignInWithClaimsAsync 是
        // 在不改 Identity 的用户声明工厂的前提下唯一能做到这件事的入口。
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

    /// <summary>失绑其他会话（保留当前会话）。重绑 TOTP 时用。</summary>
    private async Task RevokeOtherSessionsAsync(Guid userId)
    {
        string? current = CurrentSessionId;

        foreach (SessionRecord session in _sessions.List(userId))
        {
            if (!string.Equals(session.Id, current, StringComparison.Ordinal))
            {
                await _sessions.RevokeAsync(userId, session.Id, Ct).ConfigureAwait(false);
            }
        }
    }

    private CurrentUserDto BuildCurrentUser(IdentityUser<Guid> user, SessionRecord? record) => new()
    {
        Id = user.Id,
        Username = user.UserName ?? string.Empty,
        TotpEnabled = user.TwoFactorEnabled,
        TwoFactorConfirmed = user.TwoFactorEnabled,

        // Identity 不保存"上次登录"历史。这里给的是**本会话**的建立时间与来源 IP——
        // 它是用户点进设置页时真正想确认的东西（"现在这次是谁、从哪登的"）。
        // 完整的历史在审计日志里（GET /api/audit），不去猜一个"上一次"出来。
        LastLoginTime = record?.CreatedAtUtc,
        LastLoginIp = record?.Ip,
    };

    /// <summary>
    /// 记一次失败并算出"还剩几次"；已因这次失败被锁定时直接抛 <c>ACCOUNT_LOCKED</c>。
    /// </summary>
    private async Task<ApiException> RegisterFailureAsync(
        IdentityUser<Guid> user,
        AuditEventType auditType,
        string reason)
    {
        await AuditLoginFailedAsync(user.UserName ?? string.Empty, reason, auditType).ConfigureAwait(false);

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

        return ApiException.Unauthorized(
            ApiErrorCodes.LoginFailed,
            "用户名或密码不正确。",
            used >= limit
                ? null
                : $"连续失败 {used} 次，达到 {limit} 次后账户将被锁定 {_lockout.Minutes} 分钟。");
    }

    private ApiException AccountLocked(IdentityUser<Guid> user) =>
        ApiException.Forbidden(
            ApiErrorCodes.AccountLocked,
            $"账户已被临时锁定，请在 {_lockout.Minutes} 分钟后重试。",
            "连续登录失败次数过多触发了锁定。锁定由 ASP.NET Core Identity 维护，重启服务不会解除。");

    private Task AuditLoginFailedAsync(string userName, string reason, AuditEventType type = AuditEventType.LoginFailed) =>
        _audit.WriteAsync(
            type,
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

    /// <summary>Base32 密钥按 4 个一组分组显示（与 Identity 管理页一致，方便手抄）。</summary>
    private static string FormatKey(string unformattedKey)
    {
        var sb = new StringBuilder();
        int index = 0;

        while (index + 4 < unformattedKey.Length)
        {
            sb.Append(unformattedKey, index, 4).Append(' ');
            index += 4;
        }

        if (index < unformattedKey.Length)
        {
            sb.Append(unformattedKey, index, unformattedKey.Length - index);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 组装 <c>otpauth://</c> URI（验证器扫码/手输都用它）。
    /// </summary>
    /// <remarks>
    /// 参数值一律 <c>EscapeDataString</c>：密钥与用户名都可能含 <c>+</c> <c>/</c> <c>=</c>
    /// 这些在 URI 里有特殊含义的字符，不转义会让验证器解析出错误的条目名甚至错误的密钥。
    /// </remarks>
    private static string BuildAuthenticatorUri(string accountName, string unformattedKey) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "otpauth://totp/{0}:{1}?secret={2}&issuer={0}&digits=6&period=30",
            Uri.EscapeDataString(Issuer),
            Uri.EscapeDataString(accountName),
            Uri.EscapeDataString(unformattedKey));
}
