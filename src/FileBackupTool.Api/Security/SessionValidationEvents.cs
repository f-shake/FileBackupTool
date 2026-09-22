using System.Security.Claims;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Security;

/// <summary>
/// Cookie 票据的逐请求校验：安全戳 + 会话登记表 + 绝对过期。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么必须用 <c>EventsType</c> 而不是 <c>options.Events</c></b>：
/// <c>AddIdentity</c> 把 <c>OnValidatePrincipal</c> 设成了
/// <c>SecurityStampValidator.ValidatePrincipalAsync</c>。而 Cookie 认证在解析事件时
/// **优先取 <c>EventsType</c>**，一旦我们设了它，Identity 设的那个回调就被整个替换掉了。
/// 所以本类必须自己把安全戳校验**显式调回来**，否则"改密码失效全部会话"会静默失效。
/// </para>
/// <para>
/// <b>三层校验的顺序</b>：
/// <list type="number">
/// <item>安全戳——改密码 / 重绑 TOTP 后，所有已签发的票据作废（05 §3）。</item>
/// <item>会话登记表——支持"单独踢出某个会话"（05 §3），这是安全戳做不到的粒度。</item>
/// <item>绝对过期——滑动 7 天 + 绝对 30 天（05 §3）。Cookie 的 <c>ExpireTimeSpan</c>
/// 只有滑动，没有绝对上限，不额外判就等于"只要一直用就永不过期"。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class SessionValidationEvents(
    SessionRegistry sessions,
    AuditLogger audit,
    IOptions<SecurityOptions> securityOptions,
    ILogger<SessionValidationEvents> logger) : CookieAuthenticationEvents
{
    private readonly SessionRegistry _sessions = sessions;
    private readonly AuditLogger _audit = audit;
    private readonly SecurityOptions _security = securityOptions.Value;
    private readonly ILogger<SessionValidationEvents> _logger = logger;

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        // ① 安全戳。Identity 原生的"改密码 / 重绑即失效全部会话"靠它。
        await SecurityStampValidator.ValidatePrincipalAsync(context).ConfigureAwait(false);
        if (context.Principal is null)
        {
            return;
        }

        string? sessionId = context.Principal.FindFirst(SecurityClaims.SessionId)?.Value;
        if (!_sessions.IsValid(sessionId))
        {
            // 会话被踢出、登记表被清空，或票据来自"登记表还没建立"的旧版。
            // 一律失效并记审计——"我的会话为什么掉线"必须能在审计里查到答案。
            _logger.LogInformation("会话 {SessionId} 已被撤销，拒绝该请求。", sessionId ?? "(无)");
            await RejectAsync(context, AuditEventType.SessionRevoked, "会话已被撤销或过期").ConfigureAwait(false);
            return;
        }

        SessionRecord? record = _sessions.Get(sessionId);
        if (record is not null)
        {
            TimeSpan lifetime = DateTime.UtcNow - record.CreatedAtUtc;
            if (lifetime > TimeSpan.FromDays(_security.CookieAbsoluteDays))
            {
                _logger.LogInformation(
                    "会话 {SessionId} 已超过绝对有效期 {Days} 天，强制失效。", record.Id, _security.CookieAbsoluteDays);
                await RejectAsync(context, AuditEventType.SessionRevoked, "超过绝对有效期").ConfigureAwait(false);
                return;
            }

            // 心跳（内部按 5 分钟节流落库）。
            await _sessions.TouchAsync(record.Id, context.HttpContext.RequestAborted).ConfigureAwait(false);
        }

        // 滑动续期交给 Cookie 中间件本身（SlidingExpiration = true + ExpireTimeSpan = 7 天）。
    }

    /// <summary>
    /// 认证失败时**不要重定向**，直接给 <c>{ code, message, detail? }</c> 的 JSON。
    /// </summary>
    /// <remarks>
    /// Cookie 认证的默认行为是 302 跳登录页。对 SPA 来说那是一个"看起来成功"的响应
    /// （axios 会跟着跳，最后拿到一坨 HTML），前端根本没法区分"没登录"和"服务端出错"。
    /// 必须把状态码拉回 401 并给出稳定 code。
    /// <para>
    /// 这两个覆盖**只能写在这里**：一旦设了 <c>EventsType</c>，Cookie 认证就会忽略
    /// <c>options.Events</c> 这个实例上的任何赋值。
    /// </para>
    /// </remarks>
    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        ApiErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status401Unauthorized,
            ApiErrorCodes.Unauthorized,
            "登录状态已失效，请重新登录。");

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        ApiErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status403Forbidden,
            ApiErrorCodes.Forbidden,
            "没有权限执行该操作。");

    private async Task RejectAsync(
        CookieValidatePrincipalContext context,
        AuditEventType eventType,
        string reason)
    {
        context.RejectPrincipal();
        await context.HttpContext
            .SignOutAsync(IdentityConstants.ApplicationScheme)
            .ConfigureAwait(false);

        await _audit.WriteAsync(
            eventType,
            target: context.Principal?.Identity?.Name,
            detail: reason,
            ct: context.HttpContext.RequestAborted).ConfigureAwait(false);
    }
}
