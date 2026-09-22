using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Monitoring;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Monitoring;

/// <summary>把一次扫描请求投给宿主（<c>IBackupTrigger</c>）。</summary>
internal delegate Task ScanDispatcher(
    Guid taskId,
    IReadOnlyList<string>? dirtyRelativePaths,
    string reason,
    CancellationToken ct);

/// <summary>
/// 一个源根的监控状态机。
/// </summary>
/// <remarks>
/// <b>协作契约（设计文档 03 §5.1）</b>——这是本模块存在的理由：
/// <code>
/// 快速路径（watch）只负责产生"该根变脏了"的信号，不承载任何正确性。
/// 权威路径（scan）是唯一能得出"文件已删除"、也是唯一能修复快速路径静默失效的机制。
/// </code>
/// 落到代码上就是：watcher 事件的**唯一**出口是"把路径写进待定表"，它既不产生快照、
/// 也不产生"删除"这个结论。唯一的输出是 <see cref="ScanDispatcher"/>——请求一次扫描。
/// <para>
/// <b>并发形状（设计文档 03 §5.6）</b>：watcher 回调只做预过滤 + 入队；
/// 其余一切（待定表、合并窗口、派发）都只在单消费者线程上发生。
/// 因此本类里除了 <see cref="NoteBackpressure"/>、<see cref="RequestBackpressureFullScan"/>、
/// <see cref="RequestFullScan"/>、<see cref="RequestScan"/> 这几个"从外部置一个幂等标志"的
/// 方法之外，没有任何跨线程写。
/// </para>
/// </remarks>
internal sealed class RootMonitor : IDisposable
{
    /// <summary>监控器出错后重建的最小间隔。防止错误风暴变成重建风暴。</summary>
    private static readonly TimeSpan WatcherRecreateCooldown = TimeSpan.FromSeconds(30);

    private readonly MonitoredRoot _root;
    private readonly IFileSystem _fs;
    private readonly IPathNormalizer _normalizer;
    private readonly IClock _clock;
    private readonly IFileWatcherFactory _watcherFactory;
    private readonly MonitoringOptions _options;
    private readonly ChangeEventQueue _queue;
    private readonly CancellationToken _lifetime;
    private readonly ScanDispatcher _dispatch;
    private readonly WatchPathFilter _filter;
    private readonly ILogger _logger;

    private readonly PendingChangeTable _pending;
    private readonly ScanCoalescer _coalescer;
    private readonly DirectoryCensus _census;

    private readonly object _gate = new();

    private IFileWatcher? _watcher;
    private RootMonitorMode _mode;
    private string? _error;
    private int _backpressureCount;
    private volatile bool _backpressureFullScanRequested;
    private DateTime? _lastEventUtc;
    private DateTime? _lastScanUtc;
    private DateTime? _lastFullScanUtc;
    private DateTime? _lastTriggerUtc;
    private DateTime _nextBackstopUtc = DateTime.MaxValue;
    private DateTime _nextWatcherRebuildUtc = DateTime.MaxValue;
    private bool _disposed;

    // 状态上报用的缓存：状态可能从 API 线程读，而字典/集合只由单消费者改。
    // 与其给每个集合加锁，不如让消费者在每轮结束时把结果写进这两个 volatile 字段。
    private int _pendingCountCache;
    private IReadOnlyList<string>? _scheduledScopesCache = [];

