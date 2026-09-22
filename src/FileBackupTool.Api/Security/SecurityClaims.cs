using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Security;

/// <summary>本应用使用的自定义 Claim 类型。</summary>
public static class SecurityClaims
{
    /// <summary>会话 id。与 <see cref="SessionRegistry"/> 里的记录一一对应。</summary>
    public const string SessionId = "fbt:sid";

    /// <summary>
    /// 登录时该用户的 TOTP 是否已确认。
    /// </summary>
    /// <remarks>
    /// 这是一个**加速用**的镜像，不是权威来源：真正的权威是数据库里的 <c>TwoFactorEnabled</c>。
    /// 强制绑定中间件先看这个 Claim（命中就无需查库），未命中才回落到数据库查询。
    /// 这样绝大多数请求省掉一次用户表查询，而"刚绑定完 TOTP"的用户也不会被卡住。
    /// </remarks>
    public const string TotpConfirmed = "fbt:totp";
}

/// <summary>
/// 在登录票据里附加本应用自己的 Claim。
/// </summary>
/// <remarks>
/// 设计文档 05 §2 要求强制 TOTP，而"是否已绑定"是每个业务请求都要判的事。
/// 把它写进 Claim 是为了让判定的快路径不查库（见 <see cref="SecurityClaims.TotpConfirmed"/>）。
/// </remarks>
public sealed class AppClaimsPrincipalFactory(
    UserManager<IdentityUser<Guid>> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<IdentityUser<Guid>, IdentityRole<Guid>>(userManager, roleManager, options)
{
    public override async Task<ClaimsPrincipal> CreateAsync(IdentityUser<Guid> user)
    {
        ClaimsPrincipal principal = await base.CreateAsync(user).ConfigureAwait(false);

        var identity = (ClaimsIdentity)principal.Identity!;

        // 安全戳也会进票据，但 Identity 内部自带；这里只补我们自己的那一位。
        identity.AddClaim(new Claim(
            SecurityClaims.TotpConfirmed,
            user.TwoFactorEnabled ? "true" : "false"));

        return principal;
    }
}
