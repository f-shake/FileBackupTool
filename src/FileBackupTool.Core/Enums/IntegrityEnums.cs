namespace FileBackupTool.Core.Enums;

/// <summary>完整性校验中发现的异常类型。设计文档 03 §8。</summary>
public enum IntegrityIssueType
{
    /// <summary>blob 池里存在但没有记录引用（正常应被回收）。</summary>
    RedundantBlob = 0,

    /// <summary>有记录引用但 blob 既不在池中也不在回收站中。**会导致还原失败，必须显著告警**。</summary>
    LostBlob = 1,

    /// <summary>快照链断裂（某条增量找不到它所属的全量快照）。</summary>
    BrokenChain = 2,

    /// <summary>blob 哈希校验失败（仅在深度校验开启时产生）。</summary>
    CorruptedBlob = 3,

    /// <summary>重放镜像时遇到结构性不一致（Created 撞已存在、Modified/Deleted 找不到目标）。旧设计用 <c>Debug.Assert(false)</c> 处理，Release 下被编译掉。</summary>
    MirrorInconsistency = 4,
}

/// <summary>审计事件类型。设计文档 05 §5。</summary>
public enum AuditEventType
{
    LoginSucceeded = 0,
    LoginFailed = 1,
    TwoFactorSucceeded = 2,
    TwoFactorFailed = 3,
    SessionCreated = 4,
    SessionRevoked = 5,
    PasswordChanged = 6,
    TwoFactorRebound = 7,
    RecoveryCodeUsed = 8,
    RecoveryCodesRegenerated = 9,
    TaskCreated = 10,
    TaskUpdated = 11,
    TaskDeleted = 12,
    SnapshotDeleted = 13,
    RestoreExecuted = 14,
    RecycleBinCleared = 15,
    SettingsChanged = 16,
}
