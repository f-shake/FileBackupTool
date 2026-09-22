using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.Engine.Mirror;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine;

/// <summary>
/// 镜像浏览与文件历史。设计文档 02 §3.1。
/// </summary>
/// <remarks>
/// <para>
/// <b>不把整条链 replay 进内存</b>。做法：
/// </para>
/// <list type="number">
/// <item>由目标快照向前找到最近一次 Full/VirtualFull，得到这条链上的快照 id 集合。</item>
/// <item>一条 SQL 取回**该路径前缀**在链上所有快照中的记录。</item>
/// <item>只对这一个小集合在内存里 replay。</item>
/// <item>服务端分页返回。</item>
/// </list>
/// <para>
/// <b>已知的性能悬崖</b>（设计文档 08 §5 第 2 条）：列一个**很大**目录的直接内容时，
/// 前缀是空串，第 2 步会取回整棵子树的记录。子目录通常几百到几万条没问题，
/// 百万文件的根目录会很重。v1 接受这个代价（这是一个已知的、需要实测的悬崖）；
/// 后续方向是链长/子树超阈值时后台物化。
/// </para>
/// </remarks>
public sealed class MirrorQueryService(
    IDbContextFactory<AppDbContext> dbFactory,
    SnapshotChainService chains,
    FileRecordStore fileRecords,
    IPathNormalizer normalizer,
    ILogger<MirrorQueryService> logger) : IMirrorQueryService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly SnapshotChainService _chains = chains;
    private readonly FileRecordStore _fileRecords = fileRecords;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly ILogger<MirrorQueryService> _logger = logger;

    public async Task<PagedResult<MirrorEntry>> ListDirectoryAsync(
        Guid taskId,
        int snapshotId,
        string relativePath,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        Dictionary<string, MirrorItem>? items = await LoadSubtreeAsync(taskId, snapshotId, relativePath, ct)
            .ConfigureAwait(false);
        if (items is null)
        {
            return PagedResult<MirrorEntry>.Empty(NormalizePage(page), NormalizePageSize(pageSize));
        }

        string directoryKey = NormalizeRelative(relativePath);
        var files = new Dictionary<string, MirrorItem>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        MirrorReplay.GroupChildren(items, directoryKey, files, directories);

        var entries = new List<MirrorEntry>(files.Count + directories.Count);

        foreach (string dir in directories.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new MirrorEntry(
                Join(directoryKey, dir), dir, IsDirectory: true, 0, default, null, snapshotId, SnapshotType.Increment));
        }

        foreach ((string name, MirrorItem item) in files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase))
        {
            entries.Add(new MirrorEntry(
                item.Path, name, IsDirectory: false, item.Length, item.FileTimeUtc, item.ContentHash,
                item.SnapshotId, SnapshotType.Increment));
        }

        return Page(entries, page, pageSize);
    }

    public async Task<IReadOnlyList<string>> ListDirectoryTreeAsync(
        Guid taskId,
        int snapshotId,
        string relativePath,
        CancellationToken ct)
    {
        Dictionary<string, MirrorItem>? items = await LoadSubtreeAsync(taskId, snapshotId, relativePath, ct)
            .ConfigureAwait(false);
        if (items is null)
        {
            return [];
        }

        string directoryKey = NormalizeRelative(relativePath);
        string prefix = directoryKey.Length == 0 ? string.Empty : directoryKey + "\\";

        var tree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in items.Keys)
        {
            if (key.Length <= prefix.Length || !key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string remainder = key[prefix.Length..];
            int lastSeparator = remainder.LastIndexOf('\\');
            if (lastSeparator < 0)
            {
                continue;
            }

            // 逐级展开，保证导航树上每一层都在
            for (int i = remainder.IndexOf('\\'); i >= 0; i = remainder.IndexOf('\\', i + 1))
            {
                tree.Add(Join(directoryKey, remainder[..i]));
            }
        }

        return [.. tree.OrderBy(d => d, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<FileVersionEntry>> GetFileHistoryAsync(
        Guid taskId,
        string relativePath,
        CancellationToken ct)
    {
        string key = _normalizer.Normalize(relativePath);

        // 文件历史要覆盖该路径**所有**快照，所以用全部 Completed 快照作为范围
        List<BackupSnapshot> completed = await _chains.GetCompletedAsync(taskId, ct).ConfigureAwait(false);
        if (completed.Count == 0)
        {
            return [];
        }

        Dictionary<int, BackupSnapshot> byId = completed.ToDictionary(s => s.Id);
        List<BackupFile> records = await _fileRecords
            .GetFileHistoryAsync(taskId, key, [.. completed.Select(s => s.Id)], ct)
            .ConfigureAwait(false);

        return
        [
            .. records
                .Where(r => byId.ContainsKey(r.SnapshotId))
                .Select(r => new FileVersionEntry(
                    r.SnapshotId,
                    byId[r.SnapshotId].Type,
                    byId[r.SnapshotId].BeginTime,
                    r.Type,
                    r.Length,
                    r.FileTimeUtc,
                    r.ContentHash))
        ];
    }

    public async Task<PagedResult<MirrorEntry>> SearchAsync(
        Guid taskId,
        int snapshotId,
        string keyword,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return PagedResult<MirrorEntry>.Empty(NormalizePage(page), NormalizePageSize(pageSize));
        }

        Dictionary<string, MirrorItem>? items = await LoadSubtreeAsync(taskId, snapshotId, string.Empty, ct)
            .ConfigureAwait(false);
        if (items is null)
        {
            return PagedResult<MirrorEntry>.Empty(NormalizePage(page), NormalizePageSize(pageSize));
        }

        var matches = new List<MirrorEntry>();
        foreach (MirrorItem item in items.Values)
        {
            string name = _normalizer.GetFileName(item.Path);
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(new MirrorEntry(item.Path, name, false, item.Length, item.FileTimeUtc,
                    item.ContentHash, item.SnapshotId, SnapshotType.Increment));
            }
        }

        matches.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return Page(matches, page, pageSize);
    }

    public async Task<PagedResult<MirrorEntry>> ListChangesAsync(
        int snapshotId,
        FileRecordType? type,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        IQueryable<BackupFile> query = db.Files.Where(f => f.SnapshotId == snapshotId && !f.IsDeleted);
        if (type.HasValue)
        {
            query = query.Where(f => f.Type == type.Value);
        }

        int total = await query.CountAsync(ct).ConfigureAwait(false);
        int p = NormalizePage(page);
        int size = NormalizePageSize(pageSize);

        List<BackupFile> rows = await query
            .OrderBy(f => f.RelativePathKey)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var items = rows
            .Select(r => new MirrorEntry(
                r.RelativePath, _normalizer.GetFileName(r.RelativePath), false,
                r.Length, r.FileTimeUtc, r.ContentHash, r.SnapshotId, SnapshotType.Increment))
            .ToList();

        return new PagedResult<MirrorEntry>(items, total, p, size);
    }

    public async Task<MirrorEntry?> GetFileAsync(int snapshotId, string relativePath, CancellationToken ct)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        BackupFile? record = await db.Files
            .FirstOrDefaultAsync(f => f.SnapshotId == snapshotId && f.RelativePathKey == _normalizer.Normalize(relativePath)
                                      && !f.IsDeleted, ct)
            .ConfigureAwait(false);

        // 该快照本身没记录不代表文件不存在——它是从更早的快照继承来的。
        if (record is null)
        {
            BackupSnapshot? snapshot = await db.Snapshots
                .FirstOrDefaultAsync(s => s.Id == snapshotId, ct)
                .ConfigureAwait(false);
            if (snapshot is null)
            {
                return null;
            }

            Dictionary<string, MirrorItem>? items = await LoadSubtreeAsync(snapshot.TaskId, snapshotId, relativePath, ct)
                .ConfigureAwait(false);
            string key = _normalizer.Normalize(relativePath);
            if (items is null || !items.TryGetValue(key, out MirrorItem item))
            {
                return null;
            }

            return new MirrorEntry(item.Path, _normalizer.GetFileName(item.Path), false,
                item.Length, item.FileTimeUtc, item.ContentHash, item.SnapshotId, SnapshotType.Increment);
        }

        return new MirrorEntry(record.RelativePath, _normalizer.GetFileName(record.RelativePath), false,
            record.Length, record.FileTimeUtc, record.ContentHash, record.SnapshotId, SnapshotType.Increment);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 取某个子树在链上的全部存活的镜像条目。链断裂或快照不存在时返回 null。
    /// </summary>
    private async Task<Dictionary<string, MirrorItem>?> LoadSubtreeAsync(
        Guid taskId,
        int snapshotId,
        string relativePath,
        CancellationToken ct)
    {
        List<BackupSnapshot>? chain = await _chains.ResolveChainAsync(taskId, snapshotId, ct).ConfigureAwait(false);
        if (chain is null || chain.Count == 0)
        {
            return null!;
        }

        string? prefixKey = string.IsNullOrEmpty(relativePath) ? null : _normalizer.Normalize(relativePath);
        List<BackupFile> records = await _fileRecords
            .GetChainRecordsInPrefixAsync(taskId, [.. chain.Select(s => s.Id)], prefixKey, ct)
            .ConfigureAwait(false);

        ReplayResult replay = MirrorReplay.Replay(records);

        if (replay.Issues > 0)
        {
            _logger.LogWarning(
                "镜像重放发现 {Count} 处结构不一致（任务 {TaskId} 快照 #{SnapshotId} 路径 {Path}）。",
                replay.Issues, taskId, snapshotId, relativePath);
        }

        return replay.Items;
    }

    private string Join(string directoryKey, string name) =>
        directoryKey.Length == 0 ? name : directoryKey + "\\" + name;

    private string NormalizeRelative(string relativePath) => _normalizer.Normalize(relativePath ?? string.Empty);

    private static int NormalizePage(int page) => page < 1 ? 1 : page;

    private static int NormalizePageSize(int pageSize) => pageSize switch
    {
        < 1 => 50,
        > 1000 => 1000,
        _ => pageSize,
    };

    private static PagedResult<MirrorEntry> Page(List<MirrorEntry> all, int page, int pageSize)
    {
        int p = NormalizePage(page);
        int size = NormalizePageSize(pageSize);
        int skip = (p - 1) * size;

        return skip >= all.Count
            ? new PagedResult<MirrorEntry>([], all.Count, p, size)
            : new PagedResult<MirrorEntry>([.. all.Skip(skip).Take(size)], all.Count, p, size);
    }
}
