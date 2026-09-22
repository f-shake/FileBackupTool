namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 文件筛选。语义沿用 <c>FzLib.IO.FileFilterRule</c> 的白名单/黑名单 × 文件/文件夹/路径模型。
/// </summary>
public interface IFileFilter
{
    /// <summary>该文件是否应被备份。</summary>
    bool IncludeFile(string fullPath);

    /// <summary>
    /// 是否应**进入**该目录。返回 false 表示整棵子树被剪枝。
    /// </summary>
    /// <remarks>
    /// **只有 exclude 规则可以剪枝。** 不能用 include 规则剪枝：
    /// 例如 <c>IncludeFolders=photos</c> 下，`C:\src` 本身不匹配 include，
    /// 若据此剪枝就永远走不到 `C:\src\photos`。这是"包含规则"型过滤器的经典陷阱。
    /// </remarks>
    bool DescendInto(string directoryFullPath);
}

/// <summary>不做任何过滤。</summary>
public sealed class NullFileFilter : IFileFilter
{
    public static readonly NullFileFilter Instance = new();

    public bool IncludeFile(string fullPath) => true;

    public bool DescendInto(string directoryFullPath) => true;
}
