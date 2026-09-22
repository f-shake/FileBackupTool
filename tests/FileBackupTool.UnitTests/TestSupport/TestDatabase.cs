using FileBackupTool.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.UnitTests.TestSupport;

/// <summary>测试用的 <see cref="IDbContextFactory{TContext}"/>。</summary>
public sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AppDbContext(options));
}

/// <summary>
/// 一个真实的临时 SQLite 库（跑真正的 EF migrations，**不是** <c>EnsureCreated()</c>）。
/// </summary>
/// <remarks>
/// 刻意用真库而不是内存库：迁移本身也要被测到，
/// 而且 <c>PreviousMirrorTable</c> 依赖 SQLite 的 TEMP 表、窗口外的 SQL 语义与 LIKE 转义——
/// 换成假的仓储就测不到这些真正的风险点。
/// </remarks>
public sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string directoryPath, IDbContextFactory<AppDbContext> factory)
    {
        DirectoryPath = directoryPath;
        Factory = factory;
    }

    public string DirectoryPath { get; }

    public IDbContextFactory<AppDbContext> Factory { get; }

    public string DatabasePath => Path.Combine(DirectoryPath, "filebackuptool.db");

    public static async Task<TestDatabase> CreateAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "fbt-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        DbContextOptions<AppDbContext> options = DatabaseBootstrapper.BuildOptions(
            Path.Combine(directory, "filebackuptool.db"));

        var factory = new TestDbContextFactory(options);

        await using (AppDbContext db = await factory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await DatabaseBootstrapper.MigrateAsync(db).ConfigureAwait(false);
        }

        return new TestDatabase(directory, factory);
    }

    public async Task<AppDbContext> OpenAsync() => await Factory.CreateDbContextAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        // 连接池会一直握着文件句柄；不清池就删不掉目录
        SqliteConnection.ClearAllPools();
        await Task.Yield();

        try
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试收尾失败不该让测试失败
        }
    }
}
