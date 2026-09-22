using FileBackupTool.Core.Entities;

namespace FileBackupTool.Core.Abstractions;

/// <summary>抢救清单的写入结果。</summary>
public readonly record struct ManifestWriteResult(
    string Layout,
    int ShardCount,
    long FileCount,
    long TotalBytes,
    /// <summary>
    /// replay 当前镜像时发现的结构性不一致条数（Created 撞已存在、Modified/Deleted 找不到目标）。
    /// 旧设计用 <c>Debug.Assert(false)</c> 当唯一守卫——Release 下被编译掉，损坏静默通过。
    /// </summary>
    int MirrorInconsistencies);

/// <summary>
/// 抢救清单写入器。设计文档 02 §6。
/// </summary>
/// <remarks>
/// <b>目标：不需要本软件，使用者就能据此分析出应当如何还原。</b>
/// 因此格式必须是纯文本、人可读、任何工具可解析——排除 SQLite 导出（需要工具、有版本兼容问题），
/// 也**不压缩**（压缩会失去"用记事本打开就能读"这一唯一的抢救属性）。
/// </remarks>
public interface IManifestWriter
{
    /// <summary>
    /// 重写受影响的清单分片。
    /// </summary>
    /// <param name="task">任务。</param>
    /// <param name="latestSnapshot">刚完成的快照（提供镜像规模与链尾）。</param>
    /// <param name="touchedRelativePaths">
    /// 本次快照涉及的相对路径（新增/修改/删除），用于推断哪些分片需要重写。
    /// </param>
    Task<ManifestWriteResult> WriteAsync(
        BackupTask task,
        Entities.BackupSnapshot latestSnapshot,
        IReadOnlyCollection<string> touchedRelativePaths,
        CancellationToken ct);
}

/// <summary>不生成清单的实现（用于单元测试与不需要清单的场景）。</summary>
public sealed class NullManifestWriter : IManifestWriter
{
    public static readonly NullManifestWriter Instance = new();

    public Task<ManifestWriteResult> WriteAsync(
        BackupTask task,
        Entities.BackupSnapshot latestSnapshot,
        IReadOnlyCollection<string> touchedRelativePaths,
        CancellationToken ct)
        => Task.FromResult(new ManifestWriteResult("none", 0, 0, 0, 0));
}
