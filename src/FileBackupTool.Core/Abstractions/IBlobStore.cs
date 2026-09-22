namespace FileBackupTool.Core.Abstractions;

/// <summary>blob 写入结果。</summary>
public enum BlobPutOutcome
{
    /// <summary>本次真的往池里写入了新 blob。</summary>
    Created = 0,

    /// <summary>池里已经有了，临时内容被丢弃（**没有产生额外写入**）。</summary>
    AlreadyExisted = 1,
}

public readonly record struct BlobPutResult(BlobPutOutcome Outcome, string Hash, long Length)
{
    /// <summary>本次是否产生了实际写入。设计文档 08 §1.4 要求断言"不产生白写"，靠它。</summary>
    public bool WroteBytes => Outcome == BlobPutOutcome.Created;
}

/// <summary>回收账本条目。设计文档 02 §5.2。</summary>
public readonly record struct RecycleLedgerEntry(
    string Hash,
    long Length,
    string? LastPath,
    DateTime MovedAtUtc,
    bool WasDirectory);

/// <summary>一次批量回收中的单个请求。</summary>
public readonly record struct BlobRecycleRequest(
    string Hash,
    long Length,
    string? LastKnownPath,
    bool WasDirectory);

public sealed record RecycleBinStatus(
    int BlobCount,
    long TotalBytes,
    IReadOnlyList<RecycleLedgerEntry> Entries);

/// <summary>
/// 内容寻址 blob 池。设计文档 02 §4。
/// 布局：<c>blobs/&lt;hash[0:2]&gt;/&lt;hash[2:4]&gt;/&lt;hash&gt;</c>，回收站为 <c>_deleted/blobs/…</c>。
/// </summary>
/// <remarks>
/// 全部基于 <see cref="Stream"/>（而不是路径），这样它只依赖 <see cref="IFileSystem"/>，
/// 测试里换掉文件系统就等于换掉了 blob 池。
/// </remarks>
public interface IBlobStore
{
    /// <summary>blob 池根目录（绝对路径），即 <c>&lt;BackupDir&gt;\blobs</c>。</summary>
    string RootPath { get; }

    /// <summary>回收站根目录（绝对路径），即 <c>&lt;BackupDir&gt;\_deleted</c>。</summary>
    string RecycleBinRoot { get; }

    /// <summary>blob 在池内的相对路径（相对 <see cref="RootPath"/> 的父级，即相对 BackupDir）。</summary>
    string GetRelativeBlobPath(string hash);

    /// <summary>blob 的绝对路径。</summary>
    string GetFullBlobPath(string hash);

    bool Exists(string hash);

    /// <summary>是否在回收站里（还原失败提示需要区分这两种情况）。</summary>
    bool ExistsInRecycleBin(string hash);

    long GetLength(string hash);

    Stream OpenRead(string hash);

    /// <summary>
    /// 创建一个临时 blob 用于"边拷边算"。写完必须调用
    /// <see cref="CommitTemporary"/> 或 <see cref="DiscardTemporary"/>。
    /// </summary>
    Stream CreateTemporary(out string token);

    /// <summary>把临时 blob 就位为 <paramref name="hash"/> 对应的池内文件；已存在则丢弃临时文件。</summary>
    BlobPutResult CommitTemporary(string token, string hash);

    void DiscardTemporary(string token);

    /// <summary>
    /// 把 blob 移入回收站（同卷 <c>File.Move</c>，元数据操作）。
    /// <paramref name="lastKnownPath"/> 写入账本，供用户判断能不能删。
    /// </summary>
    /// <returns>是否发生了移动。</returns>
    bool Recycle(string hash, string? lastKnownPath, long length, bool wasDirectory, DateTime movedAtUtc);

    /// <summary>
    /// 批量回收。必须提供——账本是单个 JSON，逐个回收会退化成 O(n²) 的读改写。
    /// </summary>
    int RecycleMany(IReadOnlyList<BlobRecycleRequest> requests, DateTime movedAtUtc);

    RecycleBinStatus GetRecycleBinStatus();

    /// <summary>**硬删除**，不可逆。设计文档 06 §4.6 要求二次确认。</summary>
    void ClearRecycleBin();

    /// <summary>枚举池中所有哈希（GC 用）。惰性。</summary>
    IEnumerable<string> EnumerateBlobHashes();

    /// <summary>
    /// 枚举回收站里所有哈希。GC 判定"丢失文件"时要把回收站也算作"还在"，
    /// 否则刚回收的 blob 会被报成丢失。
    /// </summary>
    IEnumerable<string> EnumerateRecycleBinHashes();

    /// <summary>清理遗留的临时文件（进程崩溃后残留）。</summary>
    void CleanTemporary();
}
