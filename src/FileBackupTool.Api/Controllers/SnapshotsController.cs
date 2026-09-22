using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 快照端点（设计文档 06 §4.2）。
/// </summary>
/// <remarks>
/// 路由前缀是 <c>api</c> 而不是 <c>api/snapshots</c>：这些端点分居两棵子树
/// （<c>tasks/{id}/snapshots</c> 与 <c>snapshots/{sid}</c>），
/// 用类级 <c>[Route("api")]</c> + 动作级完整模板比"两个控制器各管一半"更不容易漏。
/// </remarks>
[Route("api")]
public sealed class SnapshotsController(
    ISnapshotService snapshots,
    IMirrorQueryService mirror,
    SnapshotChainService chains,
    TaskStore tasks,
    ApiMapper mapper,
    IBackupLogSink logSink,
    AuditLogger audit,
    IClock clock,
    IPathNormalizer normalizer,
    ILogger<SnapshotsController> logger) : ApiControllerBase
{
    /// <summary>单次镜像浏览里"当前目录统计"的统计窗口上限（与分页上限一致）。</summary>
    private const int StatisticsWindow = PageQuery.MaxPageSize;

    private readonly ISnapshotService _snapshots = snapshots;
    private readonly IMirrorQueryService _mirror = mirror;
    private readonly SnapshotChainService _chains = chains;
    private readonly TaskStore _tasks = tasks;
    private readonly ApiMapper _mapper = mapper;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly AuditLogger _audit = audit;
    private readonly IClock _clock = clock;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly ILogger<SnapshotsController> _logger = logger;

    /// <summary>快照列表（按全量快照分组，支持类型 / 日期筛选与分页）。</summary>
    [HttpGet("tasks/{id:guid}/snapshots")]
    public async Task<PagedDto<SnapshotGroupDto>> ListAsync(
        [FromRoute] Guid id,
        [FromQuery] string? type,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        (int p, int size) = PageQuery.Normalize(page, pageSize);

        PagedResult<SnapshotGroup> result = await _snapshots
            .ListAsync(id, ParseType(type), ToUtc(from), ToUtc(to), p, size, Ct)
            .ConfigureAwait(false);

        return PagedDto<SnapshotGroupDto>.From(result, group => new SnapshotGroupDto(
            _mapper.Snapshot(group.Full),

            // 分组里只放**增量**。前端契约（types.ts 的 SnapshotGroup）明确写的就是
            // "fullSnapshot + increments"，把全量自己也塞进数组会让同一行出现两次，
            // 而前端的"链长度"显示会多算一个。
            [.. group.Increments.Select(_mapper.Snapshot)]));
    }

    /// <summary>快照详情（计数、失败数、占用空间）。</summary>
    [HttpGet("snapshots/{sid:int}")]
    public async Task<SnapshotDto> GetAsync([FromRoute] int sid)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);
        BackupTask? task = await _tasks.GetAsync(snapshot.TaskId, Ct).ConfigureAwait(false);

        return _mapper.SnapshotDetail(snapshot, task?.Name);
    }

    /// <summary>级联删除影响清单。**删除前必须调它**（04 §2.2）。</summary>
    [HttpGet("snapshots/{sid:int}/delete-impact")]
    public async Task<DeleteImpactDto> DeleteImpactAsync([FromRoute] int sid)
    {
        await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);

        DeleteImpact? impact = await _snapshots.PreviewDeleteAsync(sid, Ct).ConfigureAwait(false);

        if (impact is null)
        {
            throw ApiException.NotFound(ApiErrorCodes.SnapshotNotFound, $"快照 {sid} 不存在。");
        }

        return new DeleteImpactDto
        {
            SnapshotId = sid,

            // 按开始时间升序：对话框里"这条链会被整体删掉"的时间线要能顺着读。
            AffectedSnapshots = [.. impact.Affected.OrderBy(s => s.BeginTime).Select(_mapper.Snapshot)],
            AffectedCount = impact.AffectedCount,
            AffectedFileCount = impact.TotalFileRecords,

            // 本实现一律软删除（决策 #12），所以"将被软删除的记录数"就等于记录总数。
            SoftDeletedFileCount = impact.TotalFileRecords,

            // 软删除**不会立即释放空间**：blob 要等没有任何记录引用（含软删除的记录）时才由
            // 回收站接管，而回收站本身也要用户手动清空才真的落盘释放。
            // 这个字段因此是"最终可能释放"的上界，不是"现在能多出多少空间"。
            ReclaimableBytes = impact.TotalBytes,
            Notices =
            [
                "删除是**软删除**：立刻生效且不可撤销，但磁盘空间不会立即释放。",
                "对应的 blob 只有在该任务下再没有任何记录引用时才会移入回收站，"
                + "而回收站需要手动清空才会真正释放空间。",
            ],
        };
    }

    /// <summary>级联删除快照（软删除）。</summary>
    [HttpDelete("snapshots/{sid:int}")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> DeleteAsync([FromRoute] int sid)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);
        BackupTask? task = await _tasks.GetAsync(snapshot.TaskId, Ct).ConfigureAwait(false);

        // 先算级联范围再删：删完之后 <c>PreviewDeleteAsync</c> 就查不到这些快照了，
        // 而审计里"到底删了几个"必须准确（这是不可逆操作）。
        DeleteImpact? impact = await _snapshots.PreviewDeleteAsync(sid, Ct).ConfigureAwait(false);
        int affected = impact?.AffectedCount ?? 1;

        await _snapshots.DeleteAsync(sid, Ct).ConfigureAwait(false);

        string detail =
            $"级联删除快照 {sid}（类型 {snapshot.Type}，开始 {snapshot.BeginTime:u}），" +
            $"共影响 {affected} 个快照、{impact?.TotalFileRecords ?? 0} 条文件记录（软删除）。";

        await _audit.WriteAsync(
            AuditEventType.SnapshotDeleted,
            target: $"{task?.Name ?? "(未知任务)"}/{sid}",
            detail: detail,
            ct: Ct).ConfigureAwait(false);

        if (task is not null)
        {
            await _logSink.WriteAsync(task.Id, null, LogLevel.Warning,
                $"快照 {sid} 及其增量已被删除（软删除，空间未立即释放）。", detail, Ct)
                .ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// 变更内容（新增 / 修改 / 删除三类，各类独立分页）。
    /// </summary>
    /// <remarks>
    /// 三类各自分页，是因为前端把它们放在三个页签里——共用一个页码会让翻到第 3 页的"删除"
    /// 顺手把"新增"也翻到第 3 页，而用户根本没在那一栏翻页。
    /// 默认三类都用 <c>?page=</c>，需要单独翻某一类时用 <c>?createdPage=</c> 之类覆盖。
    /// </remarks>
    [HttpGet("snapshots/{sid:int}/changes")]
    public async Task<SnapshotChangesDto> ChangesAsync(
        [FromRoute] int sid,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        [FromQuery] int? createdPage,
        [FromQuery] int? modifiedPage,
        [FromQuery] int? deletedPage)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);
        (int p, int size) = PageQuery.Normalize(page, pageSize);
        bool restorable = snapshot.IsRestorable;

        PagedResult<MirrorEntry> created = await _mirror
            .ListChangesAsync(sid, FileRecordType.Created, createdPage ?? p, size, Ct).ConfigureAwait(false);
        PagedResult<MirrorEntry> modified = await _mirror
            .ListChangesAsync(sid, FileRecordType.Modified, modifiedPage ?? p, size, Ct).ConfigureAwait(false);
        PagedResult<MirrorEntry> deleted = await _mirror
            .ListChangesAsync(sid, FileRecordType.Deleted, deletedPage ?? p, size, Ct).ConfigureAwait(false);

        return new SnapshotChangesDto(
            PagedDto<ChangeEntryDto>.From(created, e => _mapper.Change(View(e, FileRecordType.Created), restorable)),
            PagedDto<ChangeEntryDto>.From(modified, e => _mapper.Change(View(e, FileRecordType.Modified), restorable)),
            PagedDto<ChangeEntryDto>.From(deleted, e => _mapper.Change(View(e, FileRecordType.Deleted), restorable)));
    }

    /// <summary>
    /// 镜像浏览：列出该目录下的子目录与文件。
    /// </summary>
    /// <remarks>
    /// <b>统计字段的口径</b>：<c>dirCount</c> / <c>fileCount</c> / <c>totalBytes</c> 描述的是
    /// **当前目录**（不是整棵树）。它们由一次最多 1000 条的额外取数算出——
    /// 引擎一次列目录就会把该目录的记录全部 replay 一遍，再要一次同量级的查询并不便宜，
    /// 所以设了上限。目录里条目超过 1000 时，这三个数是**下界**（而不是谎报的精确值）：
    /// 前端的网格尾部统计会显示"至少这么多"。这是已知的取舍，已记在交付说明里。
    /// </remarks>
    [HttpGet("snapshots/{sid:int}/files")]
    public async Task<MirrorListingDto> FilesAsync(
        [FromRoute] int sid,
        [FromQuery] string? path,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);
        (int p, int size) = PageQuery.Normalize(page, pageSize);

        string relative = NormalizeRelative(path);

        PagedResult<MirrorEntry> listed = await _mirror
            .ListDirectoryAsync(snapshot.TaskId, sid, relative, p, size, Ct).ConfigureAwait(false);

        // 已经在取"第 1 页且取满上限"时，这一页就是统计窗口，不必再查一次。
        PagedResult<MirrorEntry> window = p == 1 && size >= StatisticsWindow
            ? listed
            : await _mirror
                .ListDirectoryAsync(snapshot.TaskId, sid, relative, 1, StatisticsWindow, Ct)
                .ConfigureAwait(false);

        bool restorable = snapshot.IsRestorable;
        List<MirrorEntry> windowItems = [.. window.Items];

        return new MirrorListingDto(
            [.. listed.Items.Select(e => _mapper.Mirror(e, restorable))],
            listed.Total,
            listed.Page,
            listed.PageSize,
            relative.Replace('\\', '/'),
            windowItems.Count(e => e.IsDirectory),
            windowItems.Count(e => !e.IsDirectory),
            windowItems.Where(e => !e.IsDirectory).Sum(e => e.Length));
    }

    /// <summary>目录树骨架（用于导航，不含文件）。只返回 <paramref name="path"/> 的直接子目录。</summary>
    /// <remarks>
    /// 引擎给的是**扁平的子孙目录清单**（<c>IReadOnlyList&lt;string&gt;</c>），
    /// 没有层级信息，所以这里按分隔符自己切一层出来。
    /// <c>fileCount</c> 恒为 0：引擎的树接口只返回目录，不返回文件——
    /// 前端不显示这个字段（见 types.ts 的 TreeNode），编一个数字出来是假信息。
    /// </remarks>
    [HttpGet("snapshots/{sid:int}/tree")]
    public async Task<IReadOnlyList<TreeNodeDto>> TreeAsync(
        [FromRoute] int sid,
        [FromQuery] string? path)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);

        string root = NormalizeRelative(path);
        string rootKey = root.Trim('\\', '/');

        IReadOnlyList<string> all = await _mirror
            .ListDirectoryTreeAsync(snapshot.TaskId, sid, root, Ct).ConfigureAwait(false);

        // 每个目录的直接子目录数（用于 hasChildren 与 dirCount）。
        var childCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in all)
        {
            string key = Key(dir);
            if (key.Length == 0)
            {
                continue;
            }

            int sep = key.LastIndexOf('\\');
            if (sep < 0)
            {
                continue;
            }

            string parent = key[..sep];
            childCounts[parent] = childCounts.GetValueOrDefault(parent) + 1;
        }

        var children = new List<TreeNodeDto>();

        foreach (string dir in all)
        {
            string key = Key(dir);
            string remainder = key;

            if (rootKey.Length > 0)
            {
                // 只认真正在根之下的条目。前缀比较用归一化后的键（Windows 大小写不敏感）。
                if (!key.StartsWith(rootKey + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                remainder = key[(rootKey.Length + 1)..];
            }

            if (remainder.Length == 0)
            {
                continue;
            }

            int sep = remainder.IndexOf('\\');
            string childName = sep < 0 ? remainder : remainder[..sep];
            string childKey = rootKey.Length == 0 ? childName : rootKey + "\\" + childName;

            if (children.Any(c => string.Equals(c.RelativePath, childKey.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            int descendants = childCounts.GetValueOrDefault(childKey);

            children.Add(new TreeNodeDto(
                childName,
                childKey.Replace('\\', '/'),
                HasChildren: descendants > 0,
                DirCount: descendants,

                // 见方法注释：引擎不返回文件，这里没有可信的数字可填。
                FileCount: 0));
        }

        return [.. children.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>单文件元数据（名称、大小、mtime、哈希、所属快照）。</summary>
    [HttpGet("snapshots/{sid:int}/file")]
    public async Task<FileMetadataDto> FileAsync([FromRoute] int sid, [FromQuery] string? path)
    {
        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, sid).ConfigureAwait(false);

        string relative = NormalizeRelative(path);
        if (relative.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.PathNotFound, "必须通过 ?path= 指定一个相对路径。");
        }

        MirrorEntry? entry = await _mirror.GetFileAsync(sid, relative, Ct).ConfigureAwait(false);

        if (entry is null)
        {
            throw ApiException.NotFound(
                ApiErrorCodes.PathNotFound,
                $"快照 {sid} 的镜像里没有「{relative}」。");
        }

        bool restorable = snapshot.IsRestorable;

        return new FileMetadataDto
        {
            SnapshotId = sid,
            RelativePath = _mapper.ToForwardSlash(entry.RelativePath),
            Name = entry.Name,
            Length = entry.Length,
            FileTimeUtc = entry.IsDirectory ? null : entry.FileTimeUtc,
            ContentHash = entry.ContentHash,
            SnapshotType = snapshot.Type.ToString(),
            SnapshotBeginTime = snapshot.BeginTime,
            Restorable = restorable && entry.ContentHash is not null,
            BlockedReason = Restorability.BlockedReason(restorable, entry.IsDirectory, entry.ContentHash),
        };
    }

    // ------------------------------------------------------------------

    private static SnapshotType? ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse(raw, ignoreCase: true, out SnapshotType type) && Enum.IsDefined(type)
            ? type
            : throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"快照类型「{raw}」无法识别，可选：Full / VirtualFull / Increment。");
    }

    /// <summary>
    /// 查询串里的时间。**前端一律发 UTC（带 Z 的 ISO 8601）**，所以这里只做一层保险：
    /// 没带时区的时间按 UTC 解释，而不是按服务器本地时区——
    /// 后者会在同一台机器换时区后让"筛选 8 月 1 日之后"的结果悄悄变化。
    /// </summary>
    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Utc } => value,
        { Kind: DateTimeKind.Local } => value.Value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc),
    };

    /// <summary>归一化查询串里的镜像相对路径（允许 <c>/</c> 或 <c>\</c>，拒绝穿越片段）。</summary>
    private string NormalizeRelative(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string value = path.Trim().Trim('\\', '/');

        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (Core.Paths.DirectoryRelationshipValidator.ContainsRelativeSegment(value))
        {
            // 镜像里的相对路径直接参与 SQL 前缀匹配，穿越片段必须在这里就挡掉
            //（而不是指望引擎再查一次）。
            throw ApiException.BadRequest(
                ApiErrorCodes.PathContainsRelativeSegment,
                "路径中包含 '.' 或 '..' 片段，已被拒绝。");
        }

        return value.Replace('/', '\\');
    }

    private string Key(string dir) => _normalizer.TrimTrailingSeparator(dir.Replace('/', '\\'));

    /// <summary>
    /// <see cref="MirrorEntry"/> → <see cref="BackupFile"/> 视图，只为了让 <see cref="ApiMapper.Change"/> 复用。
    /// </summary>
    /// <remarks>
    /// 变更接口（引擎）与浏览接口返回的都是 <see cref="MirrorEntry"/>，但"变更内容"这一栏
    /// 在契约里的形状与 <c>BackupFile</c> 一致（含 Created/Modified/Deleted 与墓碑信息）。
    /// 与其在映射器里再写一遍相同的转换，不如在这里做一次字段搬运。
    /// <para>
    /// <b><paramref name="type"/> 必须由调用方给</b>：<see cref="MirrorEntry"/> 不带记录类型
    /// （它描述的是"镜像里现在有什么"），而"这一行属于新增还是删除"只有**发起查询的那一类**
    /// 才知道。忘了传的话三栏都会显示成 <c>Created</c>，看起来完全合理但是错的。
    /// </para>
    /// </remarks>
    private static BackupFile View(MirrorEntry entry, FileRecordType type) => new()
    {
        RelativePath = entry.RelativePath,
        Type = type,
        Length = entry.Length,
        FileTimeUtc = entry.FileTimeUtc,
        ContentHash = entry.ContentHash,
    };
}
