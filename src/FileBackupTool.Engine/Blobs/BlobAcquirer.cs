using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.IO;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Engine.Blobs;

/// <summary>为源文件取得 blob 的结果。</summary>
public readonly record struct BlobAcquireResult(
    string Hash,
    bool WroteBytes,
    long BytesWritten,
    BlobWriteStrategy Strategy,
    int SourceReadCount)
{
    /// <summary>blob 池里已经有这个内容，本次**零写入**。</summary>
    public bool Reused => !WroteBytes;
}

/// <summary>
/// 为源文件取得 blob（含去重）。设计文档 03 §3.3。
/// </summary>
/// <remarks>
/// <para>
/// 两条路径，由 <see cref="DeduplicationPolicy"/> 依据"是否存在同长度的哈希记录"决定：
/// </para>
/// <list type="bullet">
/// <item><b>长度全新</b> → 边拷边算（1 读 + 1 写）。因为不存在同长度记录，
/// 内容必然全新的，这个写入**不可能白费**。</item>
/// <item><b>长度可能撞车</b> → 先算哈希（1 读）；命中则 0 写；
/// 未命中再拷一遍（第 2 读 + 1 写）。</item>
/// </list>
/// <para>
/// <b>设计文档没点明、但必须处理的一个坑</b>：先算哈希那条路径里，
/// 从"算完哈希"到"开始拷贝"之间文件可能又被改了。若直接沿用第一次算出的哈希当 blob 名，
/// 就会写出一个<b>文件名与内容不符</b>的 blob——而内容寻址的全部完整性校验都建立在
/// "blob 名 == 内容哈希"上，这会静默污染 blob 池并被后续快照反复复用。
/// </para>
/// <para>
/// 所以"未命中 → 拷贝"这一步一律走<b>边拷边算</b>，并用**这次**算出来的哈希命名。
/// 于是 blob 永远自洽；代价只是多算一次哈希（CPU），不多读一次盘。
/// </para>
/// <para>
/// 对 <c>IOException</c> 做有限次指数退避重试，覆盖"文件被独占锁定"这类
/// 静默期检测不出来的情况（设计文档 03 §5.2 第 4 条）。
/// </para>
/// </remarks>
public sealed class BlobAcquirer
{
    private readonly IFileSystem _fs;
    private readonly IBlobStore _blobStore;
    private readonly IHashService _hash;
    private readonly JobOptions _options;
    private readonly IoPrioritySwitch _ioSwitch;
    private readonly ILogger<BlobAcquirer> _logger;

    public BlobAcquirer(
        IFileSystem fs,
        IBlobStore blobStore,
        IHashService hash,
        JobOptions options,
        IoPrioritySwitch ioSwitch,
        ILoggerFactory loggerFactory)
    {
        _fs = fs;
        _blobStore = blobStore;
        _hash = hash;
        _options = options;
        _ioSwitch = ioSwitch;
        _logger = loggerFactory.CreateLogger<BlobAcquirer>();
    }

    /// <summary>
    /// 取得 blob，失败时按配置重试。
    /// </summary>
    /// <param name="entry">
    /// 枚举时取到的文件条目的快照。**必须传这个，而不是路径**：拷贝结束后要拿它
    /// 与源文件的当前状态比对，判断"读到的内容是不是完整的"。
    /// </param>
    /// <param name="anyRecordWithSameLength">
    /// 是否存在"<c>ContentHash</c> 非空且 <c>Length</c> 等于本文件长度"的记录。
    /// 见 <see cref="DeduplicationPolicy"/>。
    /// </param>
    /// <exception cref="IOException">重试耗尽后仍失败。</exception>
    /// <exception cref="FileUnstableException">
    /// 枚举之后文件发生了变化（长度或最后写入时间）。**不重试**：文件还在写，
    /// 立刻重试只会再读一遍半成品。调用方应按跳过处理，下一次扫描再来。
    /// </exception>
    public async Task<BlobAcquireResult> AcquireAsync(
        FileEntry entry,
        bool anyRecordWithSameLength,
        CancellationToken ct)
    {
        int attempts = Math.Max(1, _options.IoRetryCount);
        Exception? last = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await AcquireOnceAsync(entry, anyRecordWithSameLength, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt == attempts - 1)
                {
                    break;
                }

                double delaySeconds = _options.IoRetryBaseDelaySeconds * Math.Pow(2, attempt);
                _logger.LogDebug(
                    ex, "读取 {Path} 失败，{Delay:F1}s 后重试（第 {Attempt}/{Total} 次）",
                    entry.FullPath, delaySeconds, attempt + 1, attempts);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException($"读取 {entry.FullPath} 失败");
    }

