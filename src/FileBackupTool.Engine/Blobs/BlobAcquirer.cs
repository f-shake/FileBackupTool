using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Configuration;
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
    private readonly ILogger<BlobAcquirer> _logger;

    public BlobAcquirer(
        IFileSystem fs,
        IBlobStore blobStore,
        IHashService hash,
        JobOptions options,
        ILoggerFactory loggerFactory)
    {
        _fs = fs;
        _blobStore = blobStore;
        _hash = hash;
        _options = options;
        _logger = loggerFactory.CreateLogger<BlobAcquirer>();
    }

    /// <summary>
    /// 取得 blob，失败时按配置重试。
    /// </summary>
    /// <param name="sourceFullPath">源文件绝对路径。</param>
    /// <param name="anyRecordWithSameLength">
    /// 是否存在"<c>ContentHash</c> 非空且 <c>Length</c> 等于本文件长度"的记录。
    /// 见 <see cref="DeduplicationPolicy"/>。
    /// </param>
    /// <exception cref="IOException">重试耗尽后仍失败。</exception>
    public async Task<BlobAcquireResult> AcquireAsync(
        string sourceFullPath,
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
                return await AcquireOnceAsync(sourceFullPath, anyRecordWithSameLength, ct).ConfigureAwait(false);
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
                    sourceFullPath, delaySeconds, attempt + 1, attempts);

                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct).ConfigureAwait(false);
            }
        }

        throw last ?? new IOException($"读取 {sourceFullPath} 失败");
    }

    private async Task<BlobAcquireResult> AcquireOnceAsync(
        string sourceFullPath,
        bool anyRecordWithSameLength,
        CancellationToken ct)
    {
        BlobWriteStrategy strategy = DeduplicationPolicy.Decide(anyRecordWithSameLength);

        if (strategy == BlobWriteStrategy.HashFirstThenMaybeCopy)
        {
            string hash;
            using (Stream readStream = _fs.OpenRead(sourceFullPath))
            {
                hash = await _hash.ComputeHashAsync(readStream, ct).ConfigureAwait(false);
            }

            if (_blobStore.Exists(hash))
            {
                // 命中：0 次写。这正是 Length 预筛要保住的那一半收益——
                // 否则"边拷边算"会把这一整份文件白写一遍。
                return new BlobAcquireResult(hash, WroteBytes: false, 0, strategy, SourceReadCount: 1);
            }
        }

        return await CopyWhileHashingAsync(sourceFullPath, strategy, ct).ConfigureAwait(false);
    }

    private async Task<BlobAcquireResult> CopyWhileHashingAsync(
        string sourceFullPath,
        BlobWriteStrategy strategy,
        CancellationToken ct)
    {
        string token = string.Empty;
        Stream? temp = null;
        try
        {
            using Stream source = _fs.OpenRead(sourceFullPath);
            temp = _blobStore.CreateTemporary(out token);

            using IIncrementalHasher hasher = _hash.CreateIncremental();
            long copied = await Core.Streams.StreamHelper
                .CopyAndHashAsync(source, temp, hasher, _options.CopyBufferSizeBytes, ct)
                .ConfigureAwait(false);

            await temp.FlushAsync(ct).ConfigureAwait(false);
            await temp.DisposeAsync().ConfigureAwait(false);
            temp = null;

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
