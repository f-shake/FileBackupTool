using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Core.Streams;

/// <summary>流复制工具。全引擎只有这一处做"读-写"循环。</summary>
public static class StreamHelper
{
    /// <summary>默认缓冲 1 MiB。机械盘上顺序 IO 的吞吐主要取决于队列深度，缓冲再大收益有限。</summary>
    public const int DefaultBufferSize = 1024 * 1024;

    /// <summary>
    /// 把 <paramref name="source"/> 复制到 <paramref name="destination"/>，
    /// 同时（可选）喂给增量哈希——这就是设计文档 03 §3.3 的"边拷边算"。
    /// </summary>
    /// <returns>复制的字节数。</returns>
    public static async Task<long> CopyAndHashAsync(
        Stream source,
        Stream destination,
        IIncrementalHasher? hasher,
        int bufferSize,
        CancellationToken ct,
        Action<long>? onBytesCopied = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (bufferSize < 1)
        {
            bufferSize = DefaultBufferSize;
        }

        byte[] buffer = new byte[bufferSize];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            hasher?.Append(buffer.AsSpan(0, read));
            total += read;
            onBytesCopied?.Invoke(read);
        }

        return total;
    }

    /// <summary>复制流，不做哈希。</summary>
    public static Task<long> CopyAsync(
        Stream source,
        Stream destination,
        int bufferSize,
        CancellationToken ct,
        Action<long>? onBytesCopied = null)
        => CopyAndHashAsync(source, destination, null, bufferSize, ct, onBytesCopied);

    /// <summary>把流整个读出来算哈希（用于"先算哈希"那条路径）。</summary>
    public static async Task<long> HashAsync(
        Stream source,
        IIncrementalHasher hasher,
        int bufferSize,
        CancellationToken ct)
    {
        if (bufferSize < 1)
        {
            bufferSize = DefaultBufferSize;
        }

        byte[] buffer = new byte[bufferSize];
        long total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (read <= 0)
            {
                break;
            }

            hasher.Append(buffer.AsSpan(0, read));
            total += read;
        }

        return total;
    }
}
