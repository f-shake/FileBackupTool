using System.Globalization;
using System.Runtime.CompilerServices;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FileBackupTool.Data;

/// <summary>上一个镜像里的一行。</summary>
public readonly record struct MirrorRow(
    string PathKey,
    string Path,
    long Length,
    DateTime FileTimeUtc,
    string? ContentHash,
    int SnapshotId);

/// <summary>
/// 把"上一个镜像"物化进一张 SQLite **TEMP 表**，然后边走边删。
/// </summary>
/// <remarks>
/// <para>
/// 这是设计文档 03 §4 那条"全程不把整条链物化进内存"的落地方式。
/// 旧设计的 <c>GetLatestFilesAsync</c> 会把整条链 replay 成一个
/// <c>Dictionary&lt;path, entity&gt;</c>，而增量备份**每次**都要这么做一次——百万文件直接 OOM。
/// </para>
/// <para>
/// 做法：按 <c>(RelativePathKey ASC, SnapshotId DESC)</c> 顺序流式扫描链上的记录
/// （走 <c>(TaskId, RelativePathKey, SnapshotId DESC)</c> 索引，同一路径的记录必然连续），
/// 每组只保留第一条非 Deleted 的记录，插入 TEMP 表。内存 O(1)。
/// </para>
/// <para>
/// 然后处理磁盘文件时：每批路径去 TEMP 表里查一次（顺便删掉），
/// 枚举结束后 TEMP 表里**剩下的就是被删除的文件**——不需要额外的 seen 集合。
/// </para>
/// <para>
/// 关于顺序：同一任务的快照 Id 按创建顺序自增，而创建顺序就是 <c>BeginTime</c> 顺序，
/// 所以"链上 SnapshotId 越大 = 越新"成立。<see cref="SnapshotChainService"/> 会校验它。
/// </para>
/// </remarks>
public sealed class PreviousMirrorTable : IAsyncDisposable
{
    /// <summary>SQLite 参数上限（3.32+ 为 32766）。留足余量。</summary>
    private const int ParameterChunkSize = 900;

    /// <summary>每条 INSERT 语句带多少行。配合事务把百万行的写入压到可接受。</summary>
    private const int InsertRowsPerStatement = 500;

    /// <summary>LIKE 的转义字符。必须显式声明 <c>ESCAPE '\'</c>——SQLite 默认不吃反斜杠转义。</summary>
    private const string LikeEscapeClause = @"ESCAPE '\'";

    private readonly SqliteConnection _connection;
    private readonly string _tableName;
    private bool _disposed;

    private PreviousMirrorTable(SqliteConnection connection, string tableName)
    {
        _connection = connection;
        _tableName = tableName;
    }

