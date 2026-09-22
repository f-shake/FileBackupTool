using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;

namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 备份引擎。设计文档 06 §3。
/// </summary>
/// <remarks>
/// 设计文档里的方法签名用了 <c>SnapshotId</c> / <c>BackupTaskId</c> 包装类型；
/// 这里直接用 <c>int</c> / <c>Guid</c>——少一层装箱与转换，减少 bug 面。
/// <para>
/// **所有公开方法必须带 <see cref="CancellationToken"/>**：旧设计的 CT 是私有字段，
/// 公开 API 上没有这个参数（<c>CheckAndBackupAllAsync</c> 直接调用还会 NRE）。
/// </para>
/// </remarks>
public interface IBackupService
{
    /// <summary>
    /// 执行一次备份。
    /// </summary>
    /// <param name="taskId">任务 id。</param>
    /// <param name="requestedType">手动指定类型；null 表示按全量/增量策略自动决定。</param>
    /// <param name="dirtyRelativePaths">
    /// 局部扫描的脏目录（**源目录相对路径**）；null / 空表示全量扫描。
    /// 设计文档 03 §5.4：只重扫变化目录，未变化目录从上个镜像直接继承。
    /// </param>
    /// <param name="progress">进度回调（两段式，见 <see cref="JobProgress"/>）。</param>
    Task<BackupRunResult> RunAsync(
        Guid taskId,
        SnapshotType? requestedType,
        IReadOnlyList<string>? dirtyRelativePaths,
        IProgress<JobProgress>? progress,
        CancellationToken ct);
}

/// <summary>还原。设计文档 04 §1——**这是纯新增功能**，ArchiveMaster 根本没有还原。</summary>
public interface IRestoreService
{
    /// <summary>预演：列出将写哪些文件、冲突有哪些、哪些不可恢复。</summary>
    Task<RestorePlan> PlanAsync(RestoreRequest request, CancellationToken ct);

    /// <summary>执行。调用方负责把它投进全局作业队列。</summary>
    Task<RestoreResult> ExecuteAsync(
        RestorePlan plan,
        IProgress<JobProgress>? progress,
        CancellationToken ct);
}

/// <summary>镜像浏览与文件历史。设计文档 02 §3.1：**不把整条链 replay 进内存**。</summary>
public interface IMirrorQueryService
{
    /// <summary>列出某个快照镜像中某个目录下的条目（服务端分页）。</summary>
    Task<PagedResult<MirrorEntry>> ListDirectoryAsync(
        Guid taskId,
        int snapshotId,
        string relativePath,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>目录树骨架（用于导航，不含文件）。</summary>
    Task<IReadOnlyList<string>> ListDirectoryTreeAsync(
        Guid taskId,
        int snapshotId,
        string relativePath,
        CancellationToken ct);

    /// <summary>某个路径的全部版本。</summary>
    Task<IReadOnlyList<FileVersionEntry>> GetFileHistoryAsync(
        Guid taskId,
        string relativePath,
        CancellationToken ct);

    /// <summary>在指定快照的镜像中按文件名搜索（真过滤，不是"展开+多选"）。</summary>
    Task<PagedResult<MirrorEntry>> SearchAsync(
        Guid taskId,
        int snapshotId,
        string keyword,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>某个快照的变更内容（Created / Modified / Deleted 三类）。</summary>
    Task<PagedResult<MirrorEntry>> ListChangesAsync(
        int snapshotId,
        FileRecordType? type,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>单文件元数据。</summary>
    Task<MirrorEntry?> GetFileAsync(int snapshotId, string relativePath, CancellationToken ct);
}

/// <summary>快照管理。设计文档 04 §2。</summary>
public interface ISnapshotService
{
    /// <summary>按全量快照分组的列表。</summary>
    Task<PagedResult<SnapshotGroup>> ListAsync(
        Guid taskId,
        SnapshotType? typeFilter,
        DateTime? fromUtc,
        DateTime? toUtc,
        int page,
        int pageSize,
        CancellationToken ct);

    /// <summary>级联删除影响清单。**删除前必须先调它**，对话框要列出全部受影响的快照。</summary>
    Task<DeleteImpact?> PreviewDeleteAsync(int snapshotId, CancellationToken ct);

    /// <summary>软删除（级联）。物理空间不立即释放——blob 只在无任何记录引用时才移入回收站。</summary>
    Task DeleteAsync(int snapshotId, CancellationToken ct);
}

/// <summary>完整校验与 blob 回收。设计文档 03 §8 / 04 §2.4。</summary>
public interface IIntegrityService
{
    /// <summary>交叉比对 blob 池与数据库记录，报告多余文件与丢失文件，并校验链路连续性。</summary>
    Task<IntegrityReport> VerifyAsync(Guid taskId, bool deepHashCheck, CancellationToken ct);

    /// <summary>
    /// 把无任何记录（含软删除的记录）引用的 blob 移入回收站。
    /// </summary>
    /// <param name="scopeHashes">
    /// 回收的**检查范围**。null 表示全池（手动"完整校验 + 回收"）。
    /// 非 null 时只检查这些哈希——设计文档 03 §8 要求"每次备份结束后做一次**增量回收**，
    /// 只检查本次涉及的哈希"，全池扫描在有百万 blob 时不该挂在每次备份的尾巴上。
    /// </param>
    Task<GcResult> CollectAsync(Guid taskId, IReadOnlyCollection<string>? scopeHashes, CancellationToken ct);

    /// <summary>回收站占用与账本。</summary>
    Task<RecycleBinStatus> GetRecycleBinAsync(Guid taskId, CancellationToken ct);

    /// <summary>清空回收站（**硬删除**，不可逆）。</summary>
    Task ClearRecycleBinAsync(Guid taskId, CancellationToken ct);
}
