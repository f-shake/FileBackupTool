using FileBackupTool.Api.Hubs;
using FileBackupTool.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Api.Services;

/// <summary>
/// <see cref="IBackupLogSink"/> 的装饰器：落库之后顺手把这一行推给实时通道
/// （设计文档 06 §5 的 <c>log.appended</c>）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么用装饰器而不是在各处手动发事件</b>：业务日志的写入点是引擎内部（逐文件失败、
/// 清单生成、GC…）。让那些地方既写日志又发 SignalR 事件，等于把"实时通道"这个宿主关注点
/// 塞进领域代码。装饰器把它收敛到唯一入口：**凡是进了 BackupLog 表的一行，就一定推送**，
/// 不存在"某条路径忘了推"的可能。
/// </para>
/// <para>
/// <b>序列</b>：先落库、后推送。反过来的话，前端可能会先收到一条在库里还查不到的行
/// （用户点"刷新"就消失了），那比晚几百毫秒糟得多。
/// </para>
/// </remarks>
public sealed class RealtimeLogSink(
    IBackupLogSink inner,
    SignalRJobEventPublisher events,
    IClock clock,
    ILogger<RealtimeLogSink> logger) : IBackupLogSink
{
    private readonly IBackupLogSink _inner = inner;
    private readonly SignalRJobEventPublisher _events = events;
    private readonly IClock _clock = clock;
    private readonly ILogger<RealtimeLogSink> _logger = logger;

    public async Task WriteAsync(
        Guid taskId,
        int? snapshotId,
        LogLevel level,
        string message,
        string? detail,
        CancellationToken ct)
    {
        await _inner.WriteAsync(taskId, snapshotId, level, message, detail, ct).ConfigureAwait(false);

        try
        {
            await _events.LogAppendedAsync(taskId, snapshotId, level.ToString(), _clock.UtcNow, message, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 推送失败只影响"实时看到"，不影响日志本身是否落库。
            _logger.LogDebug(ex, "推送 log.appended 失败（日志已落库，前端轮询仍能看到）。");
        }
    }
}
