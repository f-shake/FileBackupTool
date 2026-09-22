using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Monitoring;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FileBackupTool.Monitoring;

/// <summary>
/// 变更监控的唯一对外实现（<see cref="IChangeMonitor"/>）。
/// </summary>
/// <remarks>
/// 设计文档 03 §5。它的职责边界很清楚：**它不做备份，也不做扫描**。
/// 它只回答一个问题——"该让引擎去扫哪些目录了"，然后通过 <see cref="IBackupTrigger"/>
/// 把答案交出去。真正枚举文件、比对镜像、得出删除结论的永远是引擎。
/// <para>
/// <b>两套机制的协作契约</b>：
/// </para>
/// <code>
/// watcher（快速路径） ──┐
///                      ├──→ 待定表（部分写入防护）──→ 脏目录 ──→ 合并窗口 ──→ IBackupTrigger
/// 扫描（权威路径）   ──┘                                                  （全量 / 局部）
/// </code>
/// 快速路径**只产生"该根变脏了"这个信号**，不承载任何正确性；它是可有可无的加速器，
/// 所以它失效（钩子 ①）时唯一正确的反应是"排一次全量扫描"，而不是"尽力而为"。
/// <para>
/// <b>并发形状</b>（照抄参考实现的形状，设计文档 03 §5.6）：有界 Channel 汇聚所有监控器
/// 的事件 → 单消费者逐批处理 → 每个根一个状态机。所有共享状态只在单消费者线程上被修改，
/// watcher 回调只做预过滤与入队。
/// </para>
/// </remarks>
public sealed class ChangeMonitor : IChangeMonitor, IDisposable
{
    /// <summary>钩子 ③ 的理由文案。</summary>
    public const string StartupSelfCheckReason = "启动自检：无条件全量扫描（钩子③）";

    /// <summary>单次泵最多从一个根那里取多少条事件——实际上队列容量就是上限。</summary>
    private const int MaxEventsPerPump = 100_000;

    private readonly IMonitoringTaskSource _taskSource;
    private readonly IBackupTrigger _trigger;
    private readonly IFileSystem _fs;
    private readonly IPathNormalizer _normalizer;
    private readonly IClock _clock;
    private readonly IFileWatcherFactory _watcherFactory;
    private readonly MonitoringOptions _options;
    private readonly JobOptions _jobOptions;
    private readonly StorageOptions _storageOptions;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ChangeMonitor> _logger;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, RootMonitor> _roots = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ChangeEventQueue _queue;

    /// <summary>单消费者互斥。允许 API 线程显式触发一次泵（手动扫描），因此必须串行化。</summary>
    private readonly SemaphoreSlim _pumpLock = new(1, 1);

    private bool _disposed;

    public ChangeMonitor(
        IMonitoringTaskSource taskSource,
        IBackupTrigger trigger,
        IFileSystem fs,
        IPathNormalizer normalizer,
        IClock clock,
        IFileWatcherFactory watcherFactory,
        IOptions<MonitoringOptions> options,
        IOptions<JobOptions> jobOptions,
        IOptions<StorageOptions> storageOptions,
        ILoggerFactory loggerFactory,
        ILogger<ChangeMonitor> logger)
    {
        _taskSource = taskSource;
        _trigger = trigger;
        _fs = fs;
        _normalizer = normalizer;
        _clock = clock;
        _watcherFactory = watcherFactory;
        _options = options.Value;
        _jobOptions = jobOptions.Value;
        _storageOptions = storageOptions.Value;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _queue = new ChangeEventQueue(_options.EventQueueCapacity, _lifetime.Token, OnBackpressure);
    }

    /// <summary>泵的节拍。取静默期的四分之一，保证静默期的判定精度不至于太粗。</summary>
    public TimeSpan PumpInterval
    {
        get
        {
            double seconds = Math.Clamp(_options.SettleWindowSeconds / 4, 0.25, 5);
            return TimeSpan.FromSeconds(seconds);
        }
    }