    /// <summary>
    /// 从链上物化上一个镜像。
    /// </summary>
    /// <param name="db">用于取得连接、以及按需开启事务。</param>
    /// <param name="chainSnapshotIds">链上的快照 id（按链序，仅含 Completed 且未删除的）。</param>
    /// <param name="taskId">任务 id。</param>
    /// <param name="pathPrefixes">
    /// 局部扫描时受限的子树（**源目录相对路径**）。null / 空表示全量范围。
    /// 设计文档 03 §5.4：删除检测只在该目录子树内做，子树外一律不动。
    /// </param>
    /// <param name="pathNormalizer">用于把前缀归一化到与 <c>RelativePathKey</c> 同一形式。</param>
    /// <param name="onlyRootLevel">
    /// 只保留**源目录根下直接的文件**（路径不含分隔符）。
    /// 抢救清单按"一级子目录"分片，而根下直接的文件没有子目录可用，需要单独成片；
    /// 这种集合无法用前缀表达，所以在这里开一个开关，仍然是流式的。
    /// </param>
    public static async Task<PreviousMirrorTable> BuildAsync(
        AppDbContext db,
        IReadOnlyList<int> chainSnapshotIds,
        Guid taskId,
        IReadOnlyList<string>? pathPrefixes,
        IPathNormalizer pathNormalizer,
        CancellationToken ct,
        bool onlyRootLevel = false)
    {
        SqliteConnection connection = await OpenConnectionAsync(db, ct).ConfigureAwait(false);
        var table = new PreviousMirrorTable(connection, "mirror_" + Guid.NewGuid().ToString("N"));

        // 建表 + 灌数据必须在一个事务里：否则每行 INSERT 都是一次独立提交（各自 fsync），
        // 百万行就是百万次 fsync。
        IDbContextTransaction? owned = null;
        if (db.Database.CurrentTransaction is null)
        {
            owned = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        }

        try
        {
            await table.CreateTableAsync(ct).ConfigureAwait(false);
            if (chainSnapshotIds.Count > 0)
            {
                await table.PopulateAsync(chainSnapshotIds, taskId, pathPrefixes, pathNormalizer, ct, onlyRootLevel)
                    .ConfigureAwait(false);
            }

            if (owned is not null)
            {
                await owned.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            if (owned is not null)
            {
                await owned.DisposeAsync().ConfigureAwait(false);
            }
        }

        return table;
    }

    /// <summary>当前镜像里还剩多少条（即"磁盘上已经没有"的数量）。</summary>
    public async Task<long> RemainingCountAsync(CancellationToken ct)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM temp.\"{_tableName}\"";
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 取这批路径的镜像行，并把它从镜像里移除。返回的是"上一个镜像里存在"的那些。
    /// </summary>
    public async Task<Dictionary<string, MirrorRow>> TakeBatchAsync(
        IReadOnlyList<string> pathKeys,
        CancellationToken ct)
    {
        var result = new Dictionary<string, MirrorRow>(pathKeys.Count, StringComparer.Ordinal);
        if (pathKeys.Count == 0)
        {
            return result;
        }

        for (int offset = 0; offset < pathKeys.Count; offset += ParameterChunkSize)
        {
            int count = Math.Min(ParameterChunkSize, pathKeys.Count - offset);
            await TakeChunkAsync(pathKeys, offset, count, result, ct).ConfigureAwait(false);
        }

        return result;
    }

    /// <summary>
    /// 枚举镜像里**剩下的全部**行。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 在增量备份里，调用它之前已经用 <see cref="TakeBatchAsync"/> 把"磁盘上仍然存在"的路径逐批取走并删掉了，
    /// 所以剩下的就是**被删除的文件**（墓碑）。
    /// </para>
    /// <para>
    /// <b>它只 SELECT，不删行</b>——所以可以重复枚举同一张表。
    /// （抢救清单就依赖了这一点：先扫一遍收集一级段名，再逐段枚举。）
    /// 名字刻意不叫 Drain：那会暗示"取完就没了"。
    /// </para>
    /// </remarks>
    public async IAsyncEnumerable<MirrorRow> EnumerateAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText =
            $"SELECT PathKey, Path, Length, Mtime, Hash, SnapshotId FROM temp.\"{_tableName}\"";
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return new MirrorRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ParseTime(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt32(5));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"DROP TABLE IF EXISTS temp.\"{_tableName}\"";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException or ObjectDisposedException)
        {
            // 连接可能已经关掉；TEMP 表随之消失
        }
    }

    // ------------------------------------------------------------------

    private async Task CreateTableAsync(CancellationToken ct)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TEMP TABLE "{_tableName}" (
                PathKey    TEXT    NOT NULL PRIMARY KEY,
                Path       TEXT    NOT NULL,
                Length     INTEGER NOT NULL,
                Mtime      TEXT    NOT NULL,
                Hash       TEXT    NULL,
                SnapshotId INTEGER NOT NULL
            )
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task PopulateAsync(
        IReadOnlyList<int> chainSnapshotIds,
        Guid taskId,
        IReadOnlyList<string>? pathPrefixes,
        IPathNormalizer pathNormalizer,
        CancellationToken ct,
        bool onlyRootLevel)
    {
        using SqliteCommand select = _connection.CreateCommand();

        var sb = new System.Text.StringBuilder();
        sb.Append("SELECT RelativePathKey, RelativePath, Length, FileTimeUtc, ContentHash, SnapshotId, Type\n");
        sb.Append("FROM Files\n");
        sb.Append("WHERE TaskId = $task\n  AND IsDeleted = 0\n  AND SnapshotId IN (");
        for (int i = 0; i < chainSnapshotIds.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            string name = "$s" + i.ToString(CultureInfo.InvariantCulture);
            sb.Append(name);
            select.Parameters.AddWithValue(name, chainSnapshotIds[i]);
        }

        sb.Append(')');

        // 局部扫描：把范围限制在脏目录子树内。**只影响扫描范围**——
        // 一个快照仍然覆盖整个源，未变化的部分直接沿用上一个镜像。
        if (pathPrefixes is { Count: > 0 })
        {
            sb.Append("\n  AND (");
            for (int i = 0; i < pathPrefixes.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(" OR ");
                }

                string key = pathNormalizer.Normalize(pathPrefixes[i]);

                // 子树：`<key>` 后面跟一个**字面量分隔符**再跟通配符。
                // 这里每个反斜杠都要写成两个（SQL 里 `\\` 经 ESCAPE '\' 还原为字面量 `\`），
                // 否则 `docs\2026` 会误匹配 `docs\2026abc`。
                string likeParam = "$like" + i.ToString(CultureInfo.InvariantCulture);
                sb.Append($"RelativePathKey LIKE {likeParam} {LikeEscapeClause}");
                select.Parameters.AddWithValue(likeParam, EscapeLike(key) + @"\\%");

                string eqParam = "$eq" + i.ToString(CultureInfo.InvariantCulture);
                sb.Append($" OR RelativePathKey = {eqParam}");
                select.Parameters.AddWithValue(eqParam, key);
            }

            sb.Append(')');
        }

        // 只保留源目录根下直接的文件（路径不含分隔符）。
        // 抢救清单按"一级子目录"分片，根下直接的文件没有子目录可用，需要单独成片。
        //
        // 注意列名：这里查的是 **Files 表**，路径列叫 RelativePathKey。
        // （PathKey 只是本方法后面写进 TEMP 表时的列名，不能在这里用。）
        //
        // 注意转义：ESCAPE '\' 下 `%\%` 是「任意字符 + **字面量 %**」，不是「任意字符 + 分隔符 + 任意字符」。
        // 分隔符本身也要转义，所以模式必须是 `%\\%`（SQL 里两个反斜杠 = 一个字面量反斜杠）。
        if (onlyRootLevel)
        {
            sb.Append(@" AND RelativePathKey NOT LIKE '%\\%' ESCAPE '\'");
        }

        // 顺序必须与索引 (TaskId, RelativePathKey, SnapshotId DESC) 一致，
        // 否则 SQLite 会为百万行做一次外部排序。
        sb.Append("\nORDER BY RelativePathKey ASC, SnapshotId DESC");
        select.CommandText = sb.ToString();

        // 直接把 Guid 交给驱动绑定，**不要** ToString()。
        // EF Core 存 Guid 用的表示形式（TEXT 还是 BLOB、大小写）属于驱动内部约定，
        // 手动转成字符串再比较等于把那个约定复制了一份；一旦它变了，
        // 这里不会报错，只会**静默查不到任何记录**——镜像变空，于是每个文件都被当成新增。
        select.Parameters.AddWithValue("$task", taskId);

        await using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);

        using SqliteCommand insert = _connection.CreateCommand();
        SqliteParameter kParam = insert.Parameters.Add("$k", SqliteType.Text);
        SqliteParameter pParam = insert.Parameters.Add("$p", SqliteType.Text);
        SqliteParameter lParam = insert.Parameters.Add("$l", SqliteType.Integer);
        SqliteParameter mParam = insert.Parameters.Add("$m", SqliteType.Text);
        SqliteParameter hParam = insert.Parameters.Add("$h", SqliteType.Text);
        SqliteParameter sParam = insert.Parameters.Add("$s", SqliteType.Integer);

        string? currentKey = null;
        bool currentKeyConsumed = false;
        int pending = 0;
        var prefix = new System.Text.StringBuilder();
        var values = new System.Text.StringBuilder();

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string pathKey = reader.GetString(0);
            if (!string.Equals(pathKey, currentKey, StringComparison.Ordinal))
            {
                currentKey = pathKey;
                currentKeyConsumed = false;
            }

            if (currentKeyConsumed)
            {
                // 同一路径的更旧版本，丢弃
                continue;
            }

            currentKeyConsumed = true;

            if (reader.GetInt32(6) == (int)FileRecordType.Deleted)
            {
                // 最新记录是墓碑 → 这个路径不在上一个镜像里
                continue;
            }

            int p = pending;
            if (p > 0)
            {
                values.Append(',');
            }

            values.Append($"($k{p},$p{p},$l{p},$m{p},$h{p},$s{p})");
            kParam = insert.Parameters.Add($"$k{p}", SqliteType.Text);
            pParam = insert.Parameters.Add($"$p{p}", SqliteType.Text);
            lParam = insert.Parameters.Add($"$l{p}", SqliteType.Integer);
            mParam = insert.Parameters.Add($"$m{p}", SqliteType.Text);
            hParam = insert.Parameters.Add($"$h{p}", SqliteType.Text);
            sParam = insert.Parameters.Add($"$s{p}", SqliteType.Integer);

            kParam.Value = pathKey;
            pParam.Value = reader.GetString(1);
            lParam.Value = reader.GetInt64(2);
            mParam.Value = reader.GetString(3);
            hParam.Value = reader.IsDBNull(4) ? DBNull.Value : reader.GetString(4);
            sParam.Value = reader.GetInt32(5);

            pending++;

            if (pending >= InsertRowsPerStatement)
            {
                prefix.Clear();
                prefix.Append($"INSERT OR REPLACE INTO temp.\"{_tableName}\" (PathKey, Path, Length, Mtime, Hash, SnapshotId) VALUES ");
                prefix.Append(values);
                insert.CommandText = prefix.ToString();
                await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                insert.Parameters.Clear();
                values.Clear();
                pending = 0;
            }
        }

        if (pending > 0)
        {
            insert.CommandText =
                $"INSERT OR REPLACE INTO temp.\"{_tableName}\" (PathKey, Path, Length, Mtime, Hash, SnapshotId) VALUES {values}";
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task TakeChunkAsync(
        IReadOnlyList<string> pathKeys,
        int offset,
        int count,
        Dictionary<string, MirrorRow> result,
        CancellationToken ct)
    {
        var placeholders = new string[count];
        for (int i = 0; i < count; i++)
        {
            placeholders[i] = "$k" + i.ToString(CultureInfo.InvariantCulture);
        }

        string inList = string.Join(",", placeholders);

        using (SqliteCommand select = _connection.CreateCommand())
        {
            select.CommandText =
                $"SELECT PathKey, Path, Length, Mtime, Hash, SnapshotId FROM temp.\"{_tableName}\" WHERE PathKey IN ({inList})";
            for (int i = 0; i < count; i++)
            {
                select.Parameters.AddWithValue(placeholders[i], pathKeys[offset + i]);
            }

            await using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string key = reader.GetString(0);
                result[key] = new MirrorRow(
                    key,
                    reader.GetString(1),
                    reader.GetInt64(2),
                    ParseTime(reader.GetString(3)),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt32(5));
            }
        }

        using (SqliteCommand delete = _connection.CreateCommand())
        {
            delete.CommandText = $"DELETE FROM temp.\"{_tableName}\" WHERE PathKey IN ({inList})";
            for (int i = 0; i < count; i++)
            {
                delete.Parameters.AddWithValue(placeholders[i], pathKeys[offset + i]);
            }

            await delete.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    internal static string FormatTime(DateTime value) =>
        value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTime ParseTime(string value) =>
        DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>
    /// 转义 LIKE 的通配符。路径里出现 <c>%</c> 或 <c>_</c> 是完全可能的，
    /// 而分隔符 <c>\</c> 在 <c>ESCAPE '\'</c> 下也必须双写。
    /// </summary>
    private static string EscapeLike(string value) => value
        .Replace(@"\", @"\\", StringComparison.Ordinal)
        .Replace("%", @"\%", StringComparison.Ordinal)
        .Replace("_", @"\_", StringComparison.Ordinal);

    private static async Task<SqliteConnection> OpenConnectionAsync(DbContext db, CancellationToken ct)
    {
        var connection = (SqliteConnection)db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }

        return connection;
    }
}
