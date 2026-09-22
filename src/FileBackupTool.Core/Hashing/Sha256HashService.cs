using System.Security.Cryptography;
using System.Text;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Core.Hashing;

/// <summary>
/// SHA-256 十六进制小写实现。设计文档 03 §3.4。
/// </summary>
/// <remarks>
/// 相对旧的 SHA-1：在公网 + 高安全等级的语境下，保留 SHA-1 会是个被人挑刺的点
/// （尽管这里只用于去重、不用于安全）。SHA-256 有 SHA-NI 硬件指令，现代 CPU 单核 1–2 GB/s，
/// **哈希通常不是瓶颈，磁盘才是**。
/// </remarks>
public sealed class Sha256HashService : IHashService
{
    public static readonly Sha256HashService Instance = new();

    public string Name => "SHA256";

    public int HexLength => 64;

    public IIncrementalHasher CreateIncremental() => new IncrementalSha256Hasher();

    public async Task<string> ComputeHashAsync(Stream stream, CancellationToken ct)
    {
        using var hasher = CreateIncremental();
        await Streams.StreamHelper.HashAsync(stream, hasher, Streams.StreamHelper.DefaultBufferSize, ct)
            .ConfigureAwait(false);
        return hasher.Finish();
    }

    public bool IsValidHash(string? value)
    {
        if (value is null || value.Length != HexLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            bool ok = c is >= '0' and <= '9' or >= 'a' and <= 'f';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private sealed class IncrementalSha256Hasher : IIncrementalHasher
    {
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public void Append(ReadOnlySpan<byte> data) => _hash.AppendData(data);

        public string Finish()
        {
            byte[] bytes = _hash.GetHashAndReset();
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }

            return sb.ToString();
        }

        public void Dispose() => _hash.Dispose();
    }
}