    public IReadOnlyList<RootMonitorStatus> GetStatuses()
    {
        RootMonitor[] roots;
        lock (_gate)
        {
            roots = [.. _roots.Values];
        }

        var statuses = new List<RootMonitorStatus>(roots.Length);
        foreach (RootMonitor root in roots)
        {
            statuses.Add(root.ToStatus());
        }

        return statuses;
    }

    /// <summary>
    /// 按当前任务配置（重新）建立监控。任务增删改后调用。
    /// </summary>
    /// <remarks>
    /// 增量差分而不是"全拆重建"：重建会把所有待定表与合并窗口状态清空，
    /// 一个每天都在改任务描述的用户会让实时备份永远不触发。
    /// </remarks>
    public async Task ReloadAsync(CancellationToken ct)
    {
        IReadOnlyList<MonitoredRoot> desired = await _taskSource.GetMonitoredRootsAsync(ct).ConfigureAwait(false);
        IReadOnlyList<string> excluded = BuildExcludedRoots(desired);

        lock (_gate)
        {
            if (_disposed)
            {
                // `Dispose()` 已经跑过。此时再建 RootMonitor 会得到一个**无人释放**的 watcher：
                // 它的事件队列已经被 Complete，投递静默失败，但文件系统句柄一直握着。
                // 停机窗口里设置页/任务页的刷新正好会触发这条路径。
                return;
            }

            var wanted = new HashSet<Guid>(desired.Select(r => r.TaskId));

            foreach (Guid taskId in _roots.Keys.Where(id => !wanted.Contains(id)).ToList())
            {
                _roots[taskId].Dispose();
                _roots.Remove(taskId);
                _logger.LogInformation("停止监控任务 {TaskId}（任务已删除或不再需要监控）。", taskId);
            }

            foreach (MonitoredRoot root in desired)
            {
                if (_roots.TryGetValue(root.TaskId, out RootMonitor? existing))
                {
                    if (existing.Matches(root))
                    {
                        continue;
                    }

                    // 源目录或启用状态变了 → 旧监控器的状态全部作废，重建。
                    existing.Dispose();
                    _roots.Remove(root.TaskId);
                    _logger.LogInformation("源目录或监控开关发生变化，重建监控：{Task} → {Source}", root.TaskName, root.SourceDir);
                }

                try
                {
                    _roots[root.TaskId] = new RootMonitor(
                        root, _fs, _normalizer, _clock, _watcherFactory,
                        _options, _jobOptions, _queue, _lifetime.Token,
                        DispatchAsync, excluded, _loggerFactory.CreateLogger<RootMonitor>());

                    _logger.LogInformation(
                        "开始监控任务「{Task}」的源目录 {Source}（监控开关：{Enabled}）。",
                        root.TaskName, root.SourceDir, root.WatchEnabled);
                }
                catch (Exception ex)
                {
                    // RootMonitor 的构造已经把"watcher 构造失败"降级成 Failed 状态，
                    // 走到这里的只会是真正意外的东西（源目录非法之类）。不能让一个坏任务
                    // 把整个 ReloadAsync 打断——其它任务还得监控。
                    _logger.LogError(ex, "建立监控失败：任务「{Task}」{Source}", root.TaskName, root.SourceDir);
                }
            }
        }
    }

