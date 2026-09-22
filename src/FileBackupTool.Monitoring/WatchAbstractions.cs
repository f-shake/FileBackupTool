namespace FileBackupTool.Monitoring;

/// <summary>
/// <c>FileSystemWatcher</c> 之上的**可替换抽象**。
/// </summary>
/// <remarks>
/// 设计文档 03 §5.6 要求"watcher 回调里不做任何 IO"，而设计文档 06 §2 要求单元测试能在
/// 假文件系统上驱动监控逻辑。<c>FileSystemWatcher</c> 是密封的平台类型、无法 mock，
/// 所以它必须留在接口后面——这是本抽象存在的**唯一**理由。
/// <para>
/// 接口刻意做得极薄：只暴露"事件 + 开关"，不暴露 <c>NotifyFilter</c> / <c>InternalBufferSize</c>
/// 这类平台细节——它们由实现自己决定，测试不需要也不应该关心。
/// </para>
/// </remarks>
public interface IFileWatcher : IDisposable
{
    /// <summary>变更事件。实现必须在**自己的线程**上同步触发（与 <c>FileSystemWatcher</c> 一致）。</summary>
    event EventHandler<WatcherEvent>? Changed;

    /// <summary>
    /// 监控器自身出错（缓冲区溢出等）。设计文档 03 §5.1 钩子 ①：
    /// GalleryCloud 在这里**只记一条日志**，于是"事件静默丢失"永远没人知道。
    /// </summary>
    event EventHandler<string>? Error;

    bool EnableRaisingEvents { get; set; }
}

/// <summary>创建 <see cref="IFileWatcher"/>。构造可能失败（钩子 ①），所以单独一个工厂。</summary>
public interface IFileWatcherFactory
{
    /// <summary>
    /// 建立一个根目录的监控器。
    /// </summary>
    /// <exception cref="Exception">
    /// 任何构造失败（目录不存在、不支持的文件系统、句柄耗尽）都必须抛出来——
    /// 调用方必须捕获它并排一次全量扫描（设计文档 03 §5.1 钩子 ①）。
    /// GalleryCloud 把这里的异常 <c>catch</c> 掉，等于完全没在监控。
    /// </exception>
    IFileWatcher Create(string rootFullPath, string filter, int internalBufferSize);
}

/// <summary>快速路径观察到的一种"变更"。</summary>
public enum WatcherChangeKind
{
    Created = 0,

    Modified = 1,

    Deleted = 2,

    /// <summary>
    /// 重命名。设计文档 03 §5.7：**不特殊处理**，表现为"旧路径消失 + 新路径出现"。
    /// 所以它会被展开成两个普通事件（旧路径按删除、新路径按新增）。
    /// </summary>
    Renamed = 3,

    /// <summary>
    /// 监控器自身出错（**不是**文件变更）。它会走同一条队列，因为队列是本模块唯一的汇聚点，
    /// 而"单消费者"是并发形状的硬约束（设计文档 03 §5.6）。
    /// </summary>
    Fault = 4,
}

/// <summary>一次 watcher 通知的原始内容。</summary>
public readonly record struct WatcherEvent(string FullPath, WatcherChangeKind Kind)
{
    /// <summary>
    /// 路径是否为目录。
    /// </summary>
    /// <remarks>
    /// <c>FileSystemWatcher</c> **不报告**这一点（事件里只有路径），所以真实实现留 false，
    /// 由消费者在自己的线程上用一次 <c>DirectoryExists</c> 补齐——回调里不做 IO 是硬约束。
    /// </remarks>
    public bool IsDirectory { get; init; }

    /// <summary><see cref="WatcherChangeKind.Fault"/> 的错误说明。</summary>
    public string? Detail { get; init; }
}
