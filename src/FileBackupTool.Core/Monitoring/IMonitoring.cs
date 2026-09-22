namespace FileBackupTool.Core.Monitoring;

/// <summary>一个源的监控模式。设计文档 03 §5.5。</summary>
public enum RootMonitorMode
{
    /// <summary>任务未启用监控。</summary>
    Disabled = 0,

    /// <summary>本地盘：<c>FileSystemWatcher</c>（快速路径）+ 扫描（权威路径）。</summary>
    Watcher = 1,

    /// <summary>
    /// UNC / NAS / 映射盘：**放弃 watcher，只跑轮询扫描**。
    /// </summary>
    /// <remarks>
    /// 理由：<c>FileSystemWatcher</c> 在 UNC/网络路径上**会静默失效**——
    /// 参考项目 GalleryCloud 就是这么被坑的：<c>WatchRoot</c> 把异常 catch 掉，
    /// 等于完全没在监控，唯一兜底是等第二天凌晨。
    /// 轮询间隔比那个 24 小时的兜底激进得多，因为备份没有"第二天再说"这种廉价容错。
    /// </remarks>
    Polling = 2,

    /// <summary>监控器构造失败或不可恢复的错误。**UI 必须显著显示**，不能静默。</summary>
    Failed = 3,
}

/// <summary>一个源的监控状态（供 <c>/api/monitoring</c>）。</summary>
public sealed record RootMonitorStatus(
    Guid TaskId,
    string TaskName,
    string SourceDir,
    RootMonitorMode Mode,
    string? Error,
    int PendingChangeCount,
    int DroppedEventCount,
    DateTime? LastEventUtc,
    DateTime? LastScanUtc,
    DateTime? LastFullScanUtc,
    DateTime? LastTriggerUtc,
    /// <summary>最近一次已排程的扫描范围：null = 全量；空 = 无待办。</summary>
    IReadOnlyList<string>? ScheduledScopes);

/// <summary>
/// 监控 → 备份的触发通道。由宿主（Api 层）实现，把请求投进全局作业队列。
/// </summary>
/// <remarks>
/// 监控器**不执行备份**，只产生"该根变脏"的信号并投递（设计文档 01 §4.3）。
/// </remarks>
public interface IBackupTrigger
{
    /// <param name="dirtyRelativePaths">
    /// 脏目录（源目录相对路径）。null 表示需要一次**全量扫描**。
    /// </param>
    Task RequestBackupAsync(
        Guid taskId,
        IReadOnlyList<string>? dirtyRelativePaths,
        string reason,
        CancellationToken ct);
}

/// <summary>变更监控。设计文档 03 §5。</summary>
public interface IChangeMonitor
{
    /// <summary>各根的当前状态。</summary>
    IReadOnlyList<RootMonitorStatus> GetStatuses();

    /// <summary>按当前任务配置（重新）建立监控。任务增删改后调用。</summary>
    Task ReloadAsync(CancellationToken ct);

    /// <summary>手动触发一次扫描（<c>POST /api/tasks/{id}/scan</c>）。</summary>
    Task<bool> RequestScanAsync(Guid taskId, bool fullScan, CancellationToken ct);
}
