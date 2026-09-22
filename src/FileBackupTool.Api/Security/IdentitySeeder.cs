using System.Security;
using System.Text;
using FileBackupTool.Core.Configuration;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Security;

/// <summary>
/// 首次启动时创建唯一的账号（设计文档 05 §7 / 08 §2.2）。
/// </summary>
/// <remarks>
/// <para>
/// <b>密码只能来自环境变量。</b> 这条不是洁癖：
/// <list type="bullet">
/// <item><c>appsettings.json</c> 会被提交进仓库、会被复制给"帮我看一下"的人；</item>
/// <item>命令行参数会出现在进程列表与 shell 历史里；</item>
/// <item>而"随机生成一个并打印到日志"最坏——日志目录恰恰是备份范围之外、
/// 又是最容易被顺手打包带走的东西，等于把管理员凭据写在明信片上寄出去。</item>
/// </list>
/// 所以环境变量是唯一的入口，缺失时**拒绝启动**并说清楚怎么提供。
/// 拒绝启动是唯一能让用户立刻知道"密码没配"的行为；
/// 若默默用默认密码起来，用户会以为系统已经可用，实际上门是虚掩的。
/// </para>
/// <para>
/// <b>账号已存在时什么都不做。</b> 绝不在每次启动时重置密码——
/// 那会把用户在界面上改过的密码悄悄改回去（下次重启就得用环境变量里的旧密码，
/// 而用户显然不会想到这一点），也会让"改密码即失效全部会话"这条安全语义变成笑话。
/// 种子只在**建号那一次**发生。
/// </para>
/// </remarks>
public sealed class IdentitySeeder(
    UserManager<IdentityUser<Guid>> users,
    IOptions<SecurityOptions> securityOptions,
    ILogger<IdentitySeeder> logger)
{
    private readonly UserManager<IdentityUser<Guid>> _users = users;
    private readonly SecurityOptions _security = securityOptions.Value;
    private readonly ILogger<IdentitySeeder> _logger = logger;

    /// <summary>
    /// 确保种子账号存在。
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// 密码环境变量缺失，或密码不满足策略。宿主应当据此拒绝启动。
    /// </exception>
    public async Task SeedAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        string userName = string.IsNullOrWhiteSpace(_security.SeedUserName) ? "admin" : _security.SeedUserName;

        IdentityUser<Guid>? existing = await _users.FindByNameAsync(userName).ConfigureAwait(false);
        if (existing is not null)
        {
            _logger.LogDebug("种子账号 {User} 已存在，跳过创建。", userName);
            return;
        }

        string envName = string.IsNullOrWhiteSpace(_security.SeedPasswordEnvironmentVariable)
            ? "FILEBACKUPTOOL_ADMIN_PASSWORD"
            : _security.SeedPasswordEnvironmentVariable;

        string? password = Environment.GetEnvironmentVariable(envName);

        if (string.IsNullOrEmpty(password))
        {
            // 刻意不生成随机密码、不打印任何凭据。见类注释。
            throw new InvalidOperationException(BuildMissingPasswordMessage(envName, userName));
        }

        var user = new IdentityUser<Guid>
        {
            Id = Guid.NewGuid(),
            UserName = userName,

            // 锁定功能必须显式打开：Identity 新建用户的 LockoutEnabled 默认是 false，
            // 而 false 时 IsLockedOutAsync 恒为 false——"连续 5 次失败锁 15 分钟"
            // 会变成一个**看起来配了、实际完全没生效**的安全措施。
            LockoutEnabled = true,

            // 单用户系统，没有邮箱验证这一环；置 true 以免将来加功能时被"未验证"挡住。
            EmailConfirmed = true,
        };

        IdentityResult result = await _users.CreateAsync(user, password).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(BuildCreationFailedMessage(userName, result));
        }

        // 密码已用完，立即从进程环境里抹掉：之后任何"打印全部环境变量"的调试代码
        // 都拿不到它。进程自己的环境块是本进程私有的一份副本，抹掉不影响别的进程。
        TryClearEnvironmentVariable(envName);

        _logger.LogInformation(
            "已创建初始账号 {User}（密码来自环境变量 {Env}，未写入任何文件或日志）。" +
            "首次登录后必须完成 TOTP 绑定才能使用业务功能。",
            userName, envName);
    }

    private static string BuildMissingPasswordMessage(string envName, string userName)
    {
        var sb = new StringBuilder();
        sb.AppendLine("首次启动需要一个初始管理员密码，但它没有通过环境变量提供。");
        sb.AppendLine();
        sb.AppendLine($"请设置环境变量 {envName} 后重新启动，例如：");
        sb.AppendLine();
        sb.AppendLine("  PowerShell（当前会话）：");
        sb.AppendLine($"    $env:{envName} = \"<你的密码>\"");
        sb.AppendLine("    dotnet run --project src/FileBackupTool.Api");
        sb.AppendLine();
        sb.AppendLine("  PowerShell（持久化到用户环境，注意会以明文存在用户配置中）：");
        sb.AppendLine($"    [Environment]::SetEnvironmentVariable('{envName}', '<你的密码>', 'User')");
        sb.AppendLine();
        sb.AppendLine("  Windows 服务（nssm）：在服务的「环境」页里添加该变量，而不要写进启动参数。");
        sb.AppendLine();
        sb.AppendLine($"密码策略：长度至少 12 位，且同时包含大写字母、小写字母、数字与符号，且不能与用户名（{userName}）相同。");
        sb.AppendLine();
        sb.AppendLine("本程序**不会**自动生成密码，也**不会**把它打印到日志里——");
        sb.AppendLine("日志文件是可被随手带走的，把管理员凭据写进去等于把钥匙贴在门上。");
        return sb.ToString();
    }

    private static string BuildCreationFailedMessage(string userName, IdentityResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"创建初始账号 {userName} 失败，密码未通过校验或不符合策略：");

        foreach (IdentityError error in result.Errors)
        {
            sb.AppendLine($"  - [{error.Code}] {error.Description}");
        }

        sb.AppendLine();
        sb.AppendLine("请修正环境变量里的密码后重新启动（注意：密码只在这一步用到，无需重启数据库）。");
        return sb.ToString();
    }

    private static void TryClearEnvironmentVariable(string name)
    {
        try
        {
            Environment.SetEnvironmentVariable(name, null);
        }
        catch (Exception ex) when (ex is SecurityException or PlatformNotSupportedException)
        {
            // 抹不掉也不影响功能：只是少了一层"防误打印"的保险。
        }
    }
}
