using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Monitoring;
using FileBackupTool.Core.Paths;
using FileBackupTool.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;

namespace FileBackupTool.UnitTests.Monitoring.Fakes;

/// <summary>记录每一次"该根变脏了"的请求。</summary>
public sealed record TriggeredScan(Guid TaskId, IReadOnlyList<string>? DirtyRelativePaths, string Reason)
{
    /// <summary>null 表示全量扫描。</summary>
    public bool IsFullScan => DirtyRelativePaths is null;
}

/// <summary>假的触发通道（真实实现是 Api 层的作业队列接入点）。</summary>
public sealed class RecordingBackupTrigger : IBackupTrigger
{
    private readonly object _gate = new();
    private readonly List<TriggeredScan> _requests = [];
    private readonly TaskCompletionSource<TriggeredScan> _first =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>非 null 时，投递抛这个异常——用来验证"投递失败不丢脏目录"。</summary>
    public Exception? FailOnRequest { get; set; }

    /// <summary>第一次投递时完成。用于让测试"等到"而不是"睡到"。</summary>
    public Task<TriggeredScan> FirstRequest => _first.Task;

    public IReadOnlyList<TriggeredScan> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public Task RequestBackupAsync(
        Guid taskId,
        IReadOnlyList<string>? dirtyRelativePaths,
        string reason,
        CancellationToken ct)
    {
        if (FailOnRequest is not null)
        {
            throw FailOnRequest;
        }

        var scan = new TriggeredScan(taskId, dirtyRelativePaths, reason);
        lock (_gate)
        {
            _requests.Add(scan);
        }

        _first.TrySetResult(scan);
        return Task.CompletedTask;
    }
}

/// <summary>可变的假任务源。</summary>
public sealed class FakeMonitoringTaskSource : IMonitoringTaskSource
{
    private readonly object _gate = new();
    private readonly List<MonitoredRoot> _roots = [];

    public void Add(MonitoredRoot root)
    {
        lock (_gate)
        {
            _roots.Add(root);
        }
    }

    public void Remove(Guid taskId)
    {
        lock (_gate)
        {
            _roots.RemoveAll(r => r.TaskId == taskId);
        }
    }

    public Task<IReadOnlyList<MonitoredRoot>> GetMonitoredRootsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<MonitoredRoot>>([.. _roots]);
        }
    }
}

/// <summary>
/// 把一个 <see cref="ChangeMonitor"/> 和它的全套假件装起来。
/// </summary>
/// <remarks>
/// 测试里的"驱动"只有三个动作：<see cref="Raise"/> 造事件、
/// <see cref="AdvanceAndPumpAsync"/> 推进时钟并跑一个处理周期、断言
/// <see cref="Trigger"/> 里收到了什么。**没有任何真实的 sleep**：
/// 静默期、合并窗口、指数退避全部由可推进的假时钟构造。
/// </remarks>
public sealed class MonitoringHarness : IDisposable
{
    public MonitoringHarness(
        Action<MonitoringOptions>? configureMonitoring = null,
        Action<JobOptions>? configureJobs = null)
    {
        Clock = new FakeClock();
        Fs = new FakeFileSystem();
        Watchers = new FakeFileWatcherFactory();
        Trigger = new RecordingBackupTrigger();
        Tasks = new FakeMonitoringTaskSource();
        Monitoring = new MonitoringOptions();
        configureMonitoring?.Invoke(Monitoring);
        Jobs = new JobOptions();
        configureJobs?.Invoke(Jobs);

        Monitor = new ChangeMonitor(
            Tasks,
            Trigger,
            Fs,
            WindowsPathNormalizer.Instance,
            Clock,
            Watchers,
            Microsoft.Extensions.Options.Options.Create(Monitoring),
            Microsoft.Extensions.Options.Options.Create(Jobs),
            Microsoft.Extensions.Options.Options.Create(Storage),
            NullLoggerFactory.Instance,
            NullLogger<ChangeMonitor>.Instance);
    }

