using Microsoft.Extensions.Options;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 已解析的关键路径。
/// </summary>
/// <remarks>
/// 数据目录在设计文档里是"相对应用目录"的（01 §6），但相对**哪里**必须只有一处解释，
/// 否则 appsettings 说"App_Data"、日志写到一个地方、数据库建到另一个地方，
/// 排查起来是灾难。这里用 <c>ContentRootPath</c>：
/// <list type="bullet">
/// <item><c>dotnet run --project src/FileBackupTool.Api</c> → 项目目录（符合"便携"的直觉）</item>
/// <item>发布后双击 exe → exe 所在目录</item>
/// <item>nssm 注册成服务 → exe 所在目录，此时**必须**把 <c>DataDir</c> 配成绝对路径
/// （设计文档 01 §7.3 明确要求），因为服务的工作目录与双击运行时不同。</item>
/// </list>
/// </remarks>
public sealed class AppPaths
{
    /// <summary>全局单实例锁的名字。见 <see cref="SingleInstanceGuard"/>。</summary>
    public const string MutexName = @"Global\FileBackupTool.Api.SingleInstance";

    public required string ContentRoot { get; init; }

    /// <summary>数据目录（绝对路径）。</summary>
    public required string DataDir { get; init; }

    /// <summary>Serilog 文件 sink 的目录，<c>{DataDir}/logs</c>（设计文档 06 §7）。</summary>
    public required string LogDir { get; init; }

    /// <summary>SQLite 数据库路径，<c>{DataDir}/filebackuptool.db</c>。</summary>
    public required string DatabasePath { get; init; }

    /// <summary>
    /// ASP.NET Core Data Protection 的密钥环目录，<c>{DataDir}/keys</c>。
    /// </summary>
    /// <remarks>
    /// 设计文档 05 §7：keyring 必须排除在备份范围外——它是**明文可解密所有 Cookie** 的密钥材料，
    /// 进了备份就等于把会话伪造能力一起备份了。
    /// </remarks>
    public required string KeysDir { get; init; }

    public static AppPaths Resolve(IConfiguration configuration, string contentRoot)
    {
        string configured = configuration["DataDir"] is { Length: > 0 } value ? value : "App_Data";

        // 相对路径一律相对 ContentRoot 解析；绝对路径原样使用（服务化部署的要求）。
        string dataDir = Path.IsPathRooted(configured)
            ? Path.GetFullPath(configured)
            : Path.GetFullPath(Path.Combine(contentRoot, configured));

        return new AppPaths
        {
            ContentRoot = contentRoot,
            DataDir = dataDir,
            LogDir = Path.Combine(dataDir, "logs"),
            DatabasePath = Path.Combine(dataDir, "filebackuptool.db"),
            KeysDir = Path.Combine(dataDir, "keys"),
        };
    }

    /// <summary>确保目录存在。必须在建库、写日志、生成密钥之前调用。</summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogDir);
        Directory.CreateDirectory(KeysDir);
    }
}

/// <summary>启动时刻等只读运行状态，供 <c>/api/settings/info</c> 使用。</summary>
public sealed class RuntimeInfo
{
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>启动自检产生的提示（迁移失败、还原根不存在…）。</summary>
    public List<string> Notices { get; } = [];

    /// <summary>数据库迁移是否已完成（未完成时 <c>/api/settings/info</c> 要明确说出来）。</summary>
    public bool MigrationsApplied { get; set; }

    public string? MigrationError { get; set; }
}
