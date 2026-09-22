using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Core.Paths;

/// <summary>
/// Windows 路径语义实现。大小写不敏感（序数忽略大小写），分隔符统一为 <c>\</c>。
/// </summary>
public sealed class WindowsPathNormalizer : IPathNormalizer
{
    /// <summary>
    /// 超过此长度就走 <c>\\?\</c> 扩展路径。取 240 而不是 260：留出安全余量，
    /// 因为 <c>Directory.CreateDirectory</c> 之类内部可能再拼一段临时后缀。
    /// </summary>
    public const int ExtendedPathThreshold = 240;

    public static readonly WindowsPathNormalizer Instance = new();

    private const string ExtendedPrefix = @"\\?\";
    private const string ExtendedUncPrefix = @"\\?\UNC\";

    public char DirectorySeparatorChar => '\\';

    public StringComparer Comparer => StringComparer.OrdinalIgnoreCase;

    public string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string s = path.Replace('/', '\\');
        s = TrimTrailingSeparator(s);
        return s.ToLowerInvariant();
    }

    public bool AreEqual(string a, string b) => Comparer.Equals(TrimTrailingSeparator(a), TrimTrailingSeparator(b));

    public string TrimTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        string s = path;
        while (s.Length > 0 && (s[^1] == '\\' || s[^1] == '/'))
        {
            // 保留根：C:\  或  \\  ？ UNC 的 "\\server\share\" 应当去掉尾部
            if (s.Length == 3 && s[1] == ':')
            {
                break;
            }

            s = s[..^1];
        }

        return s;
    }

    public bool IsAbsolute(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        // \\?\ 前缀也算绝对
        if (path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        // UNC
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        // 驱动器绝对路径 C:\ 或 C:/
        return path.Length >= 3 && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
    }

    public bool IsNetworkPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        if (path.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // \\server\share 形式（排除 \\?\ 本地扩展前缀）
        if (path.StartsWith(@"\\", StringComparison.Ordinal) &&
            !path.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public bool IsSameOrUnder(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        string c = Normalize(candidate);
        string r = Normalize(root);

        if (Comparer.Equals(c, r))
        {
            return true;
        }

        string prefix = r.EndsWith('\\') ? r : r + '\\';
        return c.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public bool Overlaps(string a, string b) => IsSameOrUnder(a, b) || IsSameOrUnder(b, a);

    public string? GetRelativePathOrNull(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        string r = TrimTrailingSeparator(root);
        string f = TrimTrailingSeparator(fullPath);

        if (Comparer.Equals(r, f))
        {
            return string.Empty;
        }

        string prefix = r.EndsWith('\\') ? r : r + '\\';
        if (!f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return f[prefix.Length..];
    }

    public string Combine(string parent, string child)
    {
        if (string.IsNullOrEmpty(parent))
        {
            return child;
        }

        if (string.IsNullOrEmpty(child))
        {
            return parent;
        }

        if (IsAbsolute(child))
        {
            return child;
        }

        string p = TrimTrailingSeparator(parent);
        // 根（C:\）在 TrimTrailingSeparator 里被保留，这里也保留其分隔符
        return p.EndsWith('\\') ? p + child : p + '\\' + child;
    }

    public string ToExtendedPath(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return absolutePath;
        }

        // 已经带前缀
        if (absolutePath.StartsWith(ExtendedPrefix, StringComparison.Ordinal))
        {
            return absolutePath;
        }

        // 短路径不需要扩展——注意 \\?\ 会关闭路径规范化（..、正斜杠、相对路径全部失效），
        // 也会让某些网络重定向器不认，所以只在确实需要时才用。
        if (absolutePath.Length < ExtendedPathThreshold)
        {
            return absolutePath;
        }

        if (!IsAbsolute(absolutePath))
        {
            // 相对路径无法扩展
            return absolutePath;
        }

        // 正斜杠在 \\?\ 下不被接受
        string s = absolutePath.Replace('/', '\\');

        return IsNetworkPath(s)
            ? ExtendedUncPrefix + s[2..]
            : ExtendedPrefix + s;
    }

    public string[] SplitSegments(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return [];
        }

        return relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    }

    public string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string s = TrimTrailingSeparator(path);
        int i = s.LastIndexOfAny(['\\', '/']);
        return i < 0 ? s : s[(i + 1)..];
    }

    public string GetDirectoryName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string s = TrimTrailingSeparator(path);
        int i = s.LastIndexOf('\\');
        if (i < 0)
        {
            return string.Empty;
        }

        // "C:\" 的父目录按 "C:\" 处理（不能退化成 "C:"）
        return i == 2 && s[1] == ':' ? s[..3] : s[..i];
    }
}

/// <summary>
/// POSIX 路径语义实现。大小写敏感。<b>仅用于单元测试对跨平台语义做断言</b>，
/// 生产部署目标为 Windows。
/// </summary>
public sealed class PosixPathNormalizer : IPathNormalizer
{
    public static readonly PosixPathNormalizer Instance = new();

    public char DirectorySeparatorChar => '/';

    public StringComparer Comparer => StringComparer.Ordinal;

    public string Normalize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        return TrimTrailingSeparator(path.Replace('\\', '/'));
    }

    public bool AreEqual(string a, string b) => Comparer.Equals(TrimTrailingSeparator(a), TrimTrailingSeparator(b));

    public string TrimTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return path;
        }

        string s = path;
        while (s.Length > 1 && (s[^1] == '/' || s[^1] == '\\'))
        {
            s = s[..^1];
        }

        return s;
    }

    public bool IsAbsolute(string path) => !string.IsNullOrEmpty(path) && path[0] == '/';

    public bool IsNetworkPath(string path) => false;

    public bool IsSameOrUnder(string candidate, string root)
    {
        if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(root))
        {
            return false;
        }

        string c = Normalize(candidate);
        string r = Normalize(root);
        return Comparer.Equals(c, r) || c.StartsWith(r + '/', StringComparison.Ordinal);
    }

    public bool Overlaps(string a, string b) => IsSameOrUnder(a, b) || IsSameOrUnder(b, a);

    public string? GetRelativePathOrNull(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath))
        {
            return null;
        }

        string r = TrimTrailingSeparator(root);
        string f = TrimTrailingSeparator(fullPath);
        if (Comparer.Equals(r, f))
        {
            return string.Empty;
        }

        string prefix = r.EndsWith('/') ? r : r + '/';
        return f.StartsWith(prefix, StringComparison.Ordinal) ? f[prefix.Length..] : null;
    }

    public string Combine(string parent, string child)
    {
        if (string.IsNullOrEmpty(parent))
        {
            return child;
        }

        if (string.IsNullOrEmpty(child))
        {
            return parent;
        }

        if (IsAbsolute(child))
        {
            return child;
        }

        string p = TrimTrailingSeparator(parent);
        return p.EndsWith('/') ? p + child : p + '/' + child;
    }

    public string ToExtendedPath(string absolutePath) => absolutePath;

    public string[] SplitSegments(string relativePath) =>
        string.IsNullOrEmpty(relativePath) ? [] : relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    public string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string s = TrimTrailingSeparator(path);
        int i = s.LastIndexOf('/');
        return i < 0 ? s : s[(i + 1)..];
    }

    public string GetDirectoryName(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        string s = TrimTrailingSeparator(path);
        int i = s.LastIndexOf('/');
        return i <= 0 ? (s.StartsWith('/') ? "/" : string.Empty) : s[..i];
    }
}
