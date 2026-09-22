using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Security;

/// <summary>
/// 强制 TOTP 闸门：**未绑定 TOTP 前不能访问任何业务端点**（设计文档 05 §2.2、08 §3 验收项）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么这件事不能只靠前端：前端是**不可信**的。用户可能直接 <c>curl</c>，
/// 也可能是一个被 XSS 拿到的会话。闸门必须在服务端。
/// </para>
/// <para>
/// 为什么不用"登录时就不发 Cookie"：那样用户永远走不到绑定页。
/// 正确做法是**发一个能力受限的会话**（能改密码、能绑定 TOTP、能看 /me），
/// 其余一律 403，前端据 <c>TOTP_NOT_BOUND</c> 跳到绑定流程。
/// </para>
/// <para>
/// 中间件位置：必须在 <c>UseAuthentication</c> **之后**（要看 <c>HttpContext.User</c>），
/// 在 <c>UseAuthorization</c> **之前**（这样未认证用户仍然得到标准的 401，
/// 由授权层产生，而不是被这里误报成 403）。
/// </para>
/// </remarks>
public sealed class TotpRequiredMiddleware(
    RequestDelegate next,
    IOptions<SecurityOptions> securityOptions,
    ILogger<TotpRequiredMiddleware> logger)
{
    /// <summary>绑定前仍然放行的路径前缀——都是"认证自身"的端点。</summary>
    private static readonly string[] AllowedPrefixes =
    [
        "/api/auth",
    ];

    private readonly RequestDelegate _next = next;
    private readonly SecurityOptions _security = securityOptions.Value;
    private readonly ILogger<TotpRequiredMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, UserManager<IdentityUser<Guid>> userManager)
    {
        if (!_security.RequireConfirmedTwoFactor)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (!IsGuardedPath(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // 未登录：交给授权层产生标准 401。这里若自己写 403，
            // 前端会把"没登录"误判成"没绑定 TOTP"，走进错误的引导分支。
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 快路径：登录票据里已写明"已确认"，无需查库。
        if (context.User.HasClaim(SecurityClaims.TotpConfirmed, "true"))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // 慢路径：票据里没有（或写着 false）。以**数据库**为准——
        // 用户可能刚刚在这一会话里完成了绑定，而票据还是绑定前签发的。
        IdentityUser<Guid>? user = await userManager.GetUserAsync(context.User).ConfigureAwait(false);
        if (user is null)
        {
            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status401Unauthorized,
                ApiErrorCodes.Unauthorized,
                "登录状态已失效，请重新登录。",
                null,
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (!user.TwoFactorEnabled)
        {
            _logger.LogWarning(
                "用户 {User} 在未完成 TOTP 绑定的情况下访问了 {Path}，已拒绝。",
                user.UserName, context.Request.Path);

            await ApiErrorWriter.WriteAsync(
                context,
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.TotpNotBound,
                "必须先完成两步验证（TOTP）绑定才能使用任何业务功能。",
                "请调用 POST /api/auth/totp/setup 后 POST /api/auth/totp/confirm 完成绑定。",
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// 受闸门管辖的路径：<c>/api/**</c> 与 <c>/hubs/**</c>，排除认证端点。
    /// </summary>
    /// <remarks>
    /// SignalR 也必须挡：Hub 是业务数据的实时通道，放过去等于绕过了 REST 闸门。
    /// 被挡时前端会看到 negotiate 失败 → 降级为轮询 → 轮询同样 403 → 仍停在绑定页，
    /// 行为是一致的。
    /// </remarks>
    private static bool IsGuardedPath(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        string value = path.Value!;
        bool guarded = value.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                       || value.StartsWith("/hubs", StringComparison.OrdinalIgnoreCase);

        if (!guarded)
        {
            return false;
        }

        foreach (string prefix in AllowedPrefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
