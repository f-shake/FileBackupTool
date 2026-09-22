namespace FileBackupTool.Core.Configuration;

/// <summary>变更监控参数。对应 appsettings.json 的 <c>Monitoring</c> 节（设计文档 01 §5.1）。</summary>
public sealed class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    /// <summary>周期性全量扫描兜底间隔，小时。局部扫描**发现不了监控器漏掉的事件**，这是必经的兜底。</summary>
    public double FullScanFallbackHours { get; set; } = 6;

    /// <summary>部分写入防护的静默期，秒。设计文档 03 §5.2。</summary>
    public double SettleWindowSeconds { get; set; } = 10;

    /// <summary>变更合并窗口，秒。一次增量备份要开事务、扫源目录、写日志，单文件高频触发会把 IO 打满。</summary>
    public double CoalesceWindowSeconds { get; set; } = 45;

    /// <summary>局部扫描的事件防抖，秒。</summary>
    public double LocalScanDebounceSeconds { get; set; } = 5;

    /// <summary>UNC / 网络路径的轮询间隔，分钟。**放弃 watcher**，只跑轮询。</summary>
    public double NetworkPollIntervalMinutes { get; set; } = 3;

    /// <summary>脏目录占比超过此值时，局部扫描退化为全量扫描。</summary>
    public double LocalScanDegradeRatio { get; set; } = 0.30;

    /// <summary>事件队列容量。</summary>
    public int EventQueueCapacity { get; set; } = 10000;

    /// <summary>队列反压导致的丢弃计数超过此值时，触发一次全量扫描（钩子 ②）。</summary>
    public int QueueBackpressureThreshold { get; set; } = 1;
}

/// <summary>作业队列参数。</summary>
public sealed class JobOptions
{
    public const string SectionName = "Jobs";

    /// <summary>
    /// 全局并发度。**固定为 1**——设计文档 01 §4.1：源与目标都在机械硬盘上，
    /// 并发只会因寻道抖动互相拖慢；保留字段以备将来多盘/SSD。
    /// </summary>
    public int GlobalConcurrency { get; set; } = 1;

    /// <summary>数据库分批提交大小。**不能**把整个快照的记录攒在 EF change tracker 里。</summary>
    public int DbBatchSize { get; set; } = 2000;

    /// <summary>逐文件失败率超过此值则中止整个备份。</summary>
    public double FailureRatioThreshold { get; set; } = 0.10;

    /// <summary>
    /// 失败率判定的最小样本数。处理过的文件数低于此值时不看失败率。
    /// </summary>
    /// <remarks>
    /// 对设计文档 03 §7.1 的一处补充。文档只说了"失败率超过阈值 → 中止"，
    /// 严格照做的话，3 个文件的备份里坏 1 个就是 33% &gt; 10% → 整次中止。
    /// 而失败率这个指标存在的意义是识别**系统性**故障（盘要坏了、权限整片没了），
    /// 在样本数很小的时候它只是噪声。所以设一个下限，低于它只看"连续失败次数"。
    /// </remarks>
    public int FailureRatioMinSamples { get; set; } = 20;

    /// <summary>连续失败次数达到此值则中止整个备份。</summary>
    public int ConsecutiveFailureThreshold { get; set; } = 100;

    /// <summary>复制缓冲大小。</summary>
    public int CopyBufferSizeBytes { get; set; } = Streams.StreamHelper.DefaultBufferSize;

    /// <summary>对 <c>IOException</c> 的重试次数（覆盖"文件被独占锁定"）。</summary>
    public int IoRetryCount { get; set; } = 3;

    /// <summary>重试的基础退避时长（指数退避的底数）。</summary>
    public double IoRetryBaseDelaySeconds { get; set; } = 0.5;
}

/// <summary>存储参数。</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>文件总数低于此值时，抢救清单退回单文件（分片在几千文件以下是纯负担）。</summary>
    public int ManifestShardThreshold { get; set; } = 10000;

    /// <summary>
    /// 单个清单分片包含的最大条目数。超过则按二级子目录再分一层。
    /// </summary>
    public int ManifestShardMaxEntries { get; set; } = 20000;

    /// <summary>
    /// 数据目录（绝对路径）。用于把 <c>App_Data</c> 排除在源目录扫描之外——
    /// SQLite 的 <c>-wal</c> / <c>-shm</c> 边车文件会产生高频写入，
    /// 落进源目录范围时会变成海量噪声事件（设计文档 02 §8）。
    /// </summary>
    public string? DataDirectory { get; set; }
}

/// <summary>安全参数。对应 appsettings.json 的 <c>Security</c> 节。</summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>强制 TOTP。未绑定前不能访问任何业务功能。</summary>
    public bool RequireConfirmedTwoFactor { get; set; } = true;

    public int CookieSlidingDays { get; set; } = 7;

    public int CookieAbsoluteDays { get; set; } = 30;

    /// <summary>还原根目录白名单。还原是写文件操作，这是最重要的缓解手段。</summary>
    public List<string> AllowedRestoreRoots { get; set; } = [];

    /// <summary>创建时是否自动执行数据库迁移。</summary>
    public bool AutoMigrate { get; set; } = true;

    /// <summary>初始账号名（密码必须由环境变量提供，不写死在文件里）。</summary>
    public string SeedUserName { get; set; } = "admin";

    /// <summary>提供初始密码的环境变量名。</summary>
    public string SeedPasswordEnvironmentVariable { get; set; } = "FILEBACKUPTOOL_ADMIN_PASSWORD";
}

/// <summary>反向代理参数。设计文档 05 §6。</summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// 只有这些 IP 写入的 <c>X-Forwarded-*</c> 才被采信。
    /// `X-Forwarded-For` 可被客户端伪造，信任所有来源等于限流形同虚设。
    /// </summary>
    public List<string> TrustedProxies { get; set; } = ["127.0.0.1", "::1"];

    /// <summary>是否启用 HSTS。</summary>
    public bool UseHsts { get; set; } = true;
}

/// <summary>保留策略（设计文档 04 §2.3：默认全手动，自动淘汰是显式开关）。</summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    /// <summary>**默认关闭**。自动删除用户的备份历史是不可逆的惊喜，不该是默认行为。</summary>
    public bool AutoEvictionEnabled { get; set; }

    /// <summary>最多保留多少个快照链组。</summary>
    public int MaxSnapshotGroups { get; set; } = 10;

    /// <summary>最多保留多少天。</summary>
    public int MaxAgeDays { get; set; } = 180;

    /// <summary>占用空间上限（GB）。0 表示不限制。</summary>
    public long MaxTotalBytesGb { get; set; }
}
