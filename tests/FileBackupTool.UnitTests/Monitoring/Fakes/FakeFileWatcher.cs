using FileBackupTool.Monitoring;

namespace FileBackupTool.UnitTests.Monitoring.Fakes;

/// <summary>
/// 假监控器：测试自己决定什么时候触发哪个事件。
/// </summary>
/// <remarks>
/// 这正是"<c>FileSystemWatcher</c> 必须包在可替换的抽象后面"的兑现——
/// 用真的 <c>FileSystemWatcher</c> 就没法构造"队列满"、"缓冲区溢出"、"构造失败"这些场景。
/// </remarks>
public sealed class FakeFileWatcher(string rootFullPath) : IFileWatcher
{
    public string RootFullPath { get; } = rootFullPath;

    public event EventHandler<WatcherEvent>? Changed;

    public event EventHandler<string>? Error;

    public bool EnableRaisingEvents { get; set; }

    public bool Disposed { get; private set; }

    /// <summary>模拟一次文件系统通知。</summary>
    public void Raise(string fullPath, WatcherChangeKind kind, bool isDirectory = false) =>
        Changed?.Invoke(this, new WatcherEvent(fullPath, kind) { IsDirectory = isDirectory });

    /// <summary>模拟 <c>Error</c> 事件（缓冲区溢出）。</summary>
    public void RaiseError(string message) => Error?.Invoke(this, message);

    public void Dispose() => Disposed = true;
}

/// <summary>假工厂。<see cref="FailOnCreate"/> 用来构造"监控器构造失败"（钩子 ①）。</summary>
public sealed class FakeFileWatcherFactory : IFileWatcherFactory
{
    private readonly List<FakeFileWatcher> _created = [];

    /// <summary>非 null 时，每次创建都抛这个异常。</summary>
    public Exception? FailOnCreate { get; set; }

    public IReadOnlyList<FakeFileWatcher> Created
    {
        get
        {
            lock (_created)
            {
                return [.. _created];
            }
        }
    }

    public IFileWatcher Create(string rootFullPath, string filter, int internalBufferSize)
    {
        if (FailOnCreate is not null)
        {
            throw FailOnCreate;
        }

        var watcher = new FakeFileWatcher(rootFullPath);
        lock (_created)
        {
            _created.Add(watcher);
        }

        return watcher;
    }
}
