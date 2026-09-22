namespace FileBackupTool.Api.Contracts;

/// <summary>可浏览的根（驱动器 / 配置的根）。</summary>
public sealed record FsRootDto(string Path, string Name, string Type, bool Ready);

/// <summary>
/// <c>/api/fs/list</c> 的一个条目。**只返回目录**，且不返回文件内容（设计文档 05 §8）。
/// </summary>
public sealed record FsEntryDto(string Name, string Path, DateTime? LastWriteTimeUtc, bool? HasChildren);

/// <summary><c>POST /api/fs/validate</c> 的请求体。</summary>
public sealed record FsValidateRequestDto
{
    public string? SourceDir { get; init; }

    public string? BackupDir { get; init; }

    public string? RestoreRoot { get; init; }

    public Guid? TaskId { get; init; }
}

/// <summary>
/// <c>POST /api/fs/filter-test</c> 的请求体（"使用当前目录测试"）。
/// </summary>
/// <remarks>
/// 这个端点在设计文档 06 §4 的清单里**没有**，是前端为落实对等清单而假定的接口。
/// 后端按前端已声明的形状实现（前端已写好调用代码，改路径要两端一起改）。
/// </remarks>
public sealed record FilterTestRequestDto(string? SourceDir, string? FilterJson);

/// <summary>"使用当前目录测试"的响应。</summary>
public sealed record FilterTestResultDto
{
    public int FileCount { get; init; }

    public long TotalBytes { get; init; }

    public required IReadOnlyList<FilterTestFileDto> Files { get; init; }

    /// <summary>扫描过程中跳过的目录数（无权限 / 重解析点）。**不静默**。</summary>
    public int SkippedDirCount { get; init; }

    public bool Truncated { get; init; }

    public long ElapsedMs { get; init; }
}

public sealed record FilterTestFileDto(string RelativePath, long Length, DateTime? FileTimeUtc);