    public RootMonitor(
        MonitoredRoot root,
        IFileSystem fs,
        IPathNormalizer normalizer,
        IClock clock,
        IFileWatcherFactory watcherFactory,
        MonitoringOptions options,
        JobOptions jobOptions,
        ChangeEventQueue queue,
        CancellationToken lifetime,
        ScanDispatcher dispatch,
        IReadOnlyList<string> excludedRoots,
        ILogger logger)
    {
        _root = root;
        _fs = fs;
        _normalizer = normalizer;
        _clock = clock;
        _watcherFactory = watcherFactory;
        _options = options;
        _queue = queue;
        _lifetime = lifetime;
        _dispatch = dispatch;
        _logger = logger;
        _filter = new WatchPathFilter(normalizer, excludedRoots);

        _pending = new PendingChangeTable(
            fs, normalizer, clock, jobOptions, SettleWindow, logger);

        _coalescer = new ScanCoalescer(
            normalizer,
            TimeSpan.FromSeconds(Math.Max(0, options.LocalScanDebounceSeconds)),
            TimeSpan.FromSeconds(Math.Max(0, options.CoalesceWindowSeconds)));

        // 先定模式：兜底周期与轮询周期都依赖它。
        if (!root.WatchEnabled)
        {
            // 任务没启用实时监控。仍然上报状态（UI 要显示"不监控"），但不建 watcher、不起定时器。
            // 手动 RequestScanAsync 仍然有效——那是用户显式操作，与是否实时监控无关。
            _mode = RootMonitorMode.Disabled;
            _census = new DirectoryCensus(fs, CensusTtl(), logger);
            return;
        }

        bool network = IsNetworkSource(root.SourceDir);
        _mode = network ? RootMonitorMode.Polling : RootMonitorMode.Watcher;
        _census = new DirectoryCensus(fs, CensusTtl(), logger);

        if (network)
        {
            // 设计文档 03 §5.5：UNC / NAS / **映射盘** 一律放弃 watcher，只跑轮询。
            // 理由：FileSystemWatcher 在网络路径上会**静默失效**——构造成功、事件永不到来，
            // GalleryCloud 就是这么被坑的（唯一的兜底是等第二天凌晨）。
            _logger.LogInformation(
                "源「{Task}」是网络路径（UNC / 映射盘），使用轮询模式，间隔 {Minutes} 分钟。",
                root.TaskName, _options.NetworkPollIntervalMinutes);
        }
        else
        {
            CreateWatcher();
        }

        _nextBackstopUtc = NextBackstop(_clock.UtcNow);
    }

    public Guid TaskId => _root.TaskId;

    public string SourceDir => _root.SourceDir;

    /// <summary>该根是否与给定的配置一致（不一致就要重建监控器）。</summary>
    public bool Matches(MonitoredRoot other) =>
        other.TaskId == _root.TaskId
        && _normalizer.AreEqual(other.SourceDir, _root.SourceDir)
        && other.WatchEnabled == _root.WatchEnabled;

    private TimeSpan SettleWindow => TimeSpan.FromSeconds(Math.Max(0, _options.SettleWindowSeconds));

    /// <summary>
    /// 兜底周期：轮询模式下就是轮询间隔；否则是<b>无条件</b>的周期性全量扫描间隔。
    /// </summary>
    /// <remarks>
    /// 轮询模式**不再单独挂一条 6 小时的兜底定时器**：3 分钟的轮询本身就是全量扫描，
    /// 再挂一条 6 小时的只会是一条永远轮不到的代码。设计文档说"无条件跑"指的是
    /// "不管监控器有没有报过事件都要跑"，不是"每个根都要挂两条定时器"。
    /// </remarks>
    private TimeSpan? BackstopPeriod()
    {
        double seconds = _mode == RootMonitorMode.Polling
            ? _options.NetworkPollIntervalMinutes * 60
            : _options.FullScanFallbackHours * 3600;

        return seconds <= 0 ? null : TimeSpan.FromSeconds(seconds);
    }

    /// <summary>目录普查的 TTL：取兜底周期（每个周期反正都会重新枚举整棵树）。</summary>
    private TimeSpan CensusTtl() => BackstopPeriod() ?? TimeSpan.FromHours(6);

    private DateTime NextBackstop(DateTime nowUtc) =>
        BackstopPeriod() is { } period ? nowUtc + period : DateTime.MaxValue;

