namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 哈希抽象。设计文档 06 §2：可替换，便于测试。
/// </summary>
/// <remarks>
/// 之所以不用 FzLib 的 <c>FileHashHelper</c>：它只接受**路径**，既绕不开 <see cref="IFileSystem"/>，
/// 也无法"边拷边算"（设计文档 03 §3.3 的 <c>Length</c> 预筛路径需要一次读同时完成写与哈希）。
/// </remarks>
public interface IHashService
{
    /// <summary>算法名，如 "SHA256"。</summary>
    string Name { get; }

    /// <summary>十六进制字符串长度（SHA-256 为 64）。</summary>
    int HexLength { get; }

    /// <summary>开启一个增量哈希会话，用于边读边算。</summary>
    IIncrementalHasher CreateIncremental();

    /// <summary>把 <paramref name="stream"/> 读到结尾并算出十六进制小写哈希。</summary>
    Task<string> ComputeHashAsync(Stream stream, CancellationToken ct);

    /// <summary>
    /// 是否是本算法合法形式的哈希串。GC 用**哈希格式**识别 blob，
    /// 而不是旧实现那种"文件名长度 == 32"的启发式（会误伤用户自有的 32 字符文件名）。
    /// </summary>
    bool IsValidHash(string? value);
}

/// <summary>增量哈希会话。</summary>
public interface IIncrementalHasher : IDisposable
{
    void Append(ReadOnlySpan<byte> data);

    /// <summary>结束并返回十六进制小写哈希。会话随后不可再用。</summary>
    string Finish();
}