    public FakeClock Clock { get; }

    public FakeFileSystem Fs { get; }

    public FakeFileWatcherFactory Watchers { get; }

    public RecordingBackupTrigger Trigger { get; }

    public FakeMonitoringTaskSource Tasks { get; }

    public MonitoringOptions Monitoring { get; }

    public JobOptions Jobs { get; }

    public StorageOptions Storage { get; } = new();

    public ChangeMonitor Monitor { get; }

    /// <summary>注册一个任务并返回它的 id。</summary>
    public Guid AddRoot(string sourceDir, bool watchEnabled = true, string? backupDir = null, string? name = null)
    {
        string trimmed = sourceDir.TrimEnd('\\');
        var root = new MonitoredRoot(
            Guid.NewGuid(),
            name ?? Path.GetFileName(trimmed),
            sourceDir,
            watchEnabled,
            backupDir);

        Tasks.Add(root);
        return root.TaskId;
    }

    /// <summary>取某个任务的假监控器（当前活着且已启用的那一个）。</summary>
    public FakeFileWatcher Watcher(Guid taskId)
    {
        string sourceDir = SourceDir(taskId);
        FakeFileWatcher? watcher = Watchers.Created.LastOrDefault(
            w => string.Equals(w.RootFullPath, sourceDir, StringComparison.OrdinalIgnoreCase)
                 && !w.Disposed
                 && w.EnableRaisingEvents);

        return watcher ?? throw new InvalidOperationException($"任务 {taskId} 没有可用的监控器。");
    }

    public string SourceDir(Guid taskId) =>
        Tasks.GetMonitoredRootsAsync(CancellationToken.None).GetAwaiter().GetResult()
            .Single(r => r.TaskId == taskId).SourceDir;

    /// <summary>建立监控。</summary>
    public Task ReloadAsync() => Monitor.ReloadAsync(CancellationToken.None);

    /// <summary>
    /// 往源目录里垫一批无关目录。
    /// </summary>
    /// <remarks>
    /// 退化阈值是"脏目录数 / 目录总数 &gt; 30% → 转全量"。不垫的话，
    /// "一个目录脏了、总共就一个目录"= 100%，每个测试都会撞上退化——那测的是退化规则本身，
    /// 而不是被测的行为。必须在**第一次派发之前**调用（目录普查有 TTL 缓存）。
    /// </remarks>
    public void AddPaddingDirectories(Guid taskId, int count = 20)
    {
        string root = SourceDir(taskId);
        for (int i = 0; i < count; i++)
        {
            Fs.AddDirectory($@"{root}\pad{i:00}");
        }
    }

    /// <summary>模拟一次通知。</summary>
    public void Raise(Guid taskId, string fullPath, WatcherChangeKind kind, bool isDirectory = false) =>
        Watcher(taskId).Raise(fullPath, kind, isDirectory);

    public Task PumpAsync() => Monitor.PumpAsync(CancellationToken.None);

    /// <summary>推进假时钟，然后跑一个处理周期。</summary>
    public async Task AdvanceAndPumpAsync(double seconds)
    {
        Clock.Advance(seconds);
        await PumpAsync();
    }

    /// <summary>
    /// 走完"静默期 → 局部扫描防抖 → 合并窗口"三段，
    /// 让一个已经稳定下来的变更变成一次真正的派发。
    /// </summary>
    public async Task SettleAndDispatchAsync()
    {
        await AdvanceAndPumpAsync(Monitoring.SettleWindowSeconds);
        await AdvanceAndPumpAsync(Monitoring.LocalScanDebounceSeconds);
        await AdvanceAndPumpAsync(Monitoring.CoalesceWindowSeconds);
    }

    public RootMonitorStatus Status(Guid taskId) =>
        Monitor.GetStatuses().Single(s => s.TaskId == taskId);

    public void Dispose() => Monitor.Dispose();
}
