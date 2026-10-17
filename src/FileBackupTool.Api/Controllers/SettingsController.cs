using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Api.Services;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 全局设置（设计文档 06 §4.9、04 §2.3）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这里的校验比别处严</b>：这个界面能改的东西里，有两项是**安全边界本身**——
/// 还原根白名单（还原是写文件操作，白名单是最重要的缓解）与自动备份总开关
/// （关掉它等于"再也不备份"，而用户不会立刻发现）。所以两条路径都不允许"差不多就行"：
/// 白名单里的每个根都要过一遍目录关系校验，保留策略的每个字段都要落到合法范围。
/// </para>
/// <para>
/// <b>保存即整体替换</b>：PUT 的语义是"这是完整的设置"，不做字段级合并。
/// 与前端契约一致（<c>updateSettings(settings)</c> 提交的就是 GET 回来的那个对象改几个字段）。
/// </para>
/// </remarks>
[Route("api/settings")]
public sealed class SettingsController(
    SettingsService settings,
    ServerInfoService serverInfo,
    DirectoryRelationshipValidator validator,
    TaskStore tasks,
    IPathNormalizer normalizer,
    AppPaths paths,
    AuditLogger audit,
    ILogger<SettingsController> logger) : ApiControllerBase
{
    private readonly SettingsService _settings = settings;
    private readonly ServerInfoService _serverInfo = serverInfo;
    private readonly DirectoryRelationshipValidator _validator = validator;
    private readonly TaskStore _tasks = tasks;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly AppPaths _paths = paths;
    private readonly AuditLogger _audit = audit;
    private readonly ILogger<SettingsController> _logger = logger;

    /// <summary>读全局设置。</summary>
    [HttpGet]
    public Task<AppSettingsDto> GetAsync() => _settings.GetAppSettingsAsync(Ct);

    /// <summary>保存全局设置。**整体替换**，保存前逐项校验。</summary>
    [HttpPut]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<AppSettingsDto> UpdateAsync([FromBody] AppSettingsDto input)
    {
        AppSettingsDto before = await _settings.GetAppSettingsAsync(Ct).ConfigureAwait(false);

        List<string> roots = NormalizeRoots(input.AllowedRestoreRoots);
        ValidateRetention(input.Retention);
        ValidateFailureThreshold(input.FailureRateThresholdPercent);
        await ValidateRestoreRootsAsync(roots).ConfigureAwait(false);

        var normalized = input with
        {
            AllowedRestoreRoots = roots,
            Retention = input.Retention with
            {
                Mode = SettingsService.NormalizeMode(input.Retention.Mode),
            },
        };

        await _settings.SaveAppSettingsAsync(normalized, Ct).ConfigureAwait(false);

        // 审计要记"变更前后"：设置里混着安全边界（白名单、自动备份开关），
        // 出了问题第一个要回答的就是"这个值是什么时候、被改成什么的"。
        await _audit.WriteAsync(
            AuditEventType.SettingsChanged,
            target: "全局设置",
            detail: DescribeChange(before, normalized),
            ct: Ct).ConfigureAwait(false);

        _logger.LogInformation("全局设置已更新：{Change}", DescribeChange(before, normalized));

        // 回读一次而不是把入参原样返回：读回来的才是真正落库的值（比如 mode 被归一化过）。
        return await _settings.GetAppSettingsAsync(Ct).ConfigureAwait(false);
    }

    /// <summary>版本、数据目录、磁盘占用等只读信息。</summary>
    [HttpGet("info")]
    public Task<ServerInfoDto> InfoAsync() => _serverInfo.GetAsync(Ct);

    // ------------------------------------------------------------------

    /// <summary>
    /// 归一化还原根白名单：去空白、去尾部分隔符、去重（按平台语义）。
    /// </summary>
    /// <remarks>
    /// 空项**直接丢弃**而不是报错：前端那个多行文本框在用户回车换行时很容易留下一个空行，
    /// 为一个空行报错会让人以为是格式问题而反复折腾。真正危险的是非法路径，那些在后面必查。
    /// </remarks>
    private List<string> NormalizeRoots(IReadOnlyList<string>? raw)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(_normalizer.Comparer);

        foreach (string item in raw ?? [])
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string root = item.Trim();
            if (seen.Add(root))
            {
                result.Add(root);
            }
        }

        return result;
    }

    private void ValidateRetention(RetentionSettingsDto? retention)
    {
        if (retention is null)
        {
            throw ApiException.BadRequest(ApiErrorCodes.SettingsInvalid, "缺少 retention 设置。");
        }

        string mode = retention.Mode?.Trim() ?? string.Empty;

        if (!mode.Equals("Count", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Days", StringComparison.OrdinalIgnoreCase) &&
            !mode.Equals("Size", StringComparison.OrdinalIgnoreCase))
        {
            // 不静默归一化成 Count：那会让"我明明选了按天数"变成"按个数淘汰"，
            // 而淘汰是**不可逆的删除**——静默改变量纲在这里是不可接受的。
            throw ApiException.BadRequest(
                ApiErrorCodes.SettingsInvalid,
                $"保留策略的 mode「{retention.Mode}」无法识别，可选：Count / Days / Size。");
        }

        if (SettingsService.NormalizeMode(mode) == "Size")
        {
            // 0 表示"不限制"，之外必须是正数。
            if (retention.Value < 0)
            {
                throw ApiException.BadRequest(ApiErrorCodes.SettingsInvalid, "空间上限不能为负数（0 表示不限制）。");
            }

            return;
        }

        if (retention.Value < 1)
        {
            // Count/Days 为 0 等价于"下一次保留策略运行就删光所有历史"。
            // 用户想关掉自动淘汰应该去关 enabled，而不是把阈值设成 0。
            throw ApiException.BadRequest(
                ApiErrorCodes.SettingsInvalid,
                SettingsService.NormalizeMode(mode) == "Days"
                    ? "保留天数必须大于 0。要停止自动淘汰，请关闭「启用保留策略」。"
                    : "保留个数必须大于 0。要停止自动淘汰，请关闭「启用保留策略」。");
        }
    }

    private static void ValidateFailureThreshold(double percent)
    {
        if (double.IsNaN(percent) || percent is < 0 or > 100)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.SettingsInvalid,
                "失败率阈值必须在 0 到 100 之间。");
        }
    }

    /// <summary>
    /// 逐个校验还原根：格式合法 + 不与任何任务的备份目录互相包含。
    /// </summary>
    private async Task ValidateRestoreRootsAsync(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
        {
            // 允许清空白名单（还原功能随之不可用，RestoreController 会明确拒绝并告知原因），
            // 但要留一条 Warning：这等于关掉了唯一一条"能被攻破的会话写文件"的边界。
            _logger.LogWarning("还原根白名单已被清空，还原功能将不可用。");
            return;
        }

        List<BackupTask> all = await _tasks.GetAllAsync(includeDeleted: false, Ct).ConfigureAwait(false);

        var issues = new List<DirectoryValidationIssue>();

        foreach (string root in roots)
        {
            issues.AddRange(_validator.ValidateRestoreRoot(root, all));

            // 顺带查一下白名单与数据目录的关系：还原到 App_Data 会把数据库/密钥环
            // 当成普通文件覆盖掉（设计文档 01 §7）。
            if (_normalizer.IsSameOrUnder(root, _paths.DataDir))
            {
                throw ApiException.BadRequest(
                    ApiErrorCodes.SettingsInvalid,
                    $"还原根 {root} 位于数据目录 {_paths.DataDir} 之内，已被拒绝。",
                    "数据目录里有数据库与数据保护密钥环（能解密全部 Cookie），作为还原目标会让一次还原毁掉整个服务。");
            }
        }

        if (issues.Count > 0)
        {
            // 与任务表单报同样的稳定 code（RESTORE_ROOT_INSIDE_BACKUP 等），
            // 前端可以复用同一套高亮逻辑。
            throw DirectoryValidationFailedException.From(issues);
        }
    }

    /// <summary>把"改了什么"写成一行人话，供审计与日志使用。</summary>
    private static string DescribeChange(AppSettingsDto before, AppSettingsDto after)
    {
        var parts = new List<string>();

        if (before.AutoBackupEnabled != after.AutoBackupEnabled)
        {
            parts.Add($"自动备份开关：{before.AutoBackupEnabled} → {after.AutoBackupEnabled}");
        }

        if (!before.AllowedRestoreRoots.SequenceEqual(after.AllowedRestoreRoots, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add($"还原根白名单：{Join(before.AllowedRestoreRoots)} → {Join(after.AllowedRestoreRoots)}");
        }

        if (before.VerifyDeepHashByDefault != after.VerifyDeepHashByDefault)
        {
            parts.Add($"默认深度校验：{before.VerifyDeepHashByDefault} → {after.VerifyDeepHashByDefault}");
        }

        if (before.LowPriorityIoEnabled != after.LowPriorityIoEnabled)
        {
            parts.Add($"备份让路（低 IO 优先级）：{before.LowPriorityIoEnabled} → {after.LowPriorityIoEnabled}");
        }

        if (Math.Abs(before.FailureRateThresholdPercent - after.FailureRateThresholdPercent) > double.Epsilon)
        {
            parts.Add($"失败率阈值：{before.FailureRateThresholdPercent}% → {after.FailureRateThresholdPercent}%");
        }

        if (before.Retention != after.Retention)
        {
            parts.Add(
                $"保留策略：{Describe(before.Retention)} → {Describe(after.Retention)}");
        }

        return parts.Count == 0 ? "（无实质变化）" : string.Join("；", parts);

        static string Describe(RetentionSettingsDto r) =>
            $"{(r.Enabled ? "启用" : "关闭")}/{r.Mode}/{r.Value}/只对全量链 {(r.ApplyToFullChains ? "是" : "否")}";

        static string Join(IReadOnlyList<string> items) =>
            items.Count == 0 ? "(空)" : string.Join(", ", items);
    }
}
