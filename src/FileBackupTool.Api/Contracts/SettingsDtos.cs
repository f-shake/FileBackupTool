namespace FileBackupTool.Api.Contracts;

/// <summary><c>GET</c> / <c>PUT /api/settings</c>：全局设置（06 §4.9 / 04 §2.3）。</summary>
public sealed record AppSettingsDto
{
    /// <summary>自动备份总开关。关闭后所有定时与监控触发的备份都被暂停。</summary>
    public bool AutoBackupEnabled { get; init; } = true;

    /// <summary>还原根白名单（**硬约束**，还原绝不接受白名单之外的路径）。</summary>
    public IReadOnlyList<string> AllowedRestoreRoots { get; init; } = [];

    public required RetentionSettingsDto Retention { get; init; }

    /// <summary>"完整校验"默认是否重算哈希（默认 false，TB 级极慢）。</summary>
    public bool VerifyDeepHashByDefault { get; init; }

    /// <summary>失败率阈值（百分比），超过则告警。</summary>
    public double FailureRateThresholdPercent { get; init; } = 10;

    /// <summary>
    /// 备份读源文件时是否申请低 IO 优先级（Windows 的 <c>IoPriorityHintVeryLow</c>）。
    /// </summary>
    /// <remarks>
    /// 默认开启。它只在**争用**时生效：用户不往同一块盘写东西时，备份照样跑满带宽。
    /// 关掉它的典型场景是"这台机器上该能力表现异常"，或需要排查吞吐问题时做对照。
    /// </remarks>
    public bool LowPriorityIoEnabled { get; init; } = true;
}

/// <summary>保留策略（04 §2.3）。**默认关闭**。</summary>
public sealed record RetentionSettingsDto
{
    /// <summary>
    /// 默认关闭。开启后才会自动淘汰快照链组。
    /// "二次确认"由 UI 承担（设计文档 04 §2.3 要求明确告知"这会永久删除备份历史"）；
    /// 服务端只认这一个显式开关，不做任何默认开启的推断。
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Count / Days / Size。</summary>
    public string Mode { get; init; } = "Count";

    public long Value { get; init; } = 10;

    /// <summary>只对完整的全量链组生效。</summary>
    public bool ApplyToFullChains { get; init; } = true;
}

/// <summary><c>GET /api/settings/info</c>：只读信息。</summary>
public sealed record ServerInfoDto
{
    public required string Version { get; init; }

    public required string DataDir { get; init; }

    public required string LogDir { get; init; }

    public required string DatabasePath { get; init; }

    public long DatabaseBytes { get; init; }

    /// <summary>blob 池占用。</summary>
    public long BlobPoolBytes { get; init; }

    /// <summary>回收站占用（**空间未释放，必须可见**）。</summary>
    public long RecycleBinBytes { get; init; }

    public long DiskFreeBytes { get; init; }

    public long DiskTotalBytes { get; init; }

    public required DateTime StartedAt { get; init; }

    public required DateTime ServerTime { get; init; }

    /// <summary>迁移 / 健康状态提示。</summary>
    public IReadOnlyList<string> Notices { get; init; } = [];
}