    /// <summary>
    /// <b>钩子 ③ 启动自检</b>：进程启动 → 无条件先跑一次全量扫描，不信任监控器。
    /// </summary>
    /// <remarks>
    /// 参考项目出过一次著名事故：<c>InitializeAsync()</c> 在 <c>Program.cs</c> 里没被调用，
    /// 监控整个没启动而没人发现。启动自检是唯一能发现"快速路径根本没接上"的机制——
    /// 因为权威路径无论如何都会跑一遍，它的结果与快速路径无关。
    /// <para>
    /// "无条件"的含义就是**不看监控器状态**：watcher 建成功也要跑。这里没有
    /// <c>if (watcher is null)</c> 之类的判断，那不是遗漏，是有意为之。
    /// </para>
    /// </remarks>
    public async Task StartupSelfCheckAsync(CancellationToken ct)
    {
        RootMonitor[] roots;
        lock (_gate)
        {
            roots = [.. _roots.Values];
        }

        if (roots.Length == 0)
        {
            _logger.LogInformation("启动自检：当前没有需要监控的任务。");
            return;
        }

        // 记录本身也有价值：这一行日志出现过，就说明自检这条路径确实被执行了。
        _logger.LogInformation("启动自检：对 {Count} 个源无条件排程全量扫描。", roots.Length);

        foreach (RootMonitor root in roots)
        {
            root.RequestFullScan(StartupSelfCheckReason);
        }

        // 立刻泵一次，不等 tick——自检的语义是"先跑一次"，不能排在 2.5 秒之后。
        await PumpAsync(ct).ConfigureAwait(false);
    }

