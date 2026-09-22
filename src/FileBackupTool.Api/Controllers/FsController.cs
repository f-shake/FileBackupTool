using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Filtering;
using FileBackupTool.Core.IO;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 文件系统浏览（设计文档 06 §4.8、05 §8）。供前端的目录选择器使用。
/// </summary>
/// <remarks>
/// <para>
/// <b>本控制器是"只读且只出目录名"的</b>：三个端点加起来只暴露"有哪些目录、目录名、最后写入时间"。
/// 不返回任何文件、不返回文件内容、不返回大小甚至不列文件——设计文档 05 §8 的要求。
/// 这是一条**刻意的窄接口**：目录选择器只需要这些，而每多暴露一个字段，
/// 被攻破的会话就多一份可以直接读到的东西。
/// </para>
/// <para>
/// <b>为什么这里不做路径白名单</b>：登录者就是管理员（单用户系统），给他一个
/// "只能选白名单目录"的目录选择器没有任何安全收益——他本来就能改白名单。
/// 但**穿越防护必须有**：<c>..</c> 片段与重解析点（符号链接 / junction）一律拒绝，
/// 否则"选目录"就变成了"绕过应用自身路径逻辑去摸任意位置"。
/// 注意白名单在**还原**那一步是硬约束（<c>RestoreController</c> 先行校验），
/// 那条防线没有被这里削弱。
/// </para>
/// </remarks>
[Route("api/fs")]
public sealed class FsController(
    IFileSystem fs,
    IPathNormalizer normalizer,
    TaskStore tasks,
    SettingsService settings,
    DirectoryRelationshipValidator validator,
    AppPaths paths,
    ILogger<FsController> logger) : ApiControllerBase
{
    /// <summary>一次 <c>/fs/list</c> 最多返回的条目数。</summary>
    private const int MaxListEntries = 2000;

    /// <summary>探测 <c>hasChildren</c> 的条目数上限（见 <see cref="ListAsync"/>）。</summary>
    private const int HasChildrenProbeLimit = 1000;

    /// <summary><c>filter-test</c> 枚举的总条目上限（06 §4.8 建议 20 万）。</summary>
    private const int MaxScanEntries = 200_000;

    /// <summary><c>filter-test</c> 返回的文件清单条数上限（06 §4.8 建议 200）。</summary>
    private const int MaxListedFiles = 200;

    private readonly IFileSystem _fs = fs;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly TaskStore _tasks = tasks;
    private readonly SettingsService _settings = settings;
    private readonly DirectoryRelationshipValidator _validator = validator;
    private readonly AppPaths _paths = paths;
    private readonly ILogger<FsController> _logger = logger;

    /// <summary>可浏览的根：驱动器 + 已配置的根（任务备份目录、还原根白名单、用户主目录）。</summary>
    [HttpGet("roots")]
    public async Task<IReadOnlyList<FsRootDto>> RootsAsync()
    {
        var result = new List<FsRootDto>();
        var seen = new HashSet<string>(_normalizer.Comparer);

        foreach (DriveEntry drive in _fs.GetLogicalDrives())
        {
            if (string.IsNullOrWhiteSpace(drive.RootPath) || !seen.Add(drive.RootPath))
            {
                continue;
            }

            result.Add(new FsRootDto(
                drive.RootPath,
                drive.RootPath,
                drive.DriveType.ToString(),
                ProbeReady(drive.RootPath)));
        }

        // 驱动器之外还有三类"用户天天要选"的根：任务已有的备份目录、还原根白名单、用户主目录。
        // 它们的共同点是**由配置或系统给出**，不是用户在这个端点上现编的，
        // 所以列出来只是省掉几次点击，不构成新的信息暴露（它们本来就在任务/设置页里明文可见）。
        foreach (string extra in await CollectConfiguredRootsAsync().ConfigureAwait(false))
        {
            if (seen.Add(extra))
            {
                result.Add(new FsRootDto(extra, extra, "Configured", ProbeReady(extra)));
            }
        }

        return result;
    }

    /// <summary>
    /// 列出某个目录下的**子目录**（不列文件、不返回值）。
    /// </summary>
    /// <remarks>
    /// <c>hasChildren</c> 需要对每个子目录各做一次枚举才知道，所以设了
    /// <see cref="HasChildrenProbeLimit"/> 的上限：超过上限时返回 <c>null</c>（"未知"），
    /// 让界面显示为"可展开"而不是显示一个可能是假的 <c>false</c>。
    /// 前端契约里这个字段本来就是可选的（<c>hasChildren?</c>）。
    /// </remarks>
    [HttpGet("list")]
    public async Task<IReadOnlyList<FsEntryDto>> ListAsync([FromQuery] string? path)
    {
        string dir = RequireBrowsableDirectory(path);

        FileEnumerationOptions topOnly = FileEnumerationOptions.TopOnly;

        var entries = new List<FsEntryDto>();
        bool truncated = false;

        foreach (DirectoryEntry entry in _fs.EnumerateDirectories(dir, topOnly))
        {
            if (entries.Count >= MaxListEntries)
            {
                truncated = true;
                break;
            }

            // 重解析点（符号链接 / junction）**照常列出**（用户得看得见这里有个目录，
            // 否则就是"静默隐藏"），但：
            //   ① 绝不进去探测 hasChildren —— 那正是要防的穿越；
            //   ② 真正要"进去"的时候由 RequireBrowsableDirectory 直接拒绝。
            bool isLink = (entry.Attributes & FileAttributes.ReparsePoint) != 0;

            bool? hasChildren = isLink
                ? false
                : entries.Count < HasChildrenProbeLimit
                    ? _fs.EnumerateDirectories(entry.FullPath, topOnly).Any()
                    : null;

            entries.Add(new FsEntryDto(
                _normalizer.GetFileName(entry.FullPath),
                entry.FullPath,
                entry.LastWriteTimeUtc,
                hasChildren));
        }

        if (truncated)
        {
            // 这个响应体是个裸数组，**没有** truncated 字段可以如实标记，
            // 所以只能留一条明确的服务端日志。见交付说明里的已知限制。
            _logger.LogWarning(
                "目录 {Dir} 的子目录数超过 {Limit}，响应已截断（前端无法得知）。",
                dir, MaxListEntries);
        }

        return entries;
    }

    /// <summary>校验目录关系（源 vs 备份 vs 还原根）。**不落库、不抛错**，把冲突清单原样返回。</summary>
    [HttpPost("validate")]
    public async Task<DirValidationResultDto> ValidateAsync([FromBody] FsValidateRequestDto input)
    {
        var issues = new List<DirectoryValidationIssue>();

        List<BackupTask> others = await _tasks.GetForValidationAsync(input.TaskId, Ct).ConfigureAwait(false);

        bool hasSourcePair =
            !string.IsNullOrWhiteSpace(input.SourceDir) || !string.IsNullOrWhiteSpace(input.BackupDir);

        if (hasSourcePair)
        {
            IReadOnlyList<string> roots = await _settings.GetAllowedRestoreRootsAsync(Ct).ConfigureAwait(false);

            issues.AddRange(_validator.ValidateTask(
                input.SourceDir ?? string.Empty,
                input.BackupDir ?? string.Empty,
                others,
                roots));
        }

        if (!string.IsNullOrWhiteSpace(input.RestoreRoot))
        {
            // 还原根的校验要对照**全部任务**（不只是本任务）：冲突往往是
            // "我要加的这个还原根正好是另一个任务的备份目录"。
            List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, Ct).ConfigureAwait(false);

            issues.AddRange(_validator.ValidateRestoreRoot(input.RestoreRoot.Trim(), all));
        }

        if (!hasSourcePair && string.IsNullOrWhiteSpace(input.RestoreRoot))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "没有可校验的内容。",
                "至少给出 sourceDir + backupDir，或者 restoreRoot。");
        }

        return ValidationIssueMapper.ToResult(issues);
    }

    /// <summary>
    /// 筛选规则试算（06 §4.8）。返回"用当前规则筛这个目录会备份多少文件、多少字节"。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须与备份引擎走同一条链</b>，否则这个功能就是骗人的：用户在这里看到 100 个文件、
    /// 备份时却写进去 5000 个，比没有试算更糟。所以这里逐字复刻
    /// <c>BackupEngine.BuildFilter</c> + <c>EnumerateSource</c> 的两步：
    /// 先 <c>FilterRuleSet.FromJson</c> →（<c>IsEnabled &amp;&amp; !IsEffectivelyEmpty</c> 时）
    /// <c>FzLibFileFilter</c>，再包一层 <see cref="HardExcludeFileFilter"/>；
    /// 枚举时用 <c>filter.DescendInto</c> 剪枝、对每个条目问 <c>IncludeFile</c>。
    /// </para>
    /// <para>
    /// <b>为什么自己 new 一个 <see cref="PhysicalFileSystem"/></b>：
    /// "跳过的目录"清单（重解析点 / 无权限）在实现里是一个**单例**上的共享队列，
    /// 唯一的读法是"取出并清空"（<c>DrainSkippedDirectories</c>）。如果借宿主的那个实例，
    /// 就会把同时运行的备份留下的记录一起取走，导致备份日志里"跳过了 N 个目录"的提示消失——
    /// 那正是设计文档 03 §7 要求必须让用户看见的东西。所以这里用一个只属于自己的实例，
    /// 既拿得到准确的跳过数，又不会动到别人的记录。
    /// </para>
    /// <para>
    /// 硬排除里**只有数据目录**（<c>App_Data</c>）：这个请求不带 taskId，
    /// 拿不到"任务自己的备份目录"，所以无法把那条规则也算进来。
    /// 影响有限——校验流程本来就禁止备份目录位于源目录之内。
    /// </para>
    /// </remarks>
    [HttpPost("filter-test")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public FilterTestResultDto FilterTest([FromBody] FilterTestRequestDto input)
    {
        string sourceDir = RequireBrowsableDirectory(input.SourceDir);

        FilterRuleSet rules = FilterRuleSet.FromJson(input.FilterJson);

        IFileFilter inner = rules.IsEnabled && !rules.IsEffectivelyEmpty
            ? new FzLibFileFilter(rules, _normalizer)
            : NullFileFilter.Instance;

        var excluded = new List<string>();
        if (!string.IsNullOrWhiteSpace(_paths.DataDir))
        {
            excluded.Add(_paths.DataDir);
        }

        if (!string.IsNullOrWhiteSpace(_paths.KeysDir))
        {
            // 密钥环（含数据保护密钥）绝不该被当成普通文件筛进来：它能解密所有 Cookie。
            excluded.Add(_paths.KeysDir);
        }

        var filter = new HardExcludeFileFilter(inner, _normalizer, excluded);

        // 私有实例，只为拿到"本次试算跳过了哪些目录"。见 remarks。
        var scanner = new PhysicalFileSystem(_normalizer);

        long startedTicks = Environment.TickCount64;
        int fileCount = 0;
        long totalBytes = 0;
        int scanned = 0;
        bool truncated = false;
        var files = new List<FilterTestFileDto>(MaxListedFiles);

        foreach (FileEntry entry in scanner.EnumerateFiles(
                     sourceDir, FileEnumerationOptions.Recursive, filter.DescendInto))
        {
            if (++scanned > MaxScanEntries)
            {
                // 上限存在的意义：这个端点必须**昂贵得可预期**，否则它就是一个
                // "一次请求扫全盘"的廉价入口（06 §4.8 明确要求设上限）。
                truncated = true;
                break;
            }

            if (!filter.IncludeFile(entry.FullPath))
            {
                continue;
            }

            fileCount++;
            totalBytes += entry.Length;

            if (files.Count < MaxListedFiles)
            {
                string? relative = _normalizer.GetRelativePathOrNull(sourceDir, entry.FullPath);

                files.Add(new FilterTestFileDto(
                    relative is null ? entry.FullPath : relative.Replace('\\', '/'),
                    entry.Length,
                    entry.FileTimeUtc));
            }
        }

        IReadOnlyList<SkippedDirectory> skipped = scanner.DrainSkippedDirectories();

        if (skipped.Count > 0)
        {
            // 跳过目录必须让用户看见（03 §7）：他可能正因为"怎么少了一个目录"而来点这个按钮。
            _logger.LogInformation(
                "筛选试算 {Dir}：跳过 {Count} 个目录（重解析点/无权限），首个为 {First}。",
                sourceDir, skipped.Count, skipped[0].FullPath);
        }

        return new FilterTestResultDto
        {
            FileCount = fileCount,
            TotalBytes = totalBytes,
            Files = files,
            SkippedDirCount = skipped.Count,
            Truncated = truncated,
            ElapsedMs = Environment.TickCount64 - startedTicks,
        };
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 把入参归一化成"可以安全枚举的绝对目录路径"，或抛出明确错误。
    /// </summary>
    private string RequireBrowsableDirectory(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw ApiException.BadRequest(ApiErrorCodes.PathNotAbsolute, "必须提供绝对目录路径。");
        }

        string path = raw.Trim();

        // 先查穿越片段再查是否绝对：`D:\a\..\..\Windows` 是绝对的，
        // 但它表达的位置与字面不符，必须在归一化之前就挡掉（05 §8）。
        if (DirectoryRelationshipValidator.ContainsRelativeSegment(path))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.PathContainsRelativeSegment,
                "路径中包含 '.' 或 '..' 片段，已被拒绝。");
        }

        if (!_normalizer.IsAbsolute(path))
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.PathNotAbsolute,
                $"路径必须是绝对路径：{path}");
        }

        string dir = _normalizer.TrimTrailingSeparator(path);

        if (!_fs.DirectoryExists(dir))
        {
            throw ApiException.NotFound(ApiErrorCodes.PathNotFound, $"目录不存在或不可访问：{dir}");
        }

        // 重解析点（目录符号链接 / junction / 挂载点）一律不进入。
        // 这是本控制器最重要的一条防线：跟随链接就等于让"浏览目录"变成
        // "跟着一个预先摆好的链接去读任意位置"，而链接可以由任何本地进程创建。
        if (_fs.GetDirectory(dir) is { } info &&
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.PathIsReparsePoint,
                "该目录是符号链接 / junction（重解析点），已拒绝对它进行浏览或试算。",
                "请改选它的真实路径。");
        }

        return dir;
    }

    /// <summary>驱动器 + 已配置的根（去重由调用方做）。</summary>
    private async Task<List<string>> CollectConfiguredRootsAsync()
    {
        var result = new List<string>();

        List<string> restoreRoots = await _settings.GetAllowedRestoreRootsAsync(Ct).ConfigureAwait(false);
        result.AddRange(restoreRoots);

        List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, Ct).ConfigureAwait(false);
        result.AddRange(all.Select(t => t.BackupDir));

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            result.Add(home);
        }

        return [.. result.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim())];
    }

    /// <summary>
    /// 探测某个根是否可用。
    /// </summary>
    /// <remarks>
    /// 探测失败一律报"不可用"：网络盘断开时 <c>Directory.Exists</c> 可能要等 SMB 超时，
    /// 但那也比显示成"可用"、用户点进去再卡住要好。异常照常吞掉——
    /// 一个根探测失败不该让整个根列表 500。
    /// </remarks>
    private bool ProbeReady(string rootPath)
    {
        try
        {
            return _fs.DirectoryExists(rootPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _logger.LogDebug(ex, "探测根 {Root} 是否可用失败，按不可用上报。", rootPath);
            return false;
        }
    }
}
