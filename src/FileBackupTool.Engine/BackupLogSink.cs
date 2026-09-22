using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Data;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine;

/// <summary>
/// 把业务日志同时写进 <c>BackupLog</c> 表与 Serilog。
/// </summary>
/// <remarks>
/// **写日志失败绝不能影响备份本身**——所以这里吞掉所有异常，
/// 只在 Serilog 里留一条痕迹。备份路径上还处在"正在失败"的状态时尤其重要
/// （例如磁盘满的时候，日志写入本身也会失败）。
/// </remarks>
public sealed class BackupLogSink(LogStore store, IClock clock, ILogger<BackupLogSink> logger) : IBackupLogSink
{
    private readonly LogStore _store = store;
    private readonly IClock _clock = clock;
    private readonly ILogger<BackupLogSink> _logger = logger;

    public async Task WriteAsync(
        Guid taskId,
        int? snapshotId,
        LogLevel level,
        string message,
        string? detail,
        CancellationToken ct)
    {
        try
        {
            await _store.WriteAsync(new BackupLog
            {
                TaskId = taskId,
                SnapshotId = snapshotId,
                Level = level,
                Time = _clock.UtcNow,
                Message = Truncate(message, 4000),
                Detail = detail,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "写入业务日志失败：{Message}", message);
        }
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
