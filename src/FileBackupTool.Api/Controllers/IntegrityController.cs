using System.Text.RegularExpressions;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 完整校验与 blob 回收（设计文档 06 §4.6、04 §2.4、02 §5）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么这两个是同步端点，而备份/还原是入队的</b>：前端契约就是这么定的
/// （<c>verifyTask()</c> 直接 <c>Promise&lt;IntegrityReport&gt;</c>，没有作业句柄），
/// 而更实际的理由是——校验与回收**必须与备份串行**，但用户点下之后就在等结果。
/// 让它们走队列会引入"作业排队中"这个用户看不懂的中间态（他并没有排任何东西）。
/// 所以这里**同步执行 + 显式拒绝并发**：队列非空时直接回 <c>JOB_QUEUE_BUSY</c>，
/// 而不是偷偷与一个正在跑的备份抢机械盘，或在队列里排一条看不到的作业。
/// </para>
/// <para>
/// 代价是这类请求会占用一个请求线程直到跑完（大池下可能几十秒）。
/// 这是刻意的取舍：单用户系统，且同一时刻只有一个人在操作。
/// </para>
/// </remarks>
[Route("api/tasks/{id:guid}")]
public sealed class IntegrityController(
    IIntegrityService integrity,
    SnapshotChainService chains,
    TaskStore tasks,
    BlobStoreFactory blobStores,
    IJobQueue queue,
    IBackupLogSink logSink,
    AuditLogger audit,
    SettingsService settings,
    IPathNormalizer normalizer,
    ILogger<IntegrityController> logger) : ApiControllerBase
{
    /// <summary>返回体里各清单的条数上限。超出部分只在 <c>summary</c> 里报数。</summary>
    private const int MaxListedBlobs = 1000;

    /// <summary>统计"多余文件占用字节"时的 stat 次数上限（见 <see cref="OrphanBytes"/>）。</summary>
    private const int MaxOrphanStats = 50_000;

    private readonly IIntegrityService _integrity = integrity;
    private readonly SnapshotChainService _chains = chains;
    private readonly TaskStore _tasks = tasks;
    private readonly BlobStoreFactory _blobStores = blobStores;
    private readonly IJobQueue _queue = queue;
    private readonly IBackupLogSink _logSink = logSink;
    private readonly AuditLogger _audit = audit;
    private readonly SettingsService _settings = settings;
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly ILogger<IntegrityController> _logger = logger;

    /// <summary>完整校验：多余文件 / 丢失文件 / 链断裂（可选深度哈希校验）。</summary>
    [HttpPost("verify")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IntegrityReportDto> VerifyAsync(
        [FromRoute] Guid id,
        [FromBody] VerifyRequestDto? input)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);
        RequireQueueIdle(task, "完整校验");

        bool deep = input?.DeepHashCheck ?? false;

        // 未显式给出时取全局设置里的默认值（04 §2.3 的"完整校验默认是否重算哈希"）。
        // 注意这**不是**"默认开启"：设置项自己的默认就是 false。
        if (input?.DeepHashCheck is null)
        {
            deep = await _settings.GetVerifyDeepHashByDefaultAsync(Ct).ConfigureAwait(false);
        }

        if (deep)
        {
            // 深度校验会逐个读完整 blob，在 TB 级下是小时级操作。留一条显著的日志：
            // 用户之后问"为什么磁盘一直在响"时，答案要能在日志里找到。
            _logger.LogWarning("任务「{Name}」的完整校验将重算全部 blob 哈希（深度校验）。", task.Name);
        }

        IntegrityReport report = await _integrity.VerifyAsync(id, deep, Ct).ConfigureAwait(false);

        (long orphanBytes, bool statsTruncated) = OrphanBytes(task, report.RedundantBlobs);

        string summary = BuildSummary(report, orphanBytes, statsTruncated);

        await _logSink.WriteAsync(
            id,
            null,
            report.IsHealthy ? LogLevel.Information : LogLevel.Warning,
            report.IsHealthy ? "完整校验通过。" : "完整校验发现问题。",
            summary,
            Ct).ConfigureAwait(false);

        return new IntegrityReportDto
        {
            TaskId = report.TaskId,
            CheckedAt = DateTime.UtcNow,
            DeepHashChecked = deep,
            OrphanBlobs = ToRefs(report.RedundantBlobs),
            OrphanBytes = orphanBytes,
            MissingBlobs = ToRefs(report.LostBlobs),
            BrokenChains = await ToBrokenChainsAsync(id, report.BrokenChains).ConfigureAwait(false),
            CorruptBlobs = ToRefs(report.CorruptedBlobs),

            // 引擎当前**不产出**镜像不一致明细（IntegrityReport 的该字段恒为空数组，
            // 而备份时发现的计数记在 BackupSnapshot.MirrorInconsistencyCount 上）。
            // 如实返回空清单，而不是拿那个计数编一条含糊的说明。
            MirrorInconsistencies = [.. report.MirrorInconsistencies],
            CheckedBlobCount = report.CheckedBlobCount,
            CheckedRecordCount = report.CheckedRecordCount,
            Healthy = report.IsHealthy,
            Summary = summary,
        };
    }

    /// <summary>
    /// 回收：把无任何引用（含软删除记录）的 blob 移入回收站。
    /// </summary>
    /// <remarks>
    /// 这里是**全池**回收（<c>scopeHashes: null</c>）——增量回收是备份流程的收尾步骤，
    /// 由引擎在备份结束时自己发起（03 §8），不该由这个按钮做。
    /// </remarks>
    [HttpPost("collect")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<GcResultDto> CollectAsync([FromRoute] Guid id)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);
        RequireQueueIdle(task, "回收");

        GcResult result = await _integrity.CollectAsync(id, scopeHashes: null, Ct).ConfigureAwait(false);

        RecycleBinStatus bin = await _integrity.GetRecycleBinAsync(id, Ct).ConfigureAwait(false);

        string detail =
            $"移入回收站 {result.RecycledBlobCount} 个文件块（{result.RecycledBytes} 字节）；" +
            $"回收站现有 {bin.BlobCount} 个文件块、共 {bin.TotalBytes} 字节（**空间尚未释放**）。";

        await _logSink.WriteAsync(
            id, null,
            result.LostBlobs.Count > 0 ? LogLevel.Warning : LogLevel.Information,
            result.LostBlobs.Count > 0
                ? $"回收完成，但发现 {result.LostBlobs.Count} 个丢失的文件块。"
                : "回收完成。",
            detail,
            Ct).ConfigureAwait(false);

        if (result.LostBlobs.Count > 0)
        {
            // 回收是唯一会"顺路发现丢失 blob"的日常操作，这条必须显眼：
            // 丢失意味着还原会失败，而用户点的是"整理文件"，不会预期看到坏消息。
            _logger.LogError("任务「{Name}」回收时发现 {Count} 个 blob 既不在池中也不在回收站。",
                task.Name, result.LostBlobs.Count);
        }

        return new GcResultDto(
            // 同步执行，没有作业句柄。前端按可空处理。
            JobId: null,
            result.RecycledBlobCount,
            result.RecycledBytes,
            bin.TotalBytes);
    }

    /// <summary>回收站占用与账本（分页）。</summary>
    [HttpGet("recycle-bin")]
    public async Task<RecycleBinInfoDto> GetRecycleBinAsync(
        [FromRoute] Guid id,
        [FromQuery] int? page,
        [FromQuery] int? pageSize)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        (int p, int size) = PageQuery.Normalize(page, pageSize);

        RecycleBinStatus bin = await _integrity.GetRecycleBinAsync(id, Ct).ConfigureAwait(false);

        // 账本是单个 JSON，条目数远小于 blob 池（02 §5.2），所以在内存里分页足够。
        RecycleLedgerEntry[] window =
        [
            .. bin.Entries
                .OrderByDescending(e => e.MovedAtUtc)
                .Skip((p - 1) * size)
                .Take(size),
        ];

        PhysicalBlobStore store = _blobStores.Create(task.BackupDir);

        return new RecycleBinInfoDto
        {
            BlobCount = bin.BlobCount,
            TotalBytes = bin.TotalBytes,
            Entries =
            [
                .. window.Select(e => new RecycleBinEntryDto(
                    e.Hash, e.Length, e.MovedAtUtc, e.LastPath)),
            ],
            EntriesTotal = bin.Entries.Count,
            EntriesPage = p,
            EntriesPageSize = size,
            LedgerPath = _normalizer.Combine(store.RecycleBinRoot, PhysicalBlobStore.LedgerFileName),
        };
    }

    /// <summary>
    /// 清空回收站（**硬删除，不可逆**）。前端负责二次确认；服务端只认这一次明确的调用。
    /// </summary>
    [HttpDelete("recycle-bin")]
    [EnableRateLimiting(RateLimitPolicies.Write)]
    public async Task<IActionResult> ClearRecycleBinAsync([FromRoute] Guid id)
    {
        BackupTask task = await RequireTaskAsync(_tasks, id).ConfigureAwait(false);

        // 清空是纯删除操作，不读源目录也不写 blob 池；但它会与正在跑的备份/回收争盘，
        // 所以同样要求队列空闲。
        RequireQueueIdle(task, "清空回收站");

        RecycleBinStatus before = await _integrity.GetRecycleBinAsync(id, Ct).ConfigureAwait(false);

        await _integrity.ClearRecycleBinAsync(id, Ct).ConfigureAwait(false);

        string detail =
            $"硬删除回收站内的 {before.BlobCount} 个文件块，释放 {before.TotalBytes} 字节。**不可撤销。**";

        await _audit.WriteAsync(
            AuditEventType.RecycleBinCleared,
            target: task.Name,
            detail: detail,
            ct: Ct).ConfigureAwait(false);

        await _logSink.WriteAsync(id, null, LogLevel.Warning, "回收站已被清空。", detail, Ct)
            .ConfigureAwait(false);

        return NoContent();
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 队列必须空闲，否则显式拒绝。
    /// </summary>
    /// <remarks>
    /// 这两个操作都会遍历 blob 池（几十万次 <c>stat</c> / 读取），而备份正在写同一批文件。
    /// 不拒绝的话，最坏情况下用户会看到"校验报丢失"——因为 blob 正处在"临时文件已提交、
    /// 目录项还没刷新"的窗口里。所以宁可让用户等一会儿，也不要给出一个可能错误的结论。
    /// </remarks>
    private void RequireQueueIdle(BackupTask task, string action)
    {
        if (_queue.IsBusy)
        {
            throw ApiException.Conflict(
                ApiErrorCodes.JobQueueBusy,
                $"当前有作业正在运行或排队，无法执行{action}。",
                "完整校验与回收都会遍历整个 blob 池，与备份并发会争抢磁盘并且可能得出错误结论。请等作业结束后重试。");
        }

        _logger.LogInformation("任务「{Name}」开始执行{Action}。", task.Name, action);
    }

    /// <summary>
    /// 多余文件占用的总字节数。
    /// </summary>
    /// <remarks>
    /// 引擎的 <see cref="IntegrityReport"/> 只给出哈希，没有字节数——补这个数要逐个
    /// <c>stat</c>。所以设了 <see cref="MaxOrphanStats"/> 的上限：超过就不数了，
    /// 返回已统计的部分并由 <c>summary</c> 明确说明"这只是下界"。
    /// 编一个精确数字出来比给个下界糟得多，用户会拿它去判断"能腾出多少空间"。
    /// </remarks>
    private (long Bytes, bool Truncated) OrphanBytes(BackupTask task, IReadOnlyList<string> orphanHashes)
    {
        if (orphanHashes.Count == 0)
        {
            return (0, false);
        }

        PhysicalBlobStore store = _blobStores.Create(task.BackupDir);
        long bytes = 0;
        int counted = Math.Min(orphanHashes.Count, MaxOrphanStats);

        for (int i = 0; i < counted; i++)
        {
            bytes += store.GetLength(orphanHashes[i]);
        }

        return (bytes, orphanHashes.Count > counted);
    }

    private static BlobRefDto[] ToRefs(IReadOnlyList<string> hashes) =>
    [
        .. hashes
            .Take(MaxListedBlobs)

            // 引擎只给哈希：长度要额外 stat、引用路径与快照要额外查库。
            // 在"一次请求内校验百万 blob"的场景里，为每条清单多做两次 IO 不划算，
            // 所以这里如实留空，而不是填 0 假装知道。
            .Select(h => new BlobRefDto(h, Length: null, ReferencedPath: null, SnapshotId: null)),
    ];

    /// <summary>
    /// 链断裂清单。
    /// </summary>
    /// <remarks>
    /// 引擎给的是**人话字符串**（形如 <c>快照 #12（2026-09-01 10:00:00Z）找不到其所属的全量快照…</c>），
    /// 而前端契约要的是结构化的 <c>{ snapshotId, type, reason }</c>。
    /// 这里把快照号从文案里解析出来，再回查数据库补上类型——
    /// 那一句里的编号是引擎自己 <c>$"… #{snapshot.Id} …"</c> 拼出来的，格式稳定。
    /// <c>expectedPreviousSnapshotId</c> 保持为空：链解析失败意味着"算不出上一个是谁"，
    /// 硬凑一个数字出来只会误导排查。
    /// </remarks>
    private async Task<IReadOnlyList<BrokenChainDto>> ToBrokenChainsAsync(
        Guid taskId,
        IReadOnlyList<string> reasons)
    {
        var result = new List<BrokenChainDto>(reasons.Count);

        foreach (string reason in reasons)
        {
            int snapshotId = 0;
            string type = "Unknown";

            Match match = SnapshotIdPattern.Match(reason);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int parsed))
            {
                snapshotId = parsed;

                BackupSnapshot? snapshot = await _chains.GetAsync(parsed, Ct).ConfigureAwait(false);
                if (snapshot is not null && snapshot.TaskId == taskId)
                {
                    type = snapshot.Type.ToString();
                }
            }

            result.Add(new BrokenChainDto(snapshotId, type, reason, ExpectedPreviousSnapshotId: null));
        }

        return result;
    }

    private static string BuildSummary(IntegrityReport report, long orphanBytes, bool statsTruncated)
    {
        var sb = new System.Text.StringBuilder();

        sb.Append($"校验了 {report.CheckedBlobCount} 个文件块与 {report.CheckedRecordCount} 条文件记录：");
        sb.Append($"多余 {report.RedundantBlobs.Count}（约 {orphanBytes} 字节{(statsTruncated ? "，且只统计了前 50000 个，实际更多" : string.Empty)}）、");
        sb.Append($"丢失 {report.LostBlobs.Count}、链断裂 {report.BrokenChains.Count}、");
        sb.Append($"哈希不符 {report.CorruptedBlobs.Count}、镜像不一致 {report.MirrorInconsistencies.Count}。");

        if (report.LostBlobs.Count > 0)
        {
            sb.Append("**丢失的文件块会导致还原失败，请优先处理。**");
        }

        int truncatedLists = new[]
        {
            report.RedundantBlobs.Count, report.LostBlobs.Count, report.CorruptedBlobs.Count,
        }.Count(c => c > MaxListedBlobs);

        if (truncatedLists > 0)
        {
            sb.Append($"清单只列出每类的前 {MaxListedBlobs} 条（共 {truncatedLists} 类被截断）。");
        }

        return sb.ToString();
    }

    /// <summary>从引擎的断裂说明里取快照号。<c>#</c> 后紧跟数字。</summary>
    private static readonly Regex SnapshotIdPattern = new(@"#\s*(\d+)", RegexOptions.Compiled);
}
