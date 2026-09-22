using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FileBackupTool.Data;

/// <summary>
/// 每个连接打开时套用 PRAGMA。
/// </summary>
/// <remarks>
/// <c>journal_mode=WAL</c> 是持久化在库文件里的，设一次就够；
/// 但 <c>synchronous</c> 是**每连接**的，而连接池会复用/新建连接，
/// 所以必须挂在连接打开这个钩子上，不能只在新库时设一次。
/// </remarks>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void Apply(DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite)
        {
            return;
        }

        using DbCommand cmd = connection.CreateCommand();

        // WAL：备份作业做大量分批提交，回滚日志模式下每次提交都要 fsync，WAL 下是廉价追加。
        // synchronous=NORMAL：WAL 下的推荐值。掉电最多丢最后几个事务——
        // 而备份是可重跑的，用"可重做"换吞吐，与去掉 WriteThrough 是同一个取舍。
        // foreign_keys=ON：SQLite 默认**不**强制外键，必须显式打开。
        // busy_timeout：避免瞬时写冲突直接抛 SQLITE_BUSY。
        cmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=30000;";
        cmd.ExecuteNonQuery();
    }
}

/// <summary>建库辅助。</summary>
public static class DatabaseBootstrapper
{
    public static string BuildConnectionString(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        };

        return builder.ToString();
    }

    public static DbContextOptions<AppDbContext> BuildOptions(string databasePath)
    {
        return new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(BuildConnectionString(databasePath))
            .AddInterceptors(new SqlitePragmaInterceptor())

            // 读多写少且读的结果都立刻映射成 DTO——默认不跟踪，避免百万实体被 change tracker 抓住。
            // 批量写入走显式 Add，不受影响。
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;
    }

    /// <summary>执行迁移（**不是** <c>EnsureCreated()</c>，设计文档决策 #7）。</summary>
    public static Task MigrateAsync(AppDbContext db, CancellationToken ct = default)
        => db.Database.MigrateAsync(ct);
}