    private string BackstopReason => _mode == RootMonitorMode.Polling
        ? "网络路径轮询扫描（无 watcher，只能靠扫描）"
        : "周期性全量扫描兜底（局部扫描发现不了被漏掉的事件）";

    // ------------------------------------------------------------------
    // 外部入口（可能来自任意线程，只做幂等标志位）
    // ------------------------------------------------------------------

    /// <summary>
    /// 钩子 ① 的另一半 / ③ 启动自检 / 手动扫描：排一次全量扫描。
    /// </summary>
    public void RequestFullScan(string reason) => _coalescer.RequestFullScan(reason, _clock.UtcNow);

    /// <summary>
    /// 用户手动触发的扫描（<c>POST /api/tasks/{id}/scan</c>）。
    /// </summary>
    /// <remarks>
    /// <paramref name="fullScan"/> 为 false 且当前没有待办时，仍然只能从源根开始扫——
    /// 监控器手里没有"上次镜像"，唯一的事实来源是引擎。而引擎把"空相对路径"当作全量
    /// （见 <c>BackupEngine.NormalizeDirtyScopes</c>），所以两条路殊途同归。
    /// </remarks>
    public void RequestScan(bool fullScan)
    {
        DateTime now = _clock.UtcNow;

        if (fullScan || !_coalescer.HasPending)
        {
            _coalescer.RequestFullScan(fullScan ? "用户手动请求全量扫描" : "用户手动请求扫描", now);
            return;
        }

        // 已有待办：跳过防抖与合并窗口，立刻派发这些范围（它们早已过了静默期）。
        _coalescer.FlushNow(now);
    }

    /// <summary>钩子 ②：队列进入了反压（若还用 TryWrite，这条事件就已经被丢弃了）。</summary>
    public void NoteBackpressure() => Interlocked.Increment(ref _backpressureCount);

    /// <summary>
    /// 钩子 ② 的动作：队列反压累计超阈值 → 排一次全量扫描。
    /// </summary>
    /// <remarks>
    /// 队列是**全局**的，因此无法定位丢的是哪个根的事件，只能对所有根保守地排全量。
    /// 代价是一次多余的扫描，收益是"绝不漏掉一个变更"。
    /// </remarks>
    public void RequestBackpressureFullScan() => _backpressureFullScanRequested = true;

    // ------------------------------------------------------------------
    // 单消费者
    // ------------------------------------------------------------------

    /// <summary>把一条已出队的事件喂给本根（只在单消费者线程上调用）。</summary>
    public void Accept(in ChangeEvent change)
    {
        _lastEventUtc = _clock.UtcNow;

        if (change.Kind == WatcherChangeKind.Fault)
        {
            HandleWatcherFault(change.Detail);
            return;
        }

        if (!_filter.ShouldWatch(change.FullPath))
        {
            return;
        }

        // FileSystemWatcher 不报告路径是不是目录，而"目录自身"与"文件所在目录"的脏范围不同
        // （目录变更要扫自己那棵子树）。这一次 IO 放在消费者线程上，**不在回调里**。
        bool isDirectory = change.IsDirectory || _fs.DirectoryExists(change.FullPath);

        // 到这里为止都还不是"触发备份"，只是"进入待定表"（部分写入防护，§5.2）。
        _pending.Track(change.FullPath, isDirectory);
    }

