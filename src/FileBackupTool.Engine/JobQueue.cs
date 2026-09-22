using System.Collections.Concurrent;
using System.Threading.Channels;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine;

/// <summary>
/// 全局串行 IO 作业队列（单消费者 Channel）。设计文档 01 §4.1。
/// </summary>
/// <remarks>
/// 这个类型**不是** <c>BackgroundService</c>——宿主化的部分（<c>IHostedService</c>）属于 Api 层，
/// 这里只提供"启动/停止消费者循环"的纯逻辑，方便单元测试直接驱动。
/// </remarks>
public sealed class JobQueue(
    IBackupService backup,
    IRestoreService restore,
    IIntegrityService integrity,
    IJobEventPublisher events,
    IClock clock,
    ILogger<JobQueue> logger) : IJobQueue, IAsyncDisposable
{
    /// <summary>保留的作业历史条数（含已完成）。作业**不跨重启恢复**，所以这是一个进程内上限。</summary>
    private const int HistoryLimit = 200;

    /// <summary>进度事件的发送节流（SignalR 不该被逐文件的进度淹掉）。</summary>
    private static readonly TimeSpan ProgressThrottle = TimeSpan.FromMilliseconds(250);

    private readonly Channel<JobEntry> _channel = Channel.CreateUnbounded<JobEntry>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly ConcurrentDictionary<string, JobEntry> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loop;

    public bool IsBusy => _jobs.Values.Any(j => j.Status == JobStatus.Running || j.Status == JobStatus.Queued);

    public int QueuedCount => _jobs.Values.Count(j => j.Status == JobStatus.Queued);

    /// <summary>启动消费者循环。只能调一次。</summary>
    public void Start(CancellationToken hostStopping)
    {
        lock (_gate)
        {
            if (_loop is not null)
            {
                return;
            }

            _loopCts = CancellationTokenSource.CreateLinkedTokenSource(hostStopping);
            _loop = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
        }
    }

    public Task<string> EnqueueAsync(JobRequest request, CancellationToken ct)
    {
        var entry = new JobEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Request = request,
            Status = JobStatus.Queued,
            EnqueuedAtUtc = clock.UtcNow,
            Progress = new JobProgress(JobPhase.Queued),
        };

        _jobs[entry.Id] = entry;
        _order.Enqueue(entry.Id);
        TrimHistory();

        if (!_channel.Writer.TryWrite(entry))
        {
            entry.Status = JobStatus.Failed;
            entry.Error = "作业队列已关闭。";
            throw new InvalidOperationException("作业队列已关闭。");
        }

        logger.LogInformation("作业 {Id} 入队：{Description}（前面还有 {Ahead} 个）",
            entry.Id, request.Description, QueuedCount - 1);

        // `position` 用 **1 基**，与 REST（`GET /api/jobs`：`JobDtos` 注释写"1 表示下一个执行"）
        // 以及前端（`e.position > 0` → 提示"前面还有 position-1 个作业"）一致。
        // `PositionOf` 返回的是"前面有多少个 Queued"，是 0 基，所以这里 +1。
        // 原来直接把 0 基值发出去，第一个排队作业的位置是 0 —— 前端那句 `> 0` 判定不成立，
        // 用户**看不到任何"已排队"提示**；第二个作业则显示"前面还有 0 个"。
        _ = events.PublishAsync("job.queued", new
        {
            jobId = entry.Id,
            type = request.Type.ToString(),
            position = PositionOf(entry.Id) + 1,
            description = request.Description,
        }, CancellationToken.None);

        return Task.FromResult(entry.Id);
    }

    public Task<IReadOnlyList<JobView>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<JobView> views =
        [
            .. _order
                .Select(id => _jobs.TryGetValue(id, out JobEntry? e) ? e : null)
                .Where(e => e is not null)
                .Select(e => ToView(e!))
                .OrderByDescending(v => v.EnqueuedAtUtc)
        ];

        return Task.FromResult(views);
    }

    public Task<JobView?> GetAsync(string jobId, CancellationToken ct) =>
        Task.FromResult(_jobs.TryGetValue(jobId, out JobEntry? entry) ? ToView(entry) : null);

    public Task<bool> CancelAsync(string jobId, CancellationToken ct)
    {
        if (!_jobs.TryGetValue(jobId, out JobEntry? entry))
        {
            return Task.FromResult(false);
        }

        // 状态迁移与 `Cancellation` 的读写全部与消费循环串行化（两边都用 `_gate`）。
        // 不加锁会真实发生两种坏结果：
        //  ① **"取消成功"但照跑**：消费循环可能已经读过 `Status`（判断要不要跳过）而还没写 `Running`，
        //     此时这里置 `Aborted`、向用户返回 true、还发了 `job.aborted`；随后循环把 `Status`
        //     覆盖回 `Running`，于是用户被告知已取消，磁盘却在被写几个小时。
        //  ② **500 而不是取消**：循环收尾时把 `Cancellation` 置 null 并释放 CTS，
        //     这里若在读到它之后才调 `Cancel()`，拿到的是**已释放**的 CTS →
        //     `ObjectDisposedException` 从 HTTP 路径抛出（`CancelForTaskAsync` 的循环还会因此
        //     中断整批取消）。把 `Cancel()` 放在锁内，是为了让"读到 CTS"与"释放 CTS"不可能交错；
        //     取消回调不会回头抢这把锁，所以不存在死锁环。
        lock (_gate)
        {
            switch (entry.Status)
            {
                case JobStatus.Queued:
                    entry.Status = JobStatus.Aborted;
                    entry.Error = "已取消（尚未开始）";
                    entry.EndedAtUtc = clock.UtcNow;
                    break;

                case JobStatus.Running:
                    entry.Cancellation?.Cancel();
                    return Task.FromResult(true);

                default:
                    return Task.FromResult(false);
            }
        }

        // 发布放在锁外：`PublishAsync` 是异步的，不该在持锁时启动。
        _ = TryPublishAsync("job.aborted", new { jobId, reason = "已取消（尚未开始）" }, entry);
        return Task.FromResult(true);
    }

    public async Task<bool> CancelForTaskAsync(Guid taskId, CancellationToken ct)
    {
        bool any = false;
        foreach (JobEntry entry in _jobs.Values.Where(j =>
                     j.Request.TaskId == taskId &&
                     j.Status is JobStatus.Queued or JobStatus.Running))
        {
            any |= await CancelAsync(entry.Id, ct).ConfigureAwait(false);
        }

        return any;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
            {
                // 宿主正在关闭，尽力而为
            }
        }

        _loopCts?.Dispose();
    }

    // ------------------------------------------------------------------

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (JobEntry entry in _channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

                lock (_gate)
                {
                    if (entry.Status == JobStatus.Aborted)
                    {
                        // 已被 CancelAsync 判为"取消（尚未开始）"，那条路径已经发过 job.aborted
                        continue;
                    }

                    entry.Cancellation = linked;
                    entry.Status = JobStatus.Running;
                    entry.StartedAtUtc = clock.UtcNow;
                }

                logger.LogInformation("作业 {Id} 开始：{Description}", entry.Id, entry.Request.Description);

                // **`job.started` 的发布必须在 try 里面。**
                // 它原来在 try 之外，一旦抛出（OCE、或任何发布器实现抛出的异常）就会从
                // `await foreach` 里直接逃出去——**唯一的消费循环就此永久死亡**，
                // 而 `Status` 已置 `Running`、`Cancellation` 也在（上面那个 lock 里刚写），
                // 于是 `IsBusy` 永远为 true：此后**所有**备份/还原都排不进来，
                // 该作业永远"运行中"，用户点取消还会打到已释放的 CTS 上。
                try
                {
                    await TryPublishAsync("job.started", new
                    {
                        jobId = entry.Id,
                        type = entry.Request.Type.ToString(),
                        taskId = entry.Request.TaskId,
                    }, entry).ConfigureAwait(false);

                    await ExecuteAsync(entry, linked.Token).ConfigureAwait(false);

                    entry.Status = JobStatus.Completed;
                    entry.EndedAtUtc = clock.UtcNow;

                    // 状态先定下来，再发布。顺序反过来（原来就是这样）会有一个很坏的后果：
                    // `job.completed` 一旦抛异常就会落进下面的通用 catch，把一次**已经提交成功**的
                    // 备份改写成 `Failed`——库里 Completed、界面 Failed，用户据此重跑一次全量备份，
                    // 或去排查一个不存在的故障。`TryPublishAsync` 也不抛，双保险。
                    await TryPublishAsync("job.completed", new
                    {
                        jobId = entry.Id,
                        result = entry.BackupResult ?? (object?)entry.RestoreResult ?? entry.GcResult
                                 ?? (object?)entry.IntegrityReport,
                    }, entry).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested
                                                         || entry.BackupResult?.Canceled == true)
                {
                    entry.Status = JobStatus.Aborted;
                    entry.Error = "已取消";
                    entry.EndedAtUtc = clock.UtcNow;

                    await TryPublishAsync("job.aborted", new { jobId = entry.Id, reason = "已取消" }, entry)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    entry.Status = JobStatus.Failed;
                    entry.Error = $"{ex.GetType().Name}: {ex.Message}";
                    entry.EndedAtUtc = clock.UtcNow;

                    logger.LogError(ex, "作业 {Id} 失败。", entry.Id);
                    await TryPublishAsync("job.failed", new { jobId = entry.Id, error = entry.Error }, entry)
                        .ConfigureAwait(false);
                }
                finally
                {
                    // `Cancellation` 的置空与 CTS 的释放必须**在同一把锁内**完成：
                    // 这样 `CancelAsync` 要么在释放前拿到活着的 CTS（在锁内调 `Cancel()`），
                    // 要么看到 null 而什么都不做——不可能拿到已释放的实例。
                    // `linked.Dispose()` 幂等，`using` 作用域结束时再调一次无副作用。
                    lock (_gate)
                    {
                        entry.Cancellation = null;
                        linked.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭
        }
    }

    /// <summary>
    /// 发布一个作业事件，**永不抛出**。
    /// </summary>
    /// <remarks>
    /// 发布失败绝不能影响作业本身或消费循环，两种坏结果都已经真实存在过：
    /// <list type="bullet">
    /// <item>让异常在 <c>RunLoopAsync</c> 的 <c>await foreach</c> 里逃出去，会**永久杀死唯一的
    /// 消费循环**（<c>IsBusy</c> 停在 true，此后所有作业都排不进来）；</item>
    /// <item>让 <c>job.completed</c> 的失败落进通用 catch，会把一次**已经提交成功**的备份
    /// 改写成 <c>Failed</c>——库里 Completed、界面 Failed。</item>
    /// </list>
    /// 这两种都是把"实时通道不通"升级成"数据面坏掉"，代价远大于丢一条通知。
    /// 所以这里一律降级为 Warning：前端本来就有 REST 兜底（<c>GET /api/jobs</c> 与
    /// 重连后的全量重取），少一条推送不影响正确性。
    /// </remarks>
    private async Task TryPublishAsync(string eventName, object payload, JobEntry entry)
    {
        try
        {
            await events.PublishAsync(eventName, payload, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "发布作业事件 {Event}（作业 {Id}）失败，已忽略。", eventName, entry.Id);
        }
    }

    private async Task ExecuteAsync(JobEntry entry, CancellationToken ct)
    {
        JobRequest request = entry.Request;
        var progress = new ThrottledProgress(entry, events, clock);

        switch (request.Type)
        {
            case JobType.Backup:
                entry.BackupResult = await backup.RunAsync(
                    request.TaskId!.Value, request.BackupType, request.DirtyScopes, progress, ct)
                    .ConfigureAwait(false);

                // **不能只看"有没有抛异常"。** 引擎把用户取消吞掉了：它把快照置为 Aborted 后
                // 返回一个普通结果（09 B6 要求如此，好让"快照已作废"这个事实落库）。
                // 不看这两个分支的话：用户点了取消，快照列表显示"已作废"，
                // 而 `GET /api/jobs` 与事件流都显示这个作业 **Completed**——
                // 只有 SignalR 负载里的 success:false 能看出问题，任谁去查都会先怀疑快照那边。
                if (entry.BackupResult.Canceled)
                {
                    // 抛 OCE 让下面 `when (linked.IsCancellationRequested)` 的分支接住，
                    // 作业于是被正确标成 Aborted 并发出 job.aborted。
                    throw new OperationCanceledException("已取消");
                }

                if (entry.BackupResult.Status == SnapshotStatus.Aborted)
                {
                    // 非取消的中止（失败率超阈值 / 连续失败 / 异常）应当体现为**失败**，
                    // 并带上真正的原因，而不是一个语焉不详的 Completed。
                    throw new InvalidOperationException(
                        entry.BackupResult.AbortReason ?? "备份被中止。");
                }

                break;

            case JobType.Restore:
                entry.RestoreResult = await restore.ExecuteAsync(request.RestorePlan!, progress, ct)
                    .ConfigureAwait(false);
                break;

            case JobType.Verify:
                entry.IntegrityReport = await integrity
                    .VerifyAsync(request.TaskId!.Value, request.DeepHashCheck, ct)
                    .ConfigureAwait(false);
                break;

            case JobType.Collect:
                entry.GcResult = await integrity.CollectAsync(request.TaskId!.Value, null, ct).ConfigureAwait(false);
                break;

            default:
                throw new NotSupportedException($"不支持的作业类型：{request.Type}");
        }
    }

    private int? PositionOf(string jobId)
    {
        int position = 0;
        foreach (string id in _order)
        {
            if (string.Equals(id, jobId, StringComparison.Ordinal))
            {
                return position;
            }

            if (_jobs.TryGetValue(id, out JobEntry? e) && e.Status == JobStatus.Queued)
            {
                position++;
            }
        }

        return null;
    }

    private JobView ToView(JobEntry entry) => new(
        entry.Id,
        entry.Request.Type,
        entry.Status,
        entry.Request.Description,
        entry.Request.TaskId,
        entry.Request.SnapshotId,
        entry.EnqueuedAtUtc,
        entry.StartedAtUtc,
        entry.EndedAtUtc,
        entry.Status == JobStatus.Queued ? PositionOf(entry.Id) : null,
        entry.Progress,
        entry.Error,
        entry.BackupResult,
        entry.RestoreResult,
        entry.GcResult,
        entry.IntegrityReport);

    private void TrimHistory()
    {
        while (_order.Count > HistoryLimit)
        {
            if (!_order.TryDequeue(out string? oldest))
            {
                return;
            }

            if (_jobs.TryGetValue(oldest, out JobEntry? entry) &&
                entry.Status is JobStatus.Running or JobStatus.Queued)
            {
                // 还在跑，放回队尾，等它结束
                _order.Enqueue(oldest);
                return;
            }

            _jobs.TryRemove(oldest, out _);
        }
    }

    private sealed class JobEntry
    {
        public required string Id { get; init; }

        public required JobRequest Request { get; init; }

        public JobStatus Status { get; set; }

        public DateTime EnqueuedAtUtc { get; init; }

        public DateTime? StartedAtUtc { get; set; }

        public DateTime? EndedAtUtc { get; set; }

        public JobProgress Progress { get; set; }

        public string? Error { get; set; }

        public BackupRunResult? BackupResult { get; set; }

        public RestoreResult? RestoreResult { get; set; }

        public GcResult? GcResult { get; set; }

        public IntegrityReport? IntegrityReport { get; set; }

        public CancellationTokenSource? Cancellation { get; set; }
    }

    /// <summary>把进度写进作业记录，并按节流发 SignalR 事件。</summary>
    private sealed class ThrottledProgress(JobEntry entry, IJobEventPublisher events, IClock clock)
        : IProgress<JobProgress>
    {
        private readonly JobEntry _entry = entry;
        private readonly IJobEventPublisher _events = events;
        private readonly IClock _clock = clock;
        private DateTime _lastPublish = DateTime.MinValue;

        public void Report(JobProgress value)
        {
            _entry.Progress = value;

            DateTime now = _clock.UtcNow;
            if (now - _lastPublish < ProgressThrottle)
            {
                return;
            }

            _lastPublish = now;

            string eventName = _entry.Request.Type == JobType.Restore ? "restore.progress" : "backup.progress";
            _ = _events.PublishAsync(eventName, new
            {
                jobId = _entry.Id,
                phase = value.Phase.ToString(),
                discovered = value.Discovered,
                processed = value.Processed,
                total = value.Total,
                currentPath = value.CurrentPath,
                bytesDone = value.BytesDone,
                bytesTotal = value.BytesTotal,
            }, CancellationToken.None);
        }
    }
}