    /// <summary>手动触发一次扫描（<c>POST /api/tasks/{id}/scan</c>）。</summary>
    public async Task<bool> RequestScanAsync(Guid taskId, bool fullScan, CancellationToken ct)
    {
        RootMonitor? root;
        lock (_gate)
        {
            _roots.TryGetValue(taskId, out root);
        }

        if (root is null)
        {
            return false;
        }

        root.RequestScan(fullScan);

        // 手动操作要立刻见效，不能等下一个 tick。
        await PumpAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 单消费者的一次处理周期：排空队列 → 各根推进状态机。
    /// </summary>
    /// <remarks>
    /// 这是"逐批处理"的最小实现：队列本身有界，所以一次排空天然就是一批。
    /// 也正因为它有界，被反压阻塞的投递方会在这一轮里被解除阻塞。
    /// </remarks>
    public async Task PumpAsync(CancellationToken ct)
    {
        // 停机窗口：`Dispose()` 会 `_pumpLock.Dispose()`，而此刻可能正有一个 HTTP 请求线程
        // 走到这里（`RequestScanAsync` 用的是 `HttpContext.RequestAborted`）。
        // 对已释放的 SemaphoreSlim 调 WaitAsync / Release 都会抛 ObjectDisposedException，
        // 而且是从一个**正常请求**的路径里冒出来。
        if (_disposed)
        {
            return;
        }

        try
        {
            await _pumpLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 与上一步之间被 Dispose 抢了——当作停机处理，不做任何事。
            return;
        }

        try
        {
            RootMonitor[] roots;
            lock (_gate)
            {
                roots = [.. _roots.Values];
            }

            int drained = 0;
            while (drained < MaxEventsPerPump && _queue.TryRead(out ChangeEvent change))
            {
                drained++;
                Route(change, roots);
            }

            foreach (RootMonitor root in roots)
            {
                ct.ThrowIfCancellationRequested();
                await root.PumpAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                _pumpLock.Release();
            }
            catch (ObjectDisposedException)
            {
                // `Dispose()` 在 WaitAsync 之后、Release 之前跑完了。停机时这不是错误，
                // 只是没有锁可还——吞掉，别让它从一个请求路径里抛出去。
            }
        }
    }

    /// <summary>
    /// 生产循环：泵 → 等到下一个节拍。设计文档 01 §4.3 要求监控器跑在独立的
    /// <c>BackgroundService</c> 里。
    /// </summary>
    /// <remarks>
    /// 等待走 <see cref="IClock.Delay"/>，因此单元测试可以用可推进的假时钟驱动整个循环，
    /// **不需要真的 sleep**。
    /// </remarks>
    public async Task RunAsync(CancellationToken ct)
    {
        TimeSpan interval = PumpInterval;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PumpAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // 一个根（或一次投递）出问题**不能**让所有根的实时监控一起停摆。
                // 单个根内部的失败已经各自被吞掉并记日志，能走到这里的都是意外；
                // 记下来，下个节拍继续。
                _logger.LogError(ex, "监控处理周期发生异常，将在下个节拍重试。");
            }

            try
            {
                await _clock.Delay(interval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 取消是**正常退出**，不是异常：这样宿主（以及测试）await 这个循环时
                // 不需要额外包一层 try。
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (RootMonitor root in _roots.Values)
            {
                root.Dispose();
            }

            _roots.Clear();
        }

        // 先关队列再取消：让被反压阻塞的投递方立刻解脱，而不是等取消令牌。
        _queue.Complete();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _pumpLock.Dispose();
    }

    // ------------------------------------------------------------------
    // 内部
    // ------------------------------------------------------------------

    /// <summary>
    /// 钩子 ②：队列进入了反压。
    /// </summary>
    /// <remarks>
    /// 这个方法**可能从 watcher 回调线程调用**，所以只能做两件线程安全的事：
    /// 给对应根加个计数、置一个幂等标志。真正的扫描排程由单消费者在下一轮完成。
    /// </remarks>
    private void OnBackpressure(Guid taskId, long totalCount)
    {
        RootMonitor[] roots;
        lock (_gate)
        {
            roots = [.. _roots.Values];
        }

        foreach (RootMonitor root in roots)
        {
            if (root.TaskId == taskId)
            {
                root.NoteBackpressure();
                break;
            }
        }

        // 阈值语义是"累计反压次数达到阈值"——它是累计量，所以一次越过后每次都会再触发，
        // 但 RequestBackpressureFullScan 是幂等的（就一个 bool），不会堆出多次扫描。
        if (totalCount >= _options.QueueBackpressureThreshold)
        {
            // 队列是全局的：不知道丢的是哪个根的事件，只能对所有根保守地排全量。
            foreach (RootMonitor root in roots)
            {
                root.RequestBackpressureFullScan();
            }
        }
    }

    private void Route(in ChangeEvent change, RootMonitor[] roots)
    {
        foreach (RootMonitor root in roots)
        {
            if (root.TaskId == change.TaskId)
            {
                root.Accept(change);
                return;
            }
        }

        // 根已经被移除（任务刚被删）——事件是它死前发出来的，丢掉即可。
    }

    /// <summary>投递给宿主的真实通道。</summary>
    private async Task DispatchAsync(
        Guid taskId,
        IReadOnlyList<string>? dirtyRelativePaths,
        string reason,
        CancellationToken ct)
    {
        // 空列表在这里是有毒的：它会被引擎当成"没有待办"从而什么都不扫，
        // 而调用方的本意显然是"扫一次"。空 → 全量是唯一的无歧义解释。
        if (dirtyRelativePaths is { Count: 0 })
        {
            dirtyRelativePaths = null;
        }

        await _trigger.RequestBackupAsync(taskId, dirtyRelativePaths, reason, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 必须排除在监控之外的目录：自己的数据目录 + 所有任务的备份目录。
    /// </summary>
    /// <remarks>
    /// 设计文档 03 §5.6 点名要求排除数据目录：SQLite 的 <c>-wal</c> / <c>-shm</c> 边车文件
    /// 每次事务提交都会写，落进源目录范围就是海量噪声。
    /// <para>
    /// 备份目录同理，而且更严重——它会导致<b>自激</b>：备份写出 blob → 触发事件 →
    /// 排程备份 → 又写出 blob。源目录里包含备份目录本来就该在保存任务时被拒（设计文档 02 §7），
    /// 这里再加一道，是为了不依赖那个校验。
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> BuildExcludedRoots(IReadOnlyList<MonitoredRoot> roots)
    {
        var excluded = new List<string>();

        if (!string.IsNullOrWhiteSpace(_storageOptions.DataDirectory))
        {
            excluded.Add(_storageOptions.DataDirectory);
        }

        foreach (MonitoredRoot root in roots)
        {
            if (!string.IsNullOrWhiteSpace(root.BackupDir))
            {
                excluded.Add(root.BackupDir);
            }
        }

        return excluded;
    }
}