    private async Task<BlobAcquireResult> AcquireOnceAsync(
        FileEntry entry,
        bool anyRecordWithSameLength,
        CancellationToken ct)
    {
        string sourceFullPath = entry.FullPath;
        BlobWriteStrategy strategy = DeduplicationPolicy.Decide(anyRecordWithSameLength);

        if (strategy == BlobWriteStrategy.HashFirstThenMaybeCopy)
        {
            string hash;
            using (Stream readStream = _fs.OpenRead(sourceFullPath, backgroundIo: WantBackgroundIo))
            {
                hash = await _hash.ComputeHashAsync(readStream, ct).ConfigureAwait(false);
            }

            // 算完哈希也要复核：这一趟同样可能读到"正在长"的半成品。
            EnsureUnchanged(entry);

            if (_blobStore.Exists(hash))
            {
                // 命中：0 次写。这正是 Length 预筛要保住的那一半收益——
                // 否则"边拷边算"会把这一整份文件白写一遍。
                //
                // 但命中也不能直接交付：**blob 自己的长度**才是"刚读到的内容有多长"的权威值，
                // 而它不受目录项滞后影响。长度对不上就说明枚举之后文件变了
                // （典型形态：被截断成 0 字节，而空 blob 早就存在），
                // 交付它等于写一条"长度 L、内容为空"的记录，比跳过坏得多。
                long blobLength = _blobStore.GetLength(hash);
                if (blobLength != entry.Length)
                {
                    throw new FileUnstableException(entry.FullPath,
                        $"枚举时 {entry.Length} 字节，按当前内容命中的 blob 是 {blobLength} 字节");
                }

                return new BlobAcquireResult(hash, WroteBytes: false, 0, strategy, SourceReadCount: 1);
            }
        }

        return await CopyWhileHashingAsync(entry, strategy, ct).ConfigureAwait(false);
    }

    /// <summary>开关打开时才申请低 IO 优先级。</summary>
    private bool WantBackgroundIo => _ioSwitch.Enabled;

    /// <summary>
    /// 复核"读到的内容是否等于枚举时那一版"。不一致就抛，让本轮跳过。
    /// </summary>
    /// <remarks>
    /// 这是部分写入防护里**唯一真正有效**的一层。宁可变慢一点、多一次 stat，
    /// 也不能把一个截断的文件固化成合法 blob —— blob 名就是内容哈希，
    /// 一旦固化，后续所有完整性校验都会替它作证。
    /// </remarks>
    private void EnsureUnchanged(FileEntry entry)
    {
        FileEntry? current = _fs.GetFile(entry.FullPath);
        if (current is null)
        {
            // 读完就没了：不是"不稳定"，是真的读不到了。按普通 IO 失败处理更能说明问题。
            throw new IOException($"读取期间文件已消失：{entry.FullPath}");
        }

        FileEntry now = current.Value;
        if (now.Length != entry.Length || now.FileTimeUtc != entry.FileTimeUtc)
        {
            throw new FileUnstableException(entry.FullPath,
                $"枚举时 {entry.Length} 字节 / {entry.FileTimeUtc:yyyy-MM-dd HH:mm:ss.fff}，" +
                $"读取后 {now.Length} 字节 / {now.FileTimeUtc:yyyy-MM-dd HH:mm:ss.fff}");
        }
    }

    private async Task<BlobAcquireResult> CopyWhileHashingAsync(
        FileEntry entry,
        BlobWriteStrategy strategy,
        CancellationToken ct)
    {
        string sourceFullPath = entry.FullPath;
        string token = string.Empty;
        Stream? temp = null;
        try
        {
            using Stream source = _fs.OpenRead(sourceFullPath, backgroundIo: WantBackgroundIo);
            temp = _blobStore.CreateTemporary(out token);

            using IIncrementalHasher hasher = _hash.CreateIncremental();
            long copied = await Core.Streams.StreamHelper
                .CopyAndHashAsync(source, temp, hasher, _options.CopyBufferSizeBytes, ct)
                .ConfigureAwait(false);

            await temp.FlushAsync(ct).ConfigureAwait(false);
            await temp.DisposeAsync().ConfigureAwait(false);
            temp = null;

            // 最硬的一条：**实际读到的字节数**与枚举时记下的长度不符。
            // 它不依赖任何元数据，所以 NTFS 目录项滞后也躲不过去 ——
            // 生产事故里"记录长度 0、blob 却有 4 MB"正是被这一条直接抓住的形态。
            if (copied != entry.Length)
            {
                throw new FileUnstableException(entry.FullPath,
                    $"枚举时 {entry.Length} 字节，实际读到 {copied} 字节");
            }

            // 提交前最后一道闸：拷贝期间变过就作废（finally 会 DiscardTemporary）。
            EnsureUnchanged(entry);

            string computed = hasher.Finish();
            BlobPutResult put = _blobStore.CommitTemporary(token, computed);
            token = string.Empty;

            return new BlobAcquireResult(
                put.Hash,
                put.WroteBytes,
                put.WroteBytes ? copied : 0,
                strategy,
                SourceReadCount: 1);
        }
        finally
        {
            if (temp is not null)
            {
                await temp.DisposeAsync().ConfigureAwait(false);
            }

            if (!string.IsNullOrEmpty(token))
            {
                // 失败路径上别把垃圾留在 .tmp 里
                _blobStore.DiscardTemporary(token);
            }
        }
    }
}
