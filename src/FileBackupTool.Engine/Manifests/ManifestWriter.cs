using System.Globalization;
using System.Text.Json;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Engine.Manifests;

/// <summary>
/// 抢救清单写入器。设计文档 02 §6 / 决策总览 #11。
/// </summary>
/// <remarks>
/// <para>
/// 集中式 SQLite 的代价是**备份目录不再自包含**（02 §1），抢救清单就是补偿：
/// 只靠明文 JSON + blob 池，不装本软件也能还原。
/// 因此格式被钉死为明文、不压缩、不用 SQLite 导出，且每个条目自带
/// <c>path</c> / <c>hash</c> / <c>size</c> / <c>mtime</c>。
/// </para>
/// <para><b>落盘布局（02 §6.2）</b></para>
/// <code>
/// &lt;BackupDir&gt;\.manifest.json             # 文件总数 &lt; ManifestShardThreshold：单文件
/// &lt;BackupDir&gt;\.manifest\&lt;一级&gt;.json       # 一级子目录一片
/// &lt;BackupDir&gt;\.manifest\_root.json          # 源目录根下直接的文件（没有一级子目录可用）
/// &lt;BackupDir&gt;\.manifest\&lt;一级&gt;\&lt;二级&gt;.json # 该一级目录条目超过 ManifestShardMaxEntries 时再分一层
/// &lt;BackupDir&gt;\.manifest\&lt;一级&gt;\_root.json  # 此时该一级目录下直接的文件
/// </code>
/// <para><b>为什么这样切</b>：一次备份只动了几个目录，却要重写整份清单，是百万文件下不可接受的
/// 写放大。按一级子目录切片后，一次增量只重写被触及的片（含"整片被删空"的片）。</para>
/// <para><b>为什么单文件阈值与片内上限是两个参数</b>：两者解决的问题不同——前者是"几千文件以下
/// 分片是纯粹的负担"，后者是"单个片太大就重新变成一次重写整片"。</para>
/// <para>
/// <b>流式约束</b>：<c>files[]</c> 全程走 <see cref="PreviousMirrorTable"/>（SQLite TEMP 表 + 边走边删），
/// 内存 O(1)；<c>fileCount</c> / <c>totalBytes</c> 直接取快照上增量维护的镜像规模字段，
/// **绝不为了这两个数去 replay 整条链**。
/// </para>
/// </remarks>
public sealed class ManifestWriter(
    IDbContextFactory<AppDbContext> dbFactory,
    SnapshotChainService chains,
    IFileSystem fs,
    IPathNormalizer normalizer,
    IClock clock,
    IOptions<StorageOptions> storageOptions,
    ILogger<ManifestWriter> logger) : IManifestWriter
{
    private const string ToolName = "FileBackupTool";
    private const int ManifestVersion = 1;

    /// <summary>
    /// 源目录根下直接的文件单独成片，片名固定为它（二级层的"该一级目录下直接的文件"同名）。
    /// </summary>
    /// <remarks>
    /// 这个保留名**必须**是无法由真实目录名产生的，否则一个真名叫 <c>_root</c> 的一级目录
    /// 会与"根下直接的文件"那片写到同一个文件上，**静默丢条目**。
    /// 由 <see cref="EscapeSegment"/> 保证：真实段名里的 <c>%</c> 与 <c>_</c> 都会被转义，
    /// 所以任何真实段名都不可能等于字面的 <c>_root</c>。
    /// </remarks>
    private const string RootShardName = "_root";

    private const string ManifestDirName = ".manifest";
    private const string ManifestFileName = ".manifest.json";
    private const string JsonExtension = ".json";

    private const string BlobLayout = "blobs/<hash[0:2]>/<hash[2:4]>/<hash>";

    /// <summary>设计文档 02 §6.1 的"还原方法"原文。让清单在**没有本软件**时也自解释。</summary>
    private const string RestoreHint =
        "对每个条目，把 blobs/<hash[0:2]>/<hash[2:4]>/<hash> 复制到 path 指定的位置。";

    /// <summary>
    /// 缩进输出。设计文档 §6.1 的样例就是缩进的，且"用记事本打开就能读"是唯一的抢救属性，
    /// 没有理由为了省磁盘把它压成一行（代价：体积约为紧凑格式的两倍，百万文件下是百 MB 级）。
    /// </summary>
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly SnapshotChainService _chains = chains;
    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly IClock _clock = clock;
    private readonly StorageOptions _storage = storageOptions.Value;
    private readonly ILogger<ManifestWriter> _logger = logger;

    public async Task<ManifestWriteResult> WriteAsync(
        BackupTask task,
        BackupSnapshot latestSnapshot,
        IReadOnlyCollection<string> touchedRelativePaths,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // BackupEngine 调用我们时快照已经落库为 Completed，所以链一定解析得出来——
        // 解析不出来就是**真的**结构性损坏（缺全量、链序与 SnapshotId 顺序不符）。
        List<BackupSnapshot>? chain = await _chains
            .ResolveChainAsync(task.Id, latestSnapshot.Id, ct)
            .ConfigureAwait(false);

        if (chain is null || chain.Count == 0)
        {
            // 拿不到完整的当前镜像 → **什么都不写**。写一份错的清单比不写清单危险得多：
            // 用户会照着它还原，而它会缺条目或给出失效的哈希。
            // 如实把"链不一致"报出去（BackupEngine 会记到快照上并写日志，不静默）。
            _logger.LogWarning("任务「{Task}」的镜像链断裂，本次不生成抢救清单。", task.Name);
            return new ManifestWriteResult(
                "broken-chain", 0, latestSnapshot.ImageFileCount, latestSnapshot.ImageTotalBytes, 1);
        }

        string backupDir = _normalizer.TrimTrailingSeparator(task.BackupDir);
        string manifestDir = _normalizer.Combine(backupDir, ManifestDirName);
        string manifestFile = _normalizer.Combine(backupDir, ManifestFileName);

        var header = new ManifestHeader(
            task.Id,
            task.Name,
            _normalizer.TrimTrailingSeparator(task.SourceDir),
            backupDir,
            latestSnapshot.ImageFileCount,
            latestSnapshot.ImageTotalBytes,
            latestSnapshot.Id,
            latestSnapshot.EndTime ?? latestSnapshot.BeginTime,
            _clock.UtcNow);

        // 分片在几千文件以下是纯负担（02 §6.2）。
        bool sharded = latestSnapshot.ImageFileCount >= _storage.ManifestShardThreshold;

        IReadOnlyList<int> chainIds = [.. chain.Select(s => s.Id)];

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // 整个写入复用同一个连接：TEMP 表挂在连接上，换连接就没了。
        var session = new Session(
            db, chainIds, header, manifestDir, manifestFile, _normalizer.Comparer);

        ManifestWriteResult result = sharded
            ? await WriteShardedAsync(session, latestSnapshot, touchedRelativePaths, ct).ConfigureAwait(false)
            : await WriteSingleFileAsync(session, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "抢救清单已更新：布局={Layout}，片数={Shards}，镜像 {Files} 个文件 / {Bytes} 字节。",
            result.Layout, result.ShardCount, result.FileCount, result.TotalBytes);

        return result;
    }

    // ------------------------------------------------------------------
    // 单文件布局
    // ------------------------------------------------------------------

    /// <summary>
    /// 写 <c>&lt;BackupDir&gt;\.manifest.json</c>。
    /// </summary>
    /// <remarks>
    /// 单文件布局没有"只重写受影响的片"这回事——整份清单就是一个文件，
    /// 改一个条目也要重写全部（这正是几千文件以下才允许用它的原因）。
    /// 数据仍然走流式 TEMP 表，所以内存开销与分片布局一致，只有 IO 量不同。
    /// </remarks>
    private async Task<ManifestWriteResult> WriteSingleFileAsync(Session session, CancellationToken ct)
    {
        await using PreviousMirrorTable mirror = await PreviousMirrorTable
            .BuildAsync(session.Db, session.ChainIds, session.Header.TaskId, null, _normalizer, ct)
            .ConfigureAwait(false);

        // deleteWhenEmpty: false —— 这份文件**就是**清单本身。
        // 镜像为空时它仍然有意义（头部自描述"这个任务有 0 个文件"），
        // 而分片的"空片删掉"是为了不让"存在一个片"暗示"里面有条目"。
        await WriteShardFileAsync(
                session.Header, session.ManifestFile, mirror.EnumerateAllAsync(ct),
                filter: null, deleteWhenEmpty: false, ct)
            .ConfigureAwait(false);

        // 两种布局不能并存：分片切换回单文件后，整个 .manifest 目录必须消失，
        // 否则读者会同时看到两份清单，且无法判断哪份是当前的。
        _fs.DeleteDirectory(session.ManifestDir, recursive: true);

        return new ManifestWriteResult("single", 1, session.Header.FileCount, session.Header.TotalBytes, 0);
    }

    // ------------------------------------------------------------------
    // 分片布局
    // ------------------------------------------------------------------

    private async Task<ManifestWriteResult> WriteShardedAsync(
        Session session,
        BackupSnapshot latestSnapshot,
        IReadOnlyCollection<string> touchedRelativePaths,
        CancellationToken ct)
    {
        bool dirExists = _fs.DirectoryExists(session.ManifestDir);

        // 什么时候必须**整体重建**（而不是只重写受影响的片）：
        // 1) 目标布局的"地基"不存在（首次生成分片、或刚从单文件切过来）——
        //    只写被触及的片会留下一份**缺条目**的清单，那比没有清单更危险。
        // 2) 全量 / 虚拟全量快照——它的镜像**完全等于**本次快照自己的记录
        //    （链就是 [它自己]）。此时"没被触及"只代表文件在本次枚举里仍然存在，
        //    而**上次之后被删掉的目录**既不在 touched 里、也没有墓碑记录，
        //    只重写被触及的片会把它们的旧片留在磁盘上变成幽灵条目。
        bool rebuildAll = !dirExists || latestSnapshot.Type is SnapshotType.Full or SnapshotType.VirtualFull;

        int shardCount = 0;

        if (rebuildAll)
        {
            // 整体重建要扫两遍镜像：一遍收集一级段名（_root 片的前缀表达不了，单独一遍），
            // 但这只在"重建整份清单"时发生，与逐片重写的常态无关。
            // 每个一级段各自建表来写，而不是边扫边写——否则要同时开着几百个文件流。
            await using PreviousMirrorTable all = await BuildFullMirrorAsync(session, ct).ConfigureAwait(false);

            var segments = new SortedDictionary<string, string>(StringComparer.Ordinal);
            await foreach (MirrorRow row in all.EnumerateAllAsync(ct).ConfigureAwait(false))
            {
                if (TrySplitFirstSegment(row.Path, out string first, out _))
                {
                    segments.TryAdd(_normalizer.Normalize(first), first);
                }
            }

            shardCount += await WriteRootShardAsync(session, ct).ConfigureAwait(false);

            foreach (string segment in segments.Values)
            {
                shardCount += await WriteFirstLevelAsync(session, segment, ct).ConfigureAwait(false);
            }

            // 上一版留下的、现在不该存在的片（例如整个一级目录被删掉）
            CleanManifestDir(session);
        }
        else
        {
            foreach (string? segment in AffectedSegments(touchedRelativePaths))
            {
                shardCount += segment is null
                    ? await WriteRootShardAsync(session, ct).ConfigureAwait(false)
                    : await WriteFirstLevelAsync(session, segment, ct).ConfigureAwait(false);
            }
        }

        // 分片布局下不该存在单文件版本（阈值上穿时留下的）
        _fs.DeleteFile(session.ManifestFile);

        return new ManifestWriteResult("sharded", shardCount, session.Header.FileCount, session.Header.TotalBytes, 0);
    }

    /// <summary>物化**整个**当前镜像，只用来收集一级段名。</summary>
    private Task<PreviousMirrorTable> BuildFullMirrorAsync(Session session, CancellationToken ct) =>
        PreviousMirrorTable.BuildAsync(
            session.Db, session.ChainIds, session.Header.TaskId, null, _normalizer, ct);

    /// <summary>
    /// 写源目录根下的 <c>_root</c> 片。
    /// </summary>
    /// <remarks>
    /// 这一片是"路径里没有分隔符"的集合，**前缀表达不了**，所以走
    /// <see cref="PreviousMirrorTable.BuildAsync"/> 的 <c>onlyRootLevel</c> 开关：
    /// 在 SQL 里过滤，仍然是流式的，且不物化整镜像。
    /// </remarks>
    private async Task<int> WriteRootShardAsync(Session session, CancellationToken ct)
    {
        await using PreviousMirrorTable table = await PreviousMirrorTable
            .BuildAsync(
                session.Db, session.ChainIds, session.Header.TaskId, null, _normalizer, ct,
                onlyRootLevel: true)
            .ConfigureAwait(false);

        string path = ShardPath(session, segment: null);
        long entries = await WriteShardFileAsync(
                session.Header, path, table.EnumerateAllAsync(ct),
                filter: null, deleteWhenEmpty: true, ct)
            .ConfigureAwait(false);

        if (entries == 0)
        {
            return 0;
        }

        session.Expected.Add(path);
        return 1;
    }

    /// <summary>
    /// 从 <paramref name="touchedRelativePaths"/> 推出受影响的**一级段名**集合。
    /// </summary>
    /// <remarks>
    /// 取第一个分隔符之前的部分；没有分隔符 = 源目录根下直接的文件 → <c>_root</c> 片。
    /// 路径可能来自枚举（真实大小写）也可能来自镜像行（入库时的大小写），
    /// 所以用归一化后的名字做去重键、保留首次见到的原始写法做片文件名。
    /// </remarks>
    private List<string?> AffectedSegments(IReadOnlyCollection<string> touchedRelativePaths)
    {
        var segments = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (string raw in touchedRelativePaths)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                // 空串代表"源目录根自身"（BackupEngine 只在脏目录语义里用它）。
                // 清单按一级子目录切片，根自身不是任何一片的内容，忽略。
                continue;
            }

            string path = raw.Replace('/', '\\').Trim('\\');
            if (path.Length == 0)
            {
                continue;
            }

            string segment = TrySplitFirstSegment(path, out string first, out _) ? first : RootShardName;
            segments.TryAdd(_normalizer.Normalize(segment), segment);
        }

        List<string?> result = [];
        foreach (string segment in segments.Values)
        {
            result.Add(segment == RootShardName ? null : segment);
        }

        return result;
    }

    /// <summary>
    /// 写一个一级片。源目录根下直接的文件没有一级子目录名可用，所以走
    /// <see cref="WriteRootShardAsync"/> 而不是这里。
    /// </summary>
    /// <returns>写出的片文件数（0 表示这一片空了，文件被删除）。</returns>
    private async Task<int> WriteFirstLevelAsync(Session session, string segment, CancellationToken ct)
    {
        await using PreviousMirrorTable table = await PreviousMirrorTable
            .BuildAsync(session.Db, session.ChainIds, session.Header.TaskId, [segment], _normalizer, ct)
            .ConfigureAwait(false);

        // 先拿计数再决定要不要二级分片：这一片有几千还是几十万条目，决定策略完全不同。
        long count = await table.RemainingCountAsync(ct).ConfigureAwait(false);

        if (count > _storage.ManifestShardMaxEntries)
        {
            return await WriteSecondLevelAsync(session, segment, table, ct).ConfigureAwait(false);
        }

        string path = ShardPath(session, segment);
        long entries = await WriteShardFileAsync(
                session.Header, path, table.EnumerateAllAsync(ct),
                filter: null, deleteWhenEmpty: true, ct)
            .ConfigureAwait(false);

        // 从二级布局退回一级布局：二级目录必须清掉，否则同一个文件在两份片里各出现一次。
        _fs.DeleteDirectory(SegmentDir(session, segment), recursive: true);

        if (entries == 0)
        {
            return 0;
        }

        session.Expected.Add(path);
        return 1;
    }

    /// <summary>
    /// 把一个一级目录按二级子目录再分一层。
    /// </summary>
    /// <remarks>
    /// 二级段的个数是"该一级目录下的直接子目录数"，可以进内存；条目仍然逐段流式写。
    /// 这里会**重写该一级目录的全部二级片**（而不是只写被触及的那些）：
    /// 二级分片只在单个一级目录超过 <see cref="StorageOptions.ManifestShardMaxEntries"/>
    /// 时才启用，此时该目录本身就是异常的巨无霸，整片重写的代价相对可接受，
    /// 换来的是"片与镜像永远一致"这个不需要额外状态就能保证的性质。
    /// </remarks>
    private async Task<int> WriteSecondLevelAsync(
        Session session,
        string segment,
        PreviousMirrorTable table,
        CancellationToken ct)
    {
        var secondSegments = new SortedDictionary<string, string>(StringComparer.Ordinal);

        await foreach (MirrorRow row in table.EnumerateAllAsync(ct).ConfigureAwait(false))
        {
            if (TrySplitSecondSegment(row.Path, out string second, out _))
            {
                secondSegments.TryAdd(_normalizer.Normalize(second), second);
            }
        }

        string segmentDir = SegmentDir(session, segment);
        int prefixLength = _normalizer.Normalize(segment).Length;
        int shardCount = 0;

        // 该一级目录下**直接**的文件 → <一级>\_root.json。
        // 这里复用已经建好的一级表（EnumerateAllAsync 不消耗 TEMP 表），
        // 加一个"路径只有一段"的结构过滤，所以仍然是流式的、不需要第二张表。
        string rootShardPath = _normalizer.Combine(segmentDir, RootShardName + JsonExtension);
        long rootEntries = await WriteShardFileAsync(
                session.Header, rootShardPath, table.EnumerateAllAsync(ct),
                row => IsDirectChildOf(row.PathKey, prefixLength), deleteWhenEmpty: true, ct)
            .ConfigureAwait(false);

        if (rootEntries > 0)
        {
            session.Expected.Add(rootShardPath);
            shardCount++;
        }

        foreach (string second in secondSegments.Values)
        {
            ct.ThrowIfCancellationRequested();

            // 前缀必须**拼成一段**再传：BuildAsync 里的多个前缀是 OR 关系，
            // 传 ["docs", "docs\sub"] 会退化成"整个 docs"，二级分片也就白分了。
            string prefix = _normalizer.Combine(segment, second);

            await using PreviousMirrorTable sub = await PreviousMirrorTable
                .BuildAsync(session.Db, session.ChainIds, session.Header.TaskId, [prefix], _normalizer, ct)
                .ConfigureAwait(false);

            // 二级片名同样要转义：`<一级>\_root.json` 是"该一级目录下直接的文件"的保留片名，
            // 一个真名叫 `_root` 的二级目录否则会与之撞车。
            string path = _normalizer.Combine(segmentDir, EscapeSegment(second) + JsonExtension);
            long entries = await WriteShardFileAsync(
                    session.Header, path, sub.EnumerateAllAsync(ct),
                    filter: null, deleteWhenEmpty: true, ct)
                .ConfigureAwait(false);

            if (entries > 0)
            {
                session.Expected.Add(path);
                shardCount++;
            }
        }

        // 一级片与二级布局不能并存（02 §6.2）
        _fs.DeleteFile(ShardPath(session, segment));

        // 上一版留下、现在空了的二级片要删掉，否则读者会看到已经不存在的文件。
        CleanSegmentDir(session, segmentDir);

        return shardCount;
    }

    // ------------------------------------------------------------------
    // 落盘
    // ------------------------------------------------------------------

    /// <summary>
    /// 流式写一个清单文件：先写自描述的头部，再把 <paramref name="rows"/> 逐条写进 <c>files</c>。
    /// </summary>
    /// <remarks>
    /// **不构造对象再序列化**——百万条目的中间对象就是百万次分配，
    /// 与 <c>PreviousMirrorTable</c> 的"全程 O(1) 内存"直接冲突。
    /// </remarks>
    /// <param name="filter">
    /// 条目过滤器。用于"一级目录下直接的文件"这种无法用前缀表达的子集——仍然是流式过滤。
    /// </param>
    /// <param name="deleteWhenEmpty">
    /// 条目为 0 时是否删除该文件。片为 true（空片的存在会暗示里面有条目），
    /// 单文件清单为 false（它本身就是清单，空镜像也是一份有效的自描述文档）。
    /// </param>
    /// <returns>写出的条目数。</returns>
    private async Task<long> WriteShardFileAsync(
        ManifestHeader header,
        string filePath,
        IAsyncEnumerable<MirrorRow> rows,
        Func<MirrorRow, bool>? filter,
        bool deleteWhenEmpty,
        CancellationToken ct)
    {
        long entries = 0;

        // 注意顺序：writer 先于 stream 释放（using 声明按相反顺序释放），
        // 而 InMemoryFileSystem 的写入流是 Dispose 时才把内容提交进去的。
        using (Stream stream = _fs.Create(filePath))
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            WriteHeader(writer, header);

            writer.WriteStartArray("files");
            await foreach (MirrorRow row in rows.WithCancellation(ct))
            {
                if (filter is not null && !filter(row))
                {
                    continue;
                }

                WriteFileEntry(writer, row);
                entries++;
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.Flush();
            stream.Flush();
        }

        if (entries == 0 && deleteWhenEmpty)
        {
            _fs.DeleteFile(filePath);
        }

        return entries;
    }

    private static void WriteHeader(Utf8JsonWriter writer, ManifestHeader header)
    {
        writer.WriteStartObject();

        writer.WriteString("tool", ToolName);
        writer.WriteNumber("manifestVersion", ManifestVersion);
        writer.WriteString("generatedAt", FormatTime(header.GeneratedAt));
        writer.WriteString("taskId", header.TaskId.ToString("D"));
        writer.WriteString("taskName", header.TaskName);
        writer.WriteString("sourceDir", header.SourceDir);
        writer.WriteString("backupDir", header.BackupDir);
        writer.WriteString("blobLayout", BlobLayout);

        // 头部字段刻意**每个片都带**：分片被单独拿走（拷走一个文件、或只抢救到一个目录）时，
        // 它仍然是自解释的，不需要旁边的片来补上下文。
        writer.WriteString("restoreHint", RestoreHint);

        writer.WriteNumber("fileCount", header.FileCount);
        writer.WriteNumber("totalBytes", header.TotalBytes);
        writer.WriteNumber("latestSnapshotId", header.LatestSnapshotId);
        writer.WriteString("latestSnapshotTime", FormatTime(header.LatestSnapshotTime));
    }

    private static void WriteFileEntry(Utf8JsonWriter writer, MirrorRow row)
    {
        writer.WriteStartObject();
        writer.WriteString("path", row.Path);

        // 虚拟全量（以及任何 ContentHash 为 null 的记录）**如实写 null**，
        // 不能因为"还原时用不上"就丢条目——清单必须与镜像一一对应，
        // 否则它会静默地少列文件，而用户无从察觉。也不能伪造一个哈希。
        if (row.ContentHash is null)
        {
            writer.WriteNull("hash");
        }
        else
        {
            writer.WriteString("hash", row.ContentHash);
        }

        writer.WriteNumber("size", row.Length);
        writer.WriteString("mtime", FormatTime(row.FileTimeUtc));
        writer.WriteEndObject();
    }

    /// <summary>
    /// UTC 时间 → ISO 8601（<c>O</c> 往返格式，含亚秒）。
    /// </summary>
    /// <remarks>
    /// 刻意用 <c>SpecifyKind</c> 而不是 <c>ToUniversalTime</c>：全库时间戳一律 UTC 存储，
    /// 但 SQLite 把 <see cref="DateTime"/> 存成不带时区标记的 TEXT，从库里读回来的
    /// <c>Kind</c> 是 <c>Unspecified</c>；<c>ToUniversalTime</c> 会把它**当成本地时间**再减时差，
    /// 于是每个 mtime 都会被平移。亚秒也不能省——mtime 参与变更分类，截断会让下次备份
    /// 把所有文件判成"已修改"。
    /// </remarks>
    private static string FormatTime(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------
    // 路径与清理
    // ------------------------------------------------------------------

    private string ShardPath(Session session, string? segment) =>
        _normalizer.Combine(session.ManifestDir, (segment is null ? RootShardName : EscapeSegment(segment)) + JsonExtension);

    private string SegmentDir(Session session, string segment) =>
        _normalizer.Combine(session.ManifestDir, EscapeSegment(segment));

    /// <summary>
    /// 把真实目录段名转成**不可能与保留名冲突**的片名。
    /// </summary>
    /// <remarks>
    /// 保留名 <see cref="RootShardName"/>（<c>_root</c>）用在两个地方：源目录根下直接的文件，
    /// 以及二级分片时"该一级目录下直接的文件"。如果某个真实目录正好叫 <c>_root</c>，
    /// 两者会写到同一个文件上——**先写的被后写的覆盖，而且不报任何错**。
    ///
    /// <para>
    /// 转义必须是**单射**的，否则只是把冲突换了个名字：若只把 <c>_root</c> 改成 <c>_root_</c>，
    /// 那么真名叫 <c>_root_</c> 的目录又会冲突。所以对整个段名做两个替换：
    /// <c>%</c> → <c>%25</c>，<c>_</c> → <c>%5F</c>。
    /// 于是：真 <c>_root</c> → <c>%5Froot</c>；真 <c>%5Froot</c> → <c>%255Froot</c>；真 <c>root</c> → <c>root</c>。
    /// 任何真实段名都不可能再变成字面的 <c>_root</c>，保留名因此安全。
    /// </para>
    /// <para>
    /// 普通名字（不含 <c>%</c> 与 <c>_</c>）完全不受影响，片名与设计文档 §6.2 的
    /// <c>&lt;一级子目录名&gt;.json</c> 一致。
    /// </para>
    /// </remarks>
    private static string EscapeSegment(string segment) => segment
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("_", "%5F", StringComparison.Ordinal);

    /// <summary>把 <c>&lt;一级&gt;\&lt;二级&gt;...</c> 拆成一级段与剩余部分。</summary>
    private static bool TrySplitFirstSegment(string relativePath, out string segment, out string? rest)
    {
        string path = relativePath.Replace('/', '\\').Trim('\\');
        int separator = path.IndexOf('\\');

        if (separator <= 0 || separator == path.Length - 1)
        {
            segment = string.Empty;
            rest = null;
            return false;
        }

        segment = path[..separator];
        rest = path[(separator + 1)..];
        return true;
    }

    /// <summary>二级段 = 去掉一级段之后剩下的部分里，再取第一段。</summary>
    private static bool TrySplitSecondSegment(string relativePath, out string second, out string? rest)
    {
        second = string.Empty;
        rest = null;

        return TrySplitFirstSegment(relativePath, out _, out string? afterFirst)
               && afterFirst is not null
               && TrySplitFirstSegment(afterFirst, out second, out rest);
    }

    /// <summary>该路径是否**恰好**是一级段下的直接子项（即路径里只有一个分隔符）。</summary>
    private static bool IsDirectChildOf(string pathKey, int prefixLength)
    {
        if (pathKey.Length <= prefixLength + 1 || pathKey[prefixLength] != '\\')
        {
            return false;
        }

        return pathKey.IndexOf('\\', prefixLength + 1) < 0;
    }

    /// <summary>清掉某个一级目录下不再属于当前布局的片文件；目录空了就连目录一起删。</summary>
    private void CleanSegmentDir(Session session, string segmentDir)
    {
        if (!_fs.DirectoryExists(segmentDir))
        {
            return;
        }

        bool hasExpected = false;
        foreach (FileEntry file in _fs.EnumerateFiles(segmentDir, FileEnumerationOptions.Recursive).ToList())
        {
            if (session.Expected.Contains(file.FullPath))
            {
                hasExpected = true;
            }
            else
            {
                _fs.DeleteFile(file.FullPath);
            }
        }

        if (!hasExpected)
        {
            _fs.DeleteDirectory(segmentDir, recursive: true);
        }
    }

    /// <summary>整体重建后，清掉 <c>.manifest</c> 下不再属于当前镜像的片与其空目录。</summary>
    private void CleanManifestDir(Session session)
    {
        if (!_fs.DirectoryExists(session.ManifestDir))
        {
            return;
        }

        foreach (FileEntry file in _fs.EnumerateFiles(session.ManifestDir, FileEnumerationOptions.Recursive).ToList())
        {
            if (!session.Expected.Contains(file.FullPath))
            {
                _fs.DeleteFile(file.FullPath);
            }
        }

        foreach (DirectoryEntry dir in _fs
                     .EnumerateDirectories(session.ManifestDir, FileEnumerationOptions.Recursive)
                     .ToList())
        {
            if (!_fs.EnumerateFiles(dir.FullPath, FileEnumerationOptions.Recursive).Any())
            {
                _fs.DeleteDirectory(dir.FullPath, recursive: true);
            }
        }
    }

    // ------------------------------------------------------------------

    /// <summary>一个片文件的头部。分片之间只共享它，条目各写各的。</summary>
    private readonly record struct ManifestHeader(
        Guid TaskId,
        string TaskName,
        string SourceDir,
        string BackupDir,
        long FileCount,
        long TotalBytes,
        int LatestSnapshotId,
        DateTime LatestSnapshotTime,
        DateTime GeneratedAt);

    /// <summary>
    /// 一次 <see cref="WriteAsync"/> 的全部上下文。
    /// </summary>
    /// <remarks>
    /// 刻意做成参数对象而不是实例字段：<c>IManifestWriter</c> 是单例，
    /// 把任务相关状态挂在实例上，两个任务并发备份时会互相串台。
    /// </remarks>
    private sealed class Session(
        AppDbContext db,
        IReadOnlyList<int> chainIds,
        ManifestHeader header,
        string manifestDir,
        string manifestFile,
        IEqualityComparer<string> pathComparer)
    {
        public AppDbContext Db { get; } = db;

        public IReadOnlyList<int> ChainIds { get; } = chainIds;

        public ManifestHeader Header { get; } = header;

        public string ManifestDir { get; } = manifestDir;

        public string ManifestFile { get; } = manifestFile;

        /// <summary>本次真正写出来的片文件（整体重建时用来清理上一版的残留）。</summary>
        public HashSet<string> Expected { get; } = new(pathComparer);
    }
}
