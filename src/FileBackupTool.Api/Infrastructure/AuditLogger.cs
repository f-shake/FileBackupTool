using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 审计事件类型 ↔ 稳定字符串的**唯一**映射。
/// </summary>
/// <remarks>
/// 为什么不让枚举名直接序列化（<c>AuditEventType.LoginSucceeded.ToString()</c>）：
/// 前端契约要求 <c>eventType</c> 是稳定字符串（如 <c>LOGIN_SUCCESS</c> / <c>SNAPSHOT_DELETE</c>），
/// 并且 <c>GET /api/audit?eventType=</c> 要能按它筛选。
/// 一旦把 C# 枚举名当契约，将来重命名枚举成员就会**静默**破坏筛选与前端本地化。
/// 所以这里显式列一张表，改名必须动这张表，改不动就是编译不过。
/// </remarks>
public static class AuditEventNames
{
    private static readonly Dictionary<AuditEventType, string> ToName = new()
    {
        [AuditEventType.LoginSucceeded] = "LOGIN_SUCCESS",
        [AuditEventType.LoginFailed] = "LOGIN_FAILED",
        [AuditEventType.TwoFactorSucceeded] = "TOTP_SUCCESS",
        [AuditEventType.TwoFactorFailed] = "TOTP_FAILED",
        [AuditEventType.SessionCreated] = "SESSION_CREATED",
        [AuditEventType.SessionRevoked] = "SESSION_REVOKED",
        [AuditEventType.PasswordChanged] = "PASSWORD_CHANGED",
        [AuditEventType.TwoFactorRebound] = "TOTP_REBOUND",
        [AuditEventType.RecoveryCodeUsed] = "RECOVERY_CODE_USED",
        [AuditEventType.RecoveryCodesRegenerated] = "RECOVERY_CODES_REGENERATED",
        [AuditEventType.TaskCreated] = "TASK_CREATED",
        [AuditEventType.TaskUpdated] = "TASK_UPDATED",
        [AuditEventType.TaskDeleted] = "TASK_DELETED",
        [AuditEventType.SnapshotDeleted] = "SNAPSHOT_DELETE",
        [AuditEventType.RestoreExecuted] = "RESTORE_EXECUTE",
        [AuditEventType.RecycleBinCleared] = "RECYCLE_BIN_CLEAR",
        [AuditEventType.SettingsChanged] = "SETTINGS_CHANGED",
    };

    private static readonly Dictionary<string, AuditEventType> FromName =
        ToName.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);

    public static string Name(AuditEventType type) =>
        ToName.TryGetValue(type, out string? name) ? name : type.ToString().ToUpperInvariant();

    /// <summary>解析 <c>?eventType=</c>。无法识别时返回 null（调用方当"全部"处理）。</summary>
    public static AuditEventType? Parse(string? value) =>
        !string.IsNullOrWhiteSpace(value) && FromName.TryGetValue(value.Trim(), out AuditEventType type)
            ? type
            : null;

    /// <summary>供前端做筛选下拉用的全部取值。</summary>
    public static IReadOnlyList<string> All { get; } = [.. ToName.Values];
}

/// <summary>
/// 写 <c>AuditLog</c>（设计文档 05 §5、02 §2.5）。
/// </summary>
/// <remarks>
/// <para>
/// 设计文档 05 §5 的表格逐行对应到这里的调用点：登录成功/失败、TOTP 校验成功/失败、
/// 会话创建/踢出、密码修改/TOTP 重绑/恢复码使用、任务配置变更、快照删除、
/// 还原执行、回收站清空。**审计日志不参与快照删除的级联，也不自动清理**。
/// </para>
/// <para>
/// 写审计**失败绝不能影响业务操作**：一次写库失败不该让用户的登录失败。
/// 所以这里吞掉异常，只在 Serilog 里留痕（与 <c>BackupLogSink</c> 同一立场）。
/// 但要注意：吞掉写失败意味着"审计缺失"，所以留的痕迹是 Warning 而不是 Debug。
/// </para>
/// </remarks>
public sealed class AuditLogger(
    LogStore store,
    IClock clock,
    IHttpContextAccessor httpContextAccessor,
    ILogger<AuditLogger> logger)
{
    private readonly LogStore _store = store;
    private readonly IClock _clock = clock;
    private readonly IHttpContextAccessor _httpContextAccessor = httpContextAccessor;
    private readonly ILogger<AuditLogger> _logger = logger;

    public Task WriteAsync(
        AuditEventType type,
        string? target = null,
        string? detail = null,
        string? actor = null,
        CancellationToken ct = default)
    {
        HttpContext? http = _httpContextAccessor.HttpContext;

        return WriteCoreAsync(new AuditLog
        {
            Time = _clock.UtcNow,
            EventType = type,
            // 单用户系统里操作者就是用户名；系统自动行为（调度、自愈、保留策略）记 "system"。
            Actor = actor ?? http?.User?.Identity?.Name ?? "system",
            // IP 必须是 ForwardedHeaders 处理之后的 Connection.RemoteIpAddress，
            // 否则 nginx 后面看到的永远是 127.0.0.1，审计表就完全失去价值（05 §4 / §6）。
            SourceIp = http?.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Truncate(http?.Request.Headers.UserAgent.ToString(), 512),
            Target = Truncate(target, 512),
            Detail = detail,
        }, ct);
    }

    /// <summary>系统自动行为（调度触发、启动自愈、保留策略淘汰）用这个重载。</summary>
    public Task WriteSystemAsync(
        AuditEventType type,
        string? target = null,
        string? detail = null,
        CancellationToken ct = default)
        => WriteCoreAsync(new AuditLog
        {
            Time = _clock.UtcNow,
            EventType = type,
            Actor = "system",
            Target = Truncate(target, 512),
            Detail = detail,
        }, ct);

    private async Task WriteCoreAsync(AuditLog entry, CancellationToken ct)
    {
        try
        {
            await _store.WriteAuditAsync(entry, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "写入审计日志失败（事件 {Event}），该事件在审计表中缺失。", entry.EventType);
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
