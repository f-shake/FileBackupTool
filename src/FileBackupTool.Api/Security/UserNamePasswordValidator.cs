using Microsoft.AspNetCore.Identity;

namespace FileBackupTool.Api.Security;

/// <summary>
/// 追加一条 Identity 没有内置的密码规则：**密码不得与用户名相同**（设计文档 05 §2.3）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要自己写：Identity 的 <c>PasswordValidator</c> 只管长度与字符类别
/// （<c>RequireDigit</c> / <c>RequireUppercase</c> / …），**不比较用户名**。
/// 而"admin / Admin123456!@#"这种组合恰好能通过全部内置规则，是最容易被猜到的口令之一。
/// </para>
/// <para>
/// 注册成额外的 <see cref="IPasswordValidator{TUser}"/> 而不是在控制器里判：
/// <c>UserManager</c> 会**依次调用全部**已注册的密码校验器，于是这条规则自动覆盖
/// 所有入口——首次播种建号（<c>CreateAsync</c>）、改密码（<c>ChangePasswordAsync</c>）、
/// 以及将来任何重置密码的路径。写在控制器里就只能覆盖那一条路径，
/// 而"某个入口漏了这条规则"正是安全规则最常见的失效方式。
/// </para>
/// <para>
/// 比较用 <see cref="StringComparison.OrdinalIgnoreCase"/>：用户名在 Windows 与 Identity 里
/// 都是不区分大小写的，密码规则必须与之一致，否则 <c>Admin</c> 这个口令能通过、
/// 而它猜起来只比 <c>admin</c> 多一次尝试。
/// </para>
/// </remarks>
public sealed class UserNamePasswordValidator : IPasswordValidator<IdentityUser<Guid>>
{
    /// <summary>失败码。以 <c>Password</c> 开头，让它落进"密码策略类错误"的分类里。</summary>
    public const string ErrorCode = "PasswordSameAsUserName";

    public Task<IdentityResult> ValidateAsync(
        UserManager<IdentityUser<Guid>> manager,
        IdentityUser<Guid> user,
        string? password)
    {
        string? userName = user.UserName;

        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(userName))
        {
            // 空密码交给内置校验器去报（它会给出更准确的"密码太短/不能为空"）。
            return Task.FromResult(IdentityResult.Success);
        }

        if (password.Trim().Equals(userName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = ErrorCode,
                Description = "密码不能与用户名相同。",
            }));
        }

        return Task.FromResult(IdentityResult.Success);
    }
}
