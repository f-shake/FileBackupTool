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

        _ = events.PublishAsync("job.queued", new
        {
            jobId = entry.Id,
            type = request.Type.ToString(),
            position = PositionOf(entry.Id),
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

        switch (entry.Status)
        {
            case JobStatus.Queued:
                entry.Status = JobStatus.Aborted;
                entry.Error = "已取消（尚未开始）";
                entry.EndedAtUtc = clock.UtcNow;
                _ = events.PublishAsync("job.aborted", new { jobId, reason = "已取消（尚未开始）" },
                    CancellationToken.None);
                return Task.FromResult(true);

            case JobStatus.Running:
                entry.Cancellation?.Cancel();
                return Task.FromResult(true);

            default:
                return Task.FromResult(false);
        }
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
                if (entry.Status == JobStatus.Aborted)
                {
                    continue;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                entry.Cancellation = linked;
                entry.Status = JobStatus.Running;
                entry.StartedAtUtc = clock.UtcNow;

                logger.LogInformation("作业 {Id} 开始：{Description}", entry.Id, entry.Request.Description);
                await events.PublishAsync("job.started", new
                {
                    jobId = entry.Id,
                    type = entry.Request.Type.ToString(),
                    taskId = entry.Request.TaskId,
                }, CancellationToken.None).ConfigureAwait(false);

                try
                {
                    await ExecuteAsync(entry, linked.Token).ConfigureAwait(false);
                    entry.Status = JobStatus.Completed;
                    entry.EndedAtUtc = clock.UtcNow;

                    await events.PublishAsync("job.completed", new
                    {
                        jobId = entry.Id,
                        result = entry.BackupResult ?? (object?)entry.RestoreResult ?? entry.GcResult
                                 ?? (object?)entry.IntegrityReport,
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    entry.Status = JobStatus.Aborted;
                    entry.Error = "已取消";
                    entry.EndedAtUtc = clock.UtcNow;

                    await events.PublishAsync("job.aborted", new { jobId = entry.Id, reason = "已取消" },
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    entry.Status = JobStatus.Failed;
                    entry.Error = $"{ex.GetType().Name}: {ex.Message}";
                    entry.EndedAtUtc = clock.UtcNow;

                    logger.LogError(ex, "作业 {Id} 失败。", entry.Id);
                    await events.PublishAsync("job.failed", new { jobId = entry.Id, error = entry.Error },
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    entry.Cancellation = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 宿主关闭
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
