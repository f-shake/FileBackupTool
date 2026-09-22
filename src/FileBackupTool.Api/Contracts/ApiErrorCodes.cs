namespace FileBackupTool.Api.Contracts;

/// <summary>
/// 统一错误响应体。设计文档 06 §4 / 08 §4.1：<c>{ code, message, detail? }</c>。
/// </summary>
/// <remarks>
/// <c>code</c> 是**稳定字符串**，前端据此做本地化，因此它一旦发布就不能随便改。
/// 取值集中在 <see cref="ApiErrorCodes"/> 里，避免散落到各处拼字符串。
/// </remarks>
public sealed record ApiErrorBody(string Code, string Message, string? Detail = null);

/// <summary>
/// 稳定的错误码。
/// </summary>
/// <remarks>
/// 分两类：
/// <list type="number">
/// <item><b>通用</b>——HTTP 语义级（401/403/404/409/429/500）。</item>
/// <item><b>业务</b>——前端要针对性处理的（目录冲突、路径非法、TOTP 未绑定…）。</item>
/// </list>
/// 目录关系校验那一组与 <c>DirectoryRelationshipValidator</c> 里的常量一一对应
/// （直接复用它的值，不重新定义），保证"校验器说什么、API 就说什么"。
/// </remarks>
public static class ApiErrorCodes
{
    // ---- 通用 ----
    public const string Unauthorized = "UNAUTHORIZED";
    public const string Forbidden = "FORBIDDEN";
    public const string NotFound = "NOT_FOUND";
    public const string Conflict = "CONFLICT";
    public const string RateLimited = "RATE_LIMITED";
    public const string ServerError = "SERVER_ERROR";
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string ValidationFailed = "VALIDATION_FAILED";
    public const string AntiforgeryFailed = "ANTIFORGERY_FAILED";

    // ---- 认证 / 会话 ----
    public const string TotpRequired = "TOTP_REQUIRED";
    public const string TotpNotBound = "TOTP_NOT_BOUND";
    public const string TotpCodeInvalid = "TOTP_CODE_INVALID";
    public const string LoginFailed = "LOGIN_FAILED";
    public const string AccountLocked = "ACCOUNT_LOCKED";
    public const string PasswordPolicyViolation = "PASSWORD_POLICY_VIOLATION";
    public const string SeedUserNotConfigured = "SEED_USER_NOT_CONFIGURED";
    public const string SessionNotFound = "SESSION_NOT_FOUND";
    public const string SessionRevoked = "SESSION_REVOKED";

    /// <summary>
    /// 会话登记表**落库失败**——踢出已经在内存里生效，但它不会活过重启。
    /// </summary>
    /// <remarks>
    /// 需要与普通冲突分开：调用方据此告诉用户"变更**已经发生**，只是没持久化"。
    /// 用一句含糊的 409 会让用户以为没踢掉，从而反复重试或以为处理完了。
    /// </remarks>
    public const string SessionPersistFailed = "SESSION_PERSIST_FAILED";

    // ---- 任务 / 目录 ----
    public const string TaskNotFound = "TASK_NOT_FOUND";
    public const string TaskDirConflict = "TASK_DIR_CONFLICT";
    public const string TaskNameRequired = "TASK_NAME_REQUIRED";
    public const string TaskDisabled = "TASK_DISABLED";

    // ---- 快照 ----
    public const string SnapshotNotFound = "SNAPSHOT_NOT_FOUND";
    public const string SnapshotNotRestorable = "SNAPSHOT_NOT_RESTORABLE";

    // ---- 还原 ----
    public const string RestoreTargetNotAllowed = "RESTORE_TARGET_NOT_ALLOWED";
    public const string RestoreNoAllowedRoots = "RESTORE_NO_ALLOWED_ROOTS";
    public const string RestorePerFileStrategyUnsupported = "RESTORE_PER_FILE_STRATEGY_UNSUPPORTED";
    public const string RestoreTargetInvalid = "RESTORE_TARGET_INVALID";

    // ---- 作业 ----
    public const string JobNotFound = "JOB_NOT_FOUND";
    public const string JobQueueBusy = "JOB_QUEUE_BUSY";

    // ---- 文件系统浏览 ----
    public const string PathNotAbsolute = "PATH_NOT_ABSOLUTE";
    public const string PathContainsRelativeSegment = "PATH_CONTAINS_RELATIVE_SEGMENT";
    public const string PathNotFound = "PATH_NOT_FOUND";
    public const string PathIsReparsePoint = "PATH_IS_REPARSE_POINT";

    // ---- 日志 ----
    public const string LogNotFound = "LOG_NOT_FOUND";

    // ---- 设置 ----
    public const string SettingsInvalid = "SETTINGS_INVALID";

    // ---- 监控（模块由 Monitoring 项目提供）----
    public const string MonitoringUnavailable = "MONITORING_UNAVAILABLE";
    public const string MonitoringRootNotFound = "MONITORING_ROOT_NOT_FOUND";
}
