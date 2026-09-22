namespace FileBackupTool.Monitoring;

/// <summary>
/// <see cref="IFileWatcher"/> 的真实实现：包一层 <c>FileSystemWatcher</c>。
/// </summary>
/// <remarks>
/// 这是整个模块里**唯一**允许直接使用 <c>FileSystemWatcher</c> 的地方——
/// 其余代码只认 <see cref="IFileWatcher"/>，因此单元测试可以完全绕开平台。
/// </remarks>
public sealed class PhysicalFileWatcherFactory : IFileWatcherFactory
{
    /// <summary>
    /// 内部缓冲区大小，64 KB（Windows 上的上限）。
    /// </summary>
    /// <remarks>
    /// 默认的 8 KB 在"一次拷进来一万个文件"时**极容易溢出**，而溢出的后果是事件被静默丢弃
    /// （只剩一个 <c>Error</c> 事件）。既然溢出是必然会发生的、且只能靠全量扫描兜底，
    /// 那就先把缓冲区开到最大、把溢出的频率压到最低。注意这只降低概率，**不能消除**它——
    /// 所以钩子 ① 仍然必须存在。
    /// </remarks>
    public const int DefaultInternalBufferSize = 64 * 1024;

    public IFileWatcher Create(string rootFullPath, string filter, int internalBufferSize) =>
        new PhysicalFileWatcher(rootFullPath, filter, internalBufferSize);
}

/// <summary>把 <c>FileSystemWatcher</c> 适配成 <see cref="IFileWatcher"/>。</summary>
internal sealed class PhysicalFileWatcher : IFileWatcher
{
    private readonly FileSystemWatcher _inner;
    private bool _disposed;

    public PhysicalFileWatcher(string rootFullPath, string filter, int internalBufferSize)
    {
        // 构造函数可能抛（目录不存在、路径非法、句柄耗尽）。**不要**在这里 catch——
        // 调用方需要知道"监控没建立起来"，并据此排一次全量扫描（设计文档 03 §5.1 钩子 ①）。
        _inner = new FileSystemWatcher(rootFullPath, filter)
        {
            IncludeSubdirectories = true,
            InternalBufferSize = internalBufferSize,
            // Size 只在 Windows 上被支持；设置它是为了让"原地覆盖写"这类不改变时间戳精度的
            // 写入也能被观察到（多一道保险，权威路径仍然是扫描）。
            NotifyFilter = NotifyFilters.FileName
                           | NotifyFilters.DirectoryName
                           | NotifyFilters.LastWrite
                           | NotifyFilters.CreationTime
                           | (OperatingSystem.IsWindows() ? NotifyFilters.Size : 0),
            EnableRaisingEvents = false,
        };

        _inner.Created += (_, e) => Raise(e.FullPath, WatcherChangeKind.Created);
        _inner.Changed += (_, e) => Raise(e.FullPath, WatcherChangeKind.Modified);
        _inner.Deleted += (_, e) => Raise(e.FullPath, WatcherChangeKind.Deleted);

        // 重命名：设计文档 03 §5.7 明确**不特殊处理**。这里只把它拆成"旧路径 + 新路径"两个
        // 普通变更，让扫描去算实际差异。之所以必须处理它，是因为 Windows 上一次重命名**不会**
        // 产生 Deleted/Created 通知——若漏掉这个事件，重命名的目录子树会彻底不被发现。
        _inner.Renamed += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.OldFullPath))
            {
                Raise(e.OldFullPath, WatcherChangeKind.Deleted);
            }

            if (!string.IsNullOrEmpty(e.FullPath))
            {
                Raise(e.FullPath, WatcherChangeKind.Created);
            }
        };

        _inner.Error += (_, e) => Error?.Invoke(this, e.GetException()?.Message ?? "未知错误");
    }

    public event EventHandler<WatcherEvent>? Changed;

    public event EventHandler<string>? Error;

    public bool EnableRaisingEvents
    {
        get => _inner.EnableRaisingEvents;
        set => _inner.EnableRaisingEvents = value;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 必须先关掉事件再 Dispose：否则 Dispose 与正在派发的事件会竞争。
        try
        {
            _inner.EnableRaisingEvents = false;
        }
        catch (ObjectDisposedException)
        {
            // 已经死了，忽略
        }

        _inner.Dispose();
    }

    private void Raise(string fullPath, WatcherChangeKind kind) =>
        Changed?.Invoke(this, new WatcherEvent(fullPath, kind));
}
