using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 实体 / 引擎模型 → 线上 DTO 的映射。
/// </summary>
/// <remarks>
/// 集中在一处，避免"同一个字段在三个控制器里被翻译成三种写法"。
/// 所有时间字段都是 UTC（设计文档 08 §4.1：存 UTC、显示本地，时区由前端处理）。
/// </remarks>
public sealed class ApiMapper(IPathNormalizer normalizer)
{
    private readonly IPathNormalizer _normalizer = normalizer;

    public SnapshotDto Snapshot(BackupSnapshot s) => new()
    {
        Id = s.Id,
        TaskId = s.TaskId,
        Type = s.Type.ToString(),
        Status = s.Status.ToString(),
        BeginTime = s.BeginTime,
        EndTime = s.EndTime,
        CreatedFileCount = s.CreatedFileCount,
        ModifiedFileCount = s.ModifiedFileCount,
        DeletedFileCount = s.DeletedFileCount,
        FailedFileCount = s.FailedFileCount,
        SkippedFileCount = s.SkippedFileCount,
        TotalBytes = s.TotalBytes,
        AbortReason = s.AbortReason,
    };

    /// <summary>快照详情：平铺的派生字段（前端契约里 SnapshotDetail extends Snapshot）。</summary>
    public SnapshotDto SnapshotDetail(BackupSnapshot s, string? taskName) => Snapshot(s) with
    {
        TaskName = taskName,
        MirrorFileCount = s.ImageFileCount,
        MirrorTotalBytes = s.ImageTotalBytes,
        MirrorInconsistencyCount = s.MirrorInconsistencyCount,
        Restorable = s.IsRestorable,
    };

    public BackupTaskDto Task(BackupTask t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        SourceDir = t.SourceDir,
        BackupDir = t.BackupDir,
        FilterJson = t.FilterJson,
        ByTimeInterval = t.ByTimeInterval,
        TimeIntervalMinutes = t.TimeIntervalMinutes,
        MaxAutoIncrementBackupCount = t.MaxAutoIncrementBackupCount,
        IsDefaultVirtualBackup = t.IsDefaultVirtualBackup,
        ByWatching = t.ByWatching,
        IsEnabled = t.IsEnabled,
        CreatedAt = t.CreatedAt,
        UpdatedAt = t.UpdatedAt,
    };

    /// <summary>
    /// 镜像条目。目录路径统一用 <c>/</c> 分隔（前端契约如此），
    /// 但引擎内部一律用 <c>\</c>（Windows 归一化键），所以这里只做**输出侧**转换，
    /// 绝不去动引擎用的那份字符串。
    /// </summary>
    public MirrorEntryDto Mirror(MirrorEntry entry, bool snapshotRestorable, DateTime? fileTime = null) => new()
    {
        Name = entry.Name,
        RelativePath = ToForwardSlash(entry.RelativePath),
        IsDirectory = entry.IsDirectory,
        Length = entry.Length,
        FileTimeUtc = entry.IsDirectory ? null : Coalesce(entry.FileTimeUtc, fileTime),
        ContentHash = entry.ContentHash,
        Restorable = snapshotRestorable && (entry.IsDirectory || entry.ContentHash is not null),
        BlockedReason = Restorability.BlockedReason(snapshotRestorable, entry.IsDirectory, entry.ContentHash),
    };

    public ChangeEntryDto Change(BackupFile file, bool snapshotRestorable) => new()
    {
        RelativePath = ToForwardSlash(file.RelativePath),
        Name = _normalizer.GetFileName(file.RelativePath),
        Type = file.Type.ToString(),
        Length = file.Length,
        FileTimeUtc = file.FileTimeUtc,
        ContentHash = file.ContentHash,
        Info = Restorability.BlockedReason(
            snapshotRestorable,
            isDirectory: false,
            file.ContentHash,
            isTombstone: file.Type == FileRecordType.Deleted),
    };

    /// <summary>
    /// 文件历史的一行。
    /// </summary>
    /// <param name="relativePath">
    /// 用户查询的那个相对路径。**由调用方传入而不是从 <paramref name="v"/> 取**——
    /// 引擎的 <see cref="FileVersionEntry"/> 只带快照与长度信息，不带路径
    /// （一条历史记录必然对应查询的那个路径，引擎便省掉了这一列）。
    /// </param>
    public FileVersionDto Version(
        FileVersionEntry v,
        string relativePath,
        bool snapshotRestorable,
        DateTime? snapshotEndTime) => new()
    {
        SnapshotId = v.SnapshotId,
        SnapshotType = v.SnapshotType.ToString(),
        SnapshotBeginTime = v.SnapshotBeginTime,
        SnapshotEndTime = snapshotEndTime,
        RelativePath = ToForwardSlash(relativePath),
        Length = v.Length,
        FileTimeUtc = v.FileTimeUtc,
        ContentHash = v.ContentHash,
        ChangeType = v.RecordType.ToString(),
        Restorable = snapshotRestorable && v.RecordType != FileRecordType.Deleted && v.ContentHash is not null,
        BlockedReason = Restorability.BlockedReason(
            snapshotRestorable, isDirectory: false, v.ContentHash,
            isTombstone: v.RecordType == FileRecordType.Deleted),
    };

    public string ToForwardSlash(string path) => path.Replace('\\', '/');

    private static DateTime? Coalesce(DateTime value, DateTime? fallback) =>
        value == default ? fallback : value;
}

/// <summary>
/// "这个文件能不能还原"的判据与文案（设计文档 04 §1.6）。
/// </summary>
/// <remarks>
/// 前两句必须与文档里给的文案**逐字一致**，因为它们是用户唯一能看懂的解释：
/// <list type="bullet">
/// <item>虚拟全量快照的条目 <c>ContentHash</c> 为 null → 「该文件不存在实际备份文件，可能是由虚拟快照生成」</item>
/// <item>blob 既不在池中也不在回收站 → 「该文件不存在实际备份文件，可能是文件丢失」</item>
/// </list>
/// <para>
/// 第三句（墓碑）是对文档的一处补充：删除墓碑的 <c>ContentHash</c> **按定义**就是 null
/// （删除记录不指向任何 blob），若沿用第一句，用户会看到"可能是由虚拟快照生成"——
/// 而这跟虚拟全量毫无关系，是一句会把人带偏的解释。墓碑的正确解释是"这个文件在这一版里
/// 被删掉了，没有内容可还原"。
/// </para>
/// </remarks>
public static class Restorability
{
    public const string BlockedByVirtualFull = "该文件不存在实际备份文件，可能是由虚拟快照生成";
    public const string BlockedByMissingBlob = "该文件不存在实际备份文件，可能是文件丢失";
    public const string BlockedByTombstone = "该文件在此快照中已被删除，没有可还原的内容";

    /// <summary>
    /// 注意：这里只判"结构性不可还原"（虚拟全量 / 删除墓碑）。
    /// blob 是否真的丢失需要查池，在浏览接口里逐文件查池会退化成 N 次 IO，
    /// 因此那一条留给"完整校验"（<c>/api/tasks/{id}/verify</c>）去报，
    /// 而不是在每次列目录时假装知道。
    /// </summary>
    public static string? BlockedReason(
        bool snapshotRestorable,
        bool isDirectory,
        string? contentHash,
        bool isTombstone = false)
    {
        if (isDirectory)
        {
            return null;
        }

        if (isTombstone)
        {
            return BlockedByTombstone;
        }

        if (!snapshotRestorable)
        {
            return BlockedByVirtualFull;
        }

        return contentHash is null ? BlockedByVirtualFull : null;
    }
}
