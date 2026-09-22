using Microsoft.Extensions.Logging;

namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 业务日志落库。设计文档 06 §7：业务日志写入 <c>BackupLog</c> 表（按任务/快照关联），
/// 同时输出到 Serilog 文件 sink。
/// </summary>
public interface IBackupLogSink
{
    Task WriteAsync(
        Guid taskId,
        int? snapshotId,
        LogLevel level,
        string message,
        string? detail,
        CancellationToken ct);
}
