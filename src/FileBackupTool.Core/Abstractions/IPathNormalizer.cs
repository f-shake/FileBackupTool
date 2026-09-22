namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 路径归一化与关系判断的**唯一入口**。设计文档 02 §3.2 / 06 §2。
/// </summary>
/// <remarks>
/// 平台的路径语义差异（Windows 大小写不敏感与 <c>\</c>、长路径 <c>\\?\</c> 前缀）
/// 全部收敛在这里，**不允许任何地方绕过**。
/// </remarks>
public interface IPathNormalizer
{
    char DirectorySeparatorChar { get; }

    /// <summary>路径相等比较器（Windows: 序数忽略大小写；Linux: 序数）。</summary>
    StringComparer Comparer { get; }

    /// <summary>
    /// 归一化为"比较键"形式（Windows: 统一分隔符 + <c>ToLowerInvariant</c>）。
    /// 用于 <c>BackupFile.RelativePathKey</c>、去重字典 key、监控防抖字典 key。
    /// **不要**用它生成要写回磁盘的路径。
    /// </summary>
    string Normalize(string path);

    bool AreEqual(string a, string b);

    /// <summary><paramref name="candidate"/> 是否等于 <paramref name="root"/> 或位于其子树内。</summary>
    bool IsSameOrUnder(string candidate, string root);

    /// <summary>两者是否存在包含关系（含相等）。目录关系校验用。</summary>
    bool Overlaps(string a, string b);

    /// <summary>相对路径；不在子树内时返回 null。</summary>
    string? GetRelativePathOrNull(string root, string fullPath);

    string Combine(string parent, string child);

    /// <summary>绝对路径是否为绝对路径。</summary>
    bool IsAbsolute(string path);

    /// <summary>UNC（<c>\\server\share</c>）路径。设计文档 03 §5.5：UNC **放弃 watcher**，只跑轮询。</summary>
    bool IsNetworkPath(string path);

    /// <summary>
    /// 转为 <c>\\?\</c> 扩展长度路径（需要时）。见设计文档 01 §6.1。
    /// 注意 <c>\\?\</c> 会**关闭路径规范化**：<c>..</c>、正斜杠、相对路径全部失效，
    /// 因此调用前必须是完全限定的路径。
    /// </summary>
    string ToExtendedPath(string absolutePath);

    /// <summary>去掉尾部目录分隔符（根路径如 <c>C:\</c> 除外）。</summary>
    string TrimTrailingSeparator(string path);

    /// <summary>把相对路径拆成目录片段（用于 FzLib 筛选的目录匹配）。</summary>
    string[] SplitSegments(string relativePath);

    /// <summary>获取最后一段（文件名或目录名）。</summary>
    string GetFileName(string path);

    /// <summary>获取父目录；没有父目录时返回空串。</summary>
    string GetDirectoryName(string path);
}