    /// <summary>推进本根的状态机（只在单消费者线程上调用）。</summary>
    public async Task PumpAsync(CancellationToken ct)
    {
        DateTime now = _clock.UtcNow;

        // 钩子 ② 的标志在这里消费，好让 ScanCoalescer 仍然只被单线程写。
        if (_backpressureFullScanRequested)
        {
            _backpressureFullScanRequested = false;
            _coalescer.RequestFullScan("事件队列反压（钩子②）", now);
        }

        if (_mode != RootMonitorMode.Disabled)
        {
            // 1) 部分写入防护：把已经稳定的变更放行到"待备份"。
            foreach (SettledChange settled in await _pending.EvaluateAsync(ct).ConfigureAwait(false))
            {
                _coalescer.MarkDirty(GetDirtyScope(settled), _clock.UtcNow);
            }

            // 2) 周期性全量扫描兜底（设计文档 03 §5.4 约束 1）。**无条件**跑：
            //    它正是"局部扫描发现不了被漏掉的事件"这个固有盲区唯一的补丁。
            if (_clock.UtcNow >= _nextBackstopUtc)
            {
                _nextBackstopUtc = NextBackstop(_clock.UtcNow);
                _coalescer.RequestFullScan(BackstopReason, _clock.UtcNow);
            }

            // 3) 监控器失效后的重建（钩子 ①）。失败就以 Failed 状态示人，定期再试。
            if (_mode == RootMonitorMode.Failed && _clock.UtcNow >= _nextWatcherRebuildUtc)
            {
                DisposeWatcher();
                CreateWatcher();
            }
        }

        // 4) 合并窗口到点 → 派发。
        ScanRequest? ready = _coalescer.TakeReady(_clock.UtcNow);
        if (ready is null)
        {
            RefreshStatusCache();
            return;
        }

        ScanRequest effective = ApplyDegradeRule(ready);

        try
        {
            await _dispatch(_root.TaskId, ToDirtyPaths(effective), effective.Reason, ct).ConfigureAwait(false);
            _coalescer.Commit();

            DateTime dispatchedAt = _clock.UtcNow;
            lock (_gate)
            {
                _lastTriggerUtc = dispatchedAt;
                _lastScanUtc = dispatchedAt;
                if (effective.Kind == ScanKind.Full)
                {
                    _lastFullScanUtc = dispatchedAt;
                }
            }

            _logger.LogInformation(
                "源「{Task}」排程扫描：{Reason}（范围：{Scope}）",
                _root.TaskName, effective.Reason,
                effective.Kind == ScanKind.Full ? "全量" : string.Join("、", effective.Scopes));
        }
        catch (OperationCanceledException)
        {
            // 取消也必须回滚。走到这里说明 `TakeReady` 已经把脏目录从 `_scheduled`/`_pending`
            // 摘走放进了 `_inFlight`，而投递没做完——最常见的触发是**手动扫描时用户关掉标签页/
            // 导航离开**（`ct` 就是 `HttpContext.RequestAborted`），宿主停机同理。
            //
            // 原来的过滤器是 `when (ex is not OperationCanceledException)`，恰好把这条路径排除在外，
            // 而那时候下面那句注释写的正是"投递失败不能把脏目录弄丢"——语义与实现相反。
            // 不回滚的后果是静默的：这批"磁盘上确实变了"的目录永久消失，要等这些文件再次被改、
            // 或等到 6 小时兜底全量扫描、甚至下次重启才可能被覆盖到。
            _coalescer.Rollback(_clock.UtcNow);
            _logger.LogInformation(
                "源「{Task}」排程扫描被取消（{Reason}），脏目录已回滚，将在下个节拍重试。",
                _root.TaskName, effective.Reason);

            // 回滚完照原样抛出：调用方（宿主停机 / 那个被中止的请求）应当知道发生了取消，
            // 不能被这里吞掉。
            throw;
        }
        catch (Exception ex)
        {
            // 投递失败不能把脏目录弄丢：它们代表"磁盘上确实变了"这个事实。
            _coalescer.Rollback(_clock.UtcNow);
            _logger.LogWarning(ex, "源「{Task}」排程扫描失败，将在合并窗口后重试。", _root.TaskName);
        }

        RefreshStatusCache();
    }

