using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 文件与历史（设计文档 06 §4.3）。
/// </summary>
[Route("api/tasks/{id:guid}")]
public sealed class FilesController(
    IMirrorQueryService mirror,
    SnapshotChainService chains,
    TaskStore tasks,
    ApiMapper mapper,
    ILogger<FilesController> logger) : ApiControllerBase
{
    private readonly IMirrorQueryService _mirror = mirror;
    private readonly SnapshotChainService _chains = chains;
    private readonly TaskStore _tasks = tasks;
    private readonly ApiMapper _mapper = mapper;
    private readonly ILogger<FilesController> _logger = logger;

    /// <summary>某个路径的所有版本 + 快照时间（设计文档 06 §4.3）。</summary>
    [HttpGet("file-history")]
    public async Task<FileHistoryResultDto> FileHistoryAsync(
        [FromRoute] Guid id,
        [FromQuery] string? path)
    {
        await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        string relative = NormalizeRelative(path);
        if (relative.Length == 0)
        {
            throw ApiException.BadRequest(ApiErrorCodes.PathNotFound, "必须通过 ?path= 指定一个相对路径。");
        }

        IReadOnlyList<FileVersionEntry> versions = await _mirror
            .GetFileHistoryAsync(id, relative, Ct).ConfigureAwait(false);

        // 每个版本的"所属快照"要补两个字段：结束时间与是否可还原。
        // 一次取全部已完成快照做字典，而不是逐版本查一次库（N+1）。
        List<BackupSnapshot> completed = await _chains.GetCompletedAsync(id, Ct).ConfigureAwait(false);
        Dictionary<int, BackupSnapshot> byId = completed.ToDictionary(s => s.Id);

        return new FileHistoryResultDto(
            relative.Replace('\\', '/'),
            [
                .. versions.Select(v =>
                {
                    // 链只由 Completed 快照组成，所以正常一定能查到；
                    // 查不到时不编造（宁可让"可还原"显示为否），只在日志里留一句。
                    bool found = byId.TryGetValue(v.SnapshotId, out BackupSnapshot? snapshot);
                    if (!found)
                    {
                        _logger.LogWarning(
                            "文件历史里的快照 {SnapshotId} 不在已完成快照集合内（任务 {TaskId}）。",
                            v.SnapshotId, id);
                    }

                    return _mapper.Version(
                        v,
                        relative,
                        snapshotRestorable: snapshot?.IsRestorable ?? false,
                        snapshotEndTime: snapshot?.EndTime);
                }),
            ]);
    }

    /// <summary>
    /// 在指定快照的镜像中按文件名搜索（真过滤，不是"展开+多选"）。
    /// </summary>
    /// <remarks>
    /// <b>为什么显式拒绝 <c>path</c> / <c>regex</c> / <c>caseSensitive</c> 这三个参数</b>：
    /// 引擎的 <c>SearchAsync</c> 只接受一个关键字，任何额外语义都**无法真正生效**。
    /// 静默忽略它们会让用户以为"限定了目录 / 用了正则 / 区分了大小写"，
    /// 而实际上拿到的是全库模糊匹配的结果——这种"看起来生效了"的偏差比直接报错危险得多。
    /// 所以只要请求方明确表达了这些语义，就明确拒绝。
    /// </remarks>
    [HttpGet("search")]
    public async Task<PagedDto<MirrorEntryDto>> SearchAsync(
        [FromRoute] Guid id,
        [FromQuery] int? snapshotId,
        [FromQuery] string? keyword,
        [FromQuery] string? path,
        [FromQuery] bool? regex,
        [FromQuery] bool? caseSensitive,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(path))
        {
            throw Unsupported("path", "只支持在整个镜像中按名称搜索，不支持限定目录。");
        }

        if (regex == true)
        {
            throw Unsupported("regex", "引擎的搜索不支持正则表达式，关键字按普通子串匹配。");
        }

        if (caseSensitive == true)
        {
            throw Unsupported("caseSensitive", "引擎的搜索不接受大小写敏感选项。");
        }

        if (snapshotId is null)
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "必须通过 ?snapshotId= 指定在哪个快照中搜索。");
        }

        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw ApiException.BadRequest(ApiErrorCodes.InvalidRequest, "必须通过 ?keyword= 指定搜索关键字。");
        }

        BackupSnapshot snapshot = await RequireSnapshotAsync(_chains, snapshotId.Value).ConfigureAwait(false);

        if (snapshot.TaskId != id)
        {
            // 快照不属于该任务时明确报错，而不是"搜出空结果"——
            // 后者会让用户以为文件真的不在备份里。
            throw ApiException.BadRequest(
                ApiErrorCodes.SnapshotNotFound,
                $"快照 {snapshotId} 不属于任务 {id}。");
        }

        (int p, int size) = PageQuery.Normalize(page, pageSize);

        PagedResult<MirrorEntry> result = await _mirror
            .SearchAsync(id, snapshotId.Value, keyword.Trim(), p, size, Ct).ConfigureAwait(false);

        bool restorable = snapshot.IsRestorable;

        return PagedDto<MirrorEntryDto>.From(result, e => _mapper.Mirror(e, restorable));
    }

    private static ApiException Unsupported(string parameter, string message) =>
        ApiException.BadRequest(
            ApiErrorCodes.InvalidRequest,
            $"参数 {parameter} 不受支持：{message}",
            $"该接口当前只支持 ?snapshotId=&keyword=&page=&pageSize=（设计文档 06 §4.3）。"
            + $"请去掉 {parameter} 参数后重试。");

    /// <summary>归一化查询串里的镜像相对路径（允许 <c>/</c> 或 <c>\</c>，拒绝穿越片段）。</summary>
    private static string NormalizeRelative(string? path)
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
            throw ApiException.BadRequest(
                ApiErrorCodes.PathContainsRelativeSegment,
                "路径中包含 '.' 或 '..' 片段，已被拒绝。");
        }

        return value.Replace('/', '\\');
    }
}
