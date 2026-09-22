using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 业务日志与审计日志（设计文档 06 §4.7、05 §5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这里直接查 <c>AppDbContext</c> 而不走 <c>LogStore</c></b>：<c>LogStore</c>
/// 只提供"写入"（引擎侧只需要写），没有任何读取方法。日志页要分页、要按级别/时间/关键词过滤，
/// 这些查询天然属于只读投影。选择在 API 层直接查库，而不是去改 Engine/Data 的公共契约
/// ——修改 <c>LogStore</c> 会动到项目里最底层的共享类型（禁止改动范围）。
/// 代价是"日志查询逻辑不在 store 里"；这里用只读、无跟踪、纯 LINQ 的查询，风险可控。
/// </para>
/// <para>
/// <b>审计日志同样只读</b>：写入只能由 <c>AuditLogger</c> 完成，本控制器**没有任何写路径**。
/// 审计表按设计不参与级联删除、也不自动清理，所以这里只能由人来读。
/// </para>
/// </remarks>
[Route("api")]
public sealed class LogsController(
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<LogsController> logger) : ApiControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory = dbFactory;
    private readonly ILogger<LogsController> _logger = logger;

    /// <summary>业务日志列表。</summary>
    [HttpGet("logs")]
    public async Task<PagedDto<BackupLogEntryDto>> ListAsync(
        [FromQuery] Guid? taskId,
        [FromQuery] int? snapshotId,
        [FromQuery] string? level,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? keyword,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        (int p, int size) = PageQuery.Normalize(page, pageSize);
        (DateTime? fromUtc, DateTime? toUtc) = NormalizeRange(from, to);

        LogLevel? minLevel = ParseLevel(level);

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(Ct).ConfigureAwait(false);

        IQueryable<BackupLog> query = db.Logs.AsQueryable();

        if (taskId is { } tid)
        {
            query = query.Where(l => l.TaskId == tid);
        }

        if (snapshotId is { } sid)
        {
            query = query.Where(l => l.SnapshotId == sid);
        }

        if (minLevel is { } lv)
        {
            // **按级别过滤用的是"≥"而不是"=="**：用户选 Warning 的意思是"我要看值得注意的东西"，
            // 而 Error/Critical 显然比 Warning 更值得注意。用 == 会让他选 Warning 时看不到
            // 任何一条 Error——那是最容易被误读为"没有错误"的界面行为。
            query = query.Where(l => l.Level >= lv);
        }

        if (fromUtc is { } f)
        {
            query = query.Where(l => l.Time >= f);
        }

        if (toUtc is { } t)
        {
            query = query.Where(l => l.Time <= t);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // 只匹配 Message：Detail 可能很长（整段差异清单），把它拉进 LIKE 既慢又容易
            // 让用户看到一堆"消息里没有关键词"的行。
            string pattern = $"%{Escape(keyword.Trim())}%";
            query = query.Where(l => EF.Functions.Like(l.Message, pattern, "\\"));
        }

        long total = await query.LongCountAsync(Ct).ConfigureAwait(false);

        List<BackupLog> rows = await query
            .OrderByDescending(l => l.Time)
            // Id 作次序键：同一毫秒内写入的多条日志（一次备份会写很多条）必须有稳定顺序，
            // 否则翻页时会出现重复或漏行。
            .ThenByDescending(l => l.Id)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync(Ct).ConfigureAwait(false);

        return new PagedDto<BackupLogEntryDto>(
            [.. rows.Select(ToEntry)],
            total,
            p,
            size);
    }

    /// <summary>单条日志详情（含 <c>detail</c>）。</summary>
    /// <remarks>
    /// 列表刻意不带 <c>detail</c>：一次备份的失败日志里 <c>detail</c> 可能是几万字符的差异清单，
    /// 50 条一页就是 MB 级响应，而这个字段只有点开"详情"时才有人看。
    /// </remarks>
    [HttpGet("logs/{id:long}")]
    public async Task<BackupLogDetailDto> GetAsync([FromRoute] long id)
    {
        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(Ct).ConfigureAwait(false);

        BackupLog? log = await db.Logs.FirstOrDefaultAsync(l => l.Id == id, Ct).ConfigureAwait(false);

        if (log is null)
        {
            throw ApiException.NotFound(ApiErrorCodes.LogNotFound, $"日志 {id} 不存在。");
        }

        return new BackupLogDetailDto
        {
            Id = log.Id,
            TaskId = log.TaskId,
            SnapshotId = log.SnapshotId,
            Level = log.Level.ToString(),
            LevelValue = (int)log.Level,
            Time = log.Time,
            Message = log.Message,
            HasDetail = !string.IsNullOrEmpty(log.Detail),
            Detail = log.Detail,
        };
    }

    /// <summary>审计日志（按时间/类型筛选）。</summary>
    [HttpGet("audit")]
    public async Task<PagedDto<AuditLogEntryDto>> ListAuditAsync(
        [FromQuery] string? eventType,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] string? keyword,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        (int p, int size) = PageQuery.Normalize(page, pageSize);
        (DateTime? fromUtc, DateTime? toUtc) = NormalizeRange(from, to);

        AuditEventType? type = null;

        if (!string.IsNullOrWhiteSpace(eventType) &&
            !eventType.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            type = AuditEventNames.Parse(eventType)
                ?? throw ApiException.BadRequest(
                    ApiErrorCodes.InvalidRequest,
                    $"审计事件类型「{eventType}」无法识别。",
                    $"可选：{string.Join(" / ", AuditEventNames.All)}");
        }

        await using AppDbContext db = await _dbFactory.CreateDbContextAsync(Ct).ConfigureAwait(false);

        IQueryable<AuditLog> query = db.AuditLogs.AsQueryable();

        if (type is { } t)
        {
            query = query.Where(a => a.EventType == t);
        }

        if (fromUtc is { } f)
        {
            query = query.Where(a => a.Time >= f);
        }

        if (toUtc is { } tt)
        {
            query = query.Where(a => a.Time <= tt);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            // 审计的"关键词"是模糊的排查入口（"那次谁改了这个任务"），所以这里**跨四列**匹配：
            // 目标、明细、操作者、来源 IP。它与业务日志只搜 Message 的取向不同是有意的——
            // 审计表里"找 actor"和"找 IP"都是最常见的用法。
            string pattern = $"%{Escape(keyword.Trim())}%";
            query = query.Where(a =>
                EF.Functions.Like(a.Target ?? string.Empty, pattern, "\\") ||
                EF.Functions.Like(a.Detail ?? string.Empty, pattern, "\\") ||
                EF.Functions.Like(a.Actor ?? string.Empty, pattern, "\\") ||
                EF.Functions.Like(a.SourceIp ?? string.Empty, pattern, "\\"));
        }

        long total = await query.LongCountAsync(Ct).ConfigureAwait(false);

        List<AuditLog> rows = await query
            .OrderByDescending(a => a.Time)
            .ThenByDescending(a => a.Id)
            .Skip((p - 1) * size)
            .Take(size)
            .ToListAsync(Ct).ConfigureAwait(false);

        return new PagedDto<AuditLogEntryDto>(
            [
                .. rows.Select(a => new AuditLogEntryDto
                {
                    Id = a.Id,
                    Time = a.Time,

                    // 稳定字符串（AuditEventNames 是唯一映射表），不是枚举名。
                    EventType = AuditEventNames.Name(a.EventType),
                    Actor = a.Actor,
                    SourceIp = a.SourceIp,
                    UserAgent = a.UserAgent,
                    Target = a.Target,
                    Detail = a.Detail,
                }),
            ],
            total,
            p,
            size);
    }

    // ------------------------------------------------------------------

    private static BackupLogEntryDto ToEntry(BackupLog log) => new()
    {
        Id = log.Id,
        TaskId = log.TaskId,
        SnapshotId = log.SnapshotId,
        Level = log.Level.ToString(),
        LevelValue = (int)log.Level,
        Time = log.Time,
        Message = log.Message,
        HasDetail = !string.IsNullOrEmpty(log.Detail),
    };

    /// <summary>
    /// <c>?level=</c> → <see cref="LogLevel"/>。<c>All</c> / 空 = 不过滤；无法识别 → 400。
    /// </summary>
    private static LogLevel? ParseLevel(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Enum.TryParse(raw.Trim(), ignoreCase: true, out LogLevel level) && Enum.IsDefined(level)
            ? level

            // 不静默当"全部"：用户选了"Warning"却看到 Trace 级别的噪声，
            // 会以为界面坏了（或者更糟——以为级别筛选是装饰品）。
            : throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                $"日志级别「{raw}」无法识别。",
                "可选：Trace / Debug / Information / Warning / Error / Critical / None，或 All 表示不过滤。");
    }

    /// <summary>
    /// 时间范围 → UTC 闭区间。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>没有时区偏移的输入按本机时区解释</b>：前端的日期选择器用
    /// <c>value-format="YYYY-MM-DDTHH:mm:ss"</c>，发出的字符串**不带 Z 也不带偏移**
    /// （如 <c>2026-09-22T10:30:00</c>），那是用户在**本地时钟**上看到并选中的时刻。
    /// 单用户自托管场景下浏览器与宿主就在同一台机器上，所以按本机时区解释才是用户的本意。
    /// 带着 <c>Z</c> 或 <c>+08:00</c> 的输入由 <see cref="DateTimeOffset"/> 原样尊重。
    /// </para>
    /// <para>
    /// <b>结束时间取到当天的最后一刻</b>：如果 <c>to</c> 的时分秒都是 0（用户只挑了一天），
    /// 直接按 <c>00:00:00</c> 过滤会把这一整天全部排除在外——那是最典型的"我明明有日志却查不到"。
    /// </para>
    /// </remarks>
    private static (DateTime? FromUtc, DateTime? ToUtc) NormalizeRange(DateTimeOffset? from, DateTimeOffset? to)
    {
        DateTime? fromUtc = from?.UtcDateTime;

        DateTime? toUtc = to is { } t
            ? t.TimeOfDay == TimeSpan.Zero
                ? t.UtcDateTime.AddDays(1).AddTicks(-1)
                : t.UtcDateTime
            : null;

        if (fromUtc is { } f && toUtc is { } tt && f > tt)
        {
            throw ApiException.BadRequest(
                ApiErrorCodes.InvalidRequest,
                "时间范围的起点晚于终点。");
        }

        return (fromUtc, toUtc);
    }

    /// <summary>
    /// 转义 LIKE 的通配符。
    /// </summary>
    /// <remarks>
    /// 不转义的话，用户在关键词里打一个 <c>%</c> 就会匹配到**所有**日志——
    /// 看起来像是"筛选失效"，实际是 SQL 通配符。反斜杠本身也必须先转义，
    /// 否则 <c>\%</c> 这种输入会转义出错误的结果。
    /// </remarks>
    private static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