    public RootMonitorStatus ToStatus()
    {
        lock (_gate)
        {
            return new RootMonitorStatus(
                _root.TaskId,
                _root.TaskName,
                _root.SourceDir,
                _mode,
                _error,
                Volatile.Read(ref _pendingCountCache),
                Volatile.Read(ref _backpressureCount),
                _lastEventUtc,
                _lastScanUtc,
                _lastFullScanUtc,
                _lastTriggerUtc,
                _scheduledScopesCache);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        DisposeWatcher();
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>
    /// 建立 watcher。失败即**钩子 ①**：设计文档 03 §5.1 要求"立即对该根排一次全量扫描"。
    /// </summary>
    /// <remarks>
    /// GalleryCloud 在这里把异常 <c>catch</c> 掉，于是"这个根完全没在监控"这件事
    /// 没有任何人知道，唯一兜底是第二天凌晨的全量扫描。在相册里可以接受，在备份里不可以。
    /// </remarks>
    private void CreateWatcher()
    {
        try
        {
            IFileWatcher watcher = _watcherFactory.Create(
                _root.SourceDir, "*", PhysicalFileWatcherFactory.DefaultInternalBufferSize);

            watcher.Changed += OnWatcherChanged;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
            _mode = RootMonitorMode.Watcher;
            _nextWatcherRebuildUtc = DateTime.MaxValue;
        }
        catch (Exception ex)
        {
            _watcher = null;
            _mode = RootMonitorMode.Failed;
            _error = $"监控器构造失败：{ex.Message}";
            _nextWatcherRebuildUtc = _clock.UtcNow + WatcherRecreateCooldown;

            _logger.LogError(ex, "源「{Task}」监控器构造失败，改为依赖周期性全量扫描。", _root.TaskName);

            _coalescer.RequestFullScan($"监控器构造失败，立即全量扫描（钩子①）：{ex.Message}", _clock.UtcNow);
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.Changed -= OnWatcherChanged;
            _watcher.Error -= OnWatcherError;
            _watcher.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "释放监控器时出错：{Source}", _root.SourceDir);
        }

        _watcher = null;
    }

    /// <summary>
    /// watcher 回调。**不做任何 IO**（设计文档 03 §5.6 的硬约束）。
    /// </summary>
    /// <remarks>
    /// 这里是整条快速路径上唯一允许出现的代码：预过滤 + 入队。任何一次 <c>File.Exists</c>
    /// 都是在拿"监控线程被阻塞"换"少写一行代码"，而回调线程被阻塞的后果是事件堆积 → 缓冲
    /// 区溢出 → 静默丢事件。
    /// </remarks>
    private void OnWatcherChanged(object? sender, WatcherEvent e)
    {
        if (_disposed)
        {
            return;
        }

        // 设计文档 03 §5.7：目录重命名**不特殊处理**。重命名已经被拆成两个普通事件，
        // 它们会各自进入待定表，最终表现成"旧路径全删 + 新路径全增"的一次大快照。
        if (e.Kind != WatcherChangeKind.Fault && !_filter.ShouldWatch(e.FullPath))
        {
            return;
        }

        _queue.Enqueue(new ChangeEvent(_root.TaskId, e.FullPath, e.Kind, e.IsDirectory, e.Detail), _lifetime);
    }

    /// <summary>
    /// watcher 的 <c>Error</c> 事件（缓冲区溢出）。
    /// </summary>
    /// <remarks>
    /// 它也走队列而不是就地处理：就地处理会破坏"单消费者"这个前提（回调线程会去碰
    /// 合并窗口的状态）。走队列的代价是"错误要等一个 tick 才被处理"，收益是并发形状不变。
    /// </remarks>
    private void OnWatcherError(object? sender, string message)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Enqueue(
            new ChangeEvent(_root.TaskId, string.Empty, WatcherChangeKind.Fault, false, message),
            _lifetime);
    }

    /// <summary>钩子 ① 的 Error 分支：缓冲区溢出意味着**事件已经丢了**，只能靠全量扫描补。</summary>
    private void HandleWatcherFault(string? message)
    {
        _mode = RootMonitorMode.Failed;
        _error = $"监控器错误：{message}";
        _nextWatcherRebuildUtc = _clock.UtcNow + WatcherRecreateCooldown;

        _logger.LogError("源「{Task}」监控器报告错误：{Message}（已排程全量扫描）", _root.TaskName, message);

        // 注意不在这里 Dispose watcher：溢出之后它通常还能继续工作，先让它干着，
        // 等冷却期到了再整体换一个新的。否则冷却期内这个根完全没有快速路径。
        _coalescer.RequestFullScan($"监控器错误，立即全量扫描（钩子①）：{message}", _clock.UtcNow);
    }

    /// <summary>
    /// 一个变更应该重扫哪个目录。
    /// </summary>
    /// <remarks>
    /// 交付给引擎的是**目录**（不是文件）：引擎把脏目录当作枚举根，并在**该子树内**做
    /// "磁盘上没有 → 删除"的墓碑判定（设计文档 03 §5.4 约束 3）。
    /// 若这里传一个文件路径，引擎会因为"目录不存在"而直接跳过枚举，
    /// 然后把这个文件判成**删除**——一个错误的墓碑。所以只传目录。
    /// </remarks>
    private string GetDirtyScope(in SettledChange settled)
    {
        string full = settled.IsDirectory
            ? settled.FullPath
            : _normalizer.GetDirectoryName(settled.FullPath);

        string? relative = _normalizer.GetRelativePathOrNull(_root.SourceDir, full);
        if (relative is null)
        {
            // 越界（理论上不可能，watcher 是挂在源目录上的）：保守按根处理。
            _logger.LogWarning("变更路径不在源目录内，按源根处理：{Path}", settled.FullPath);
            return string.Empty;
        }

        return relative;
    }

    /// <summary>设计文档 03 §5.4 约束 2：脏目录占比超阈值 → 退化为全量扫描。</summary>
    private ScanRequest ApplyDegradeRule(ScanRequest request)
    {
        if (request.Kind != ScanKind.Local || request.Scopes.Count == 0)
        {
            return request;
        }

        int total = _census.GetCount(_root.SourceDir, _clock.UtcNow);
        if (!DirtyDirectorySet.ShouldDegradeToFullScan(request.Scopes.Count, total, _options.LocalScanDegradeRatio))
        {
            return request;
        }

        // 此时局部扫描要枚举的目录数已经接近全量，而全量还能顺带修复被漏掉的事件——
        // 它变成了严格更优的选择，不是"保守起见"。
        return new ScanRequest(
            ScanKind.Full,
            [],
            $"脏目录 {request.Scopes.Count}/{total} 超过退化阈值 {_options.LocalScanDegradeRatio:P0}，转为全量扫描");
    }

    private static IReadOnlyList<string>? ToDirtyPaths(ScanRequest request) =>
        request.Kind == ScanKind.Full ? null : request.Scopes;

    /// <summary>源路径是不是"网络路径"（UNC 或**映射盘**）。设计文档 03 §5.5。</summary>
    private bool IsNetworkSource(string sourceDir)
    {
        if (_normalizer.IsNetworkPath(sourceDir))
        {
            return true;
        }

        // 映射盘走的是盘符，IsNetworkPath 看不出来，只能问驱动器类型。
        // 这正是 DriveEntry.IsNetwork 存在的理由（IFileSystem.cs 里写着）。
        foreach (DriveEntry drive in _fs.GetLogicalDrives())
        {
            if (!drive.IsNetwork)
            {
                continue;
            }

            if (_normalizer.IsSameOrUnder(sourceDir, drive.RootPath))
            {
                return true;
            }
        }

        return false;
    }

    private void RefreshStatusCache()
    {
        Volatile.Write(ref _pendingCountCache, _pending.Count + _coalescer.PendingCount);
        _scheduledScopesCache = _coalescer.ScheduledScopes;
    }
}
