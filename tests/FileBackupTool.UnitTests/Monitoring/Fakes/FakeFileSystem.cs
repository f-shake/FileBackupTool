using System.Runtime.ExceptionServices;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.UnitTests.Monitoring.Fakes;

/// <summary>
/// 内存假文件系统。Windows 语义（大小写不敏感）。
/// </summary>
/// <remarks>
/// 监控模块只通过 <see cref="IFileSystem"/> 碰磁盘，所以把这块换成内存字典之后，
/// 全部测试都跑在纯内存里：既不会真的创建文件，也不会因为磁盘状态影响断言。
/// <see cref="FailGetFile"/> 用来构造"文件被独占锁定"（<c>IOException</c>）这条路径。
/// </remarks>
public sealed class FakeFileSystem : IFileSystem
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _getFileCalls = new(StringComparer.OrdinalIgnoreCase);

    public List<DriveEntry> Drives { get; } = [];

    private static readonly DateTime Origin = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void AddDirectory(string fullPath, DateTime? lastWriteUtc = null) =>
        _entries[Trim(fullPath)] = new Entry { IsDirectory = true, LastWriteUtc = lastWriteUtc ?? Origin };

    public void AddFile(string fullPath, long length, DateTime lastWriteUtc) =>
        _entries[Trim(fullPath)] = new Entry { Length = length, LastWriteUtc = lastWriteUtc };

    /// <summary>改动一个已存在的文件（不改动就新增）。</summary>
    public void WriteFile(string fullPath, long length, DateTime lastWriteUtc) =>
        AddFile(fullPath, length, lastWriteUtc);

    public void Delete(string fullPath) => _entries.Remove(Trim(fullPath));

    /// <summary>让某个路径的 <see cref="GetFile"/> 抛异常（模拟被独占锁定）。</summary>
    public void FailGetFile(string fullPath, Exception exception) =>
        _failures[Trim(fullPath)] = exception;

    public void ClearFailure(string fullPath) => _failures.Remove(Trim(fullPath));

    public int GetFileCalls(string fullPath) => _getFileCalls.GetValueOrDefault(Trim(fullPath));

    // ------------------------------------------------------------------
    // IFileSystem
    // ------------------------------------------------------------------

    public bool FileExists(string fullPath) =>
        _entries.TryGetValue(Trim(fullPath), out Entry? e) && !e.IsDirectory;

    public bool DirectoryExists(string fullPath) =>
        _entries.TryGetValue(Trim(fullPath), out Entry? e) && e.IsDirectory;

    /// <summary>监控模块的测试不涉及重解析点，恒为 false。</summary>
    public bool IsReparsePoint(string fullPath) => false;

    public FileEntry? GetFile(string fullPath)
    {
        string key = Trim(fullPath);
        _getFileCalls[key] = _getFileCalls.GetValueOrDefault(key) + 1;

        if (_failures.TryGetValue(key, out Exception? failure))
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return _entries.TryGetValue(key, out Entry? entry) && !entry.IsDirectory
            ? new FileEntry(key, entry.Length, entry.LastWriteUtc, FileAttributes.Normal)
            : null;
    }

    public DirectoryEntry? GetDirectory(string fullPath)
    {
        string key = Trim(fullPath);
        if (_failures.TryGetValue(key, out Exception? failure))
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return _entries.TryGetValue(key, out Entry? entry) && entry.IsDirectory
            ? new DirectoryEntry(key, entry.LastWriteUtc, FileAttributes.Directory)
            : null;
    }

    public void CreateDirectory(string fullPath) => AddDirectory(fullPath);

    public IEnumerable<FileEntry> EnumerateFiles(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        foreach ((string path, Entry entry) in Snapshot())
        {
            if (entry.IsDirectory || !IsUnder(path, rootFullPath))
            {
                continue;
            }

            if (!IsIncluded(path, rootFullPath, options, descendInto))
            {
                continue;
            }

            yield return new FileEntry(path, entry.Length, entry.LastWriteUtc, FileAttributes.Normal);
        }
    }

    public IEnumerable<DirectoryEntry> EnumerateDirectories(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        string root = Trim(rootFullPath);

        foreach ((string path, Entry entry) in Snapshot())
        {
            if (!entry.IsDirectory || path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsUnder(path, root) || !IsIncluded(path, root, options, descendInto))
            {
                continue;
            }

            yield return new DirectoryEntry(path, entry.LastWriteUtc, FileAttributes.Directory);
        }
    }

    public Stream OpenRead(string fullPath, bool backgroundIo = false) => throw new NotSupportedException("监控模块不读文件内容。");

    public Stream Create(string fullPath) => throw new NotSupportedException("监控模块不写文件内容。");

    public void MoveFile(string sourceFullPath, string destinationFullPath, bool overwrite)
    {
        if (_entries.Remove(Trim(sourceFullPath), out Entry? entry))
        {
            _entries[Trim(destinationFullPath)] = entry;
        }
    }

    public void DeleteFile(string fullPath) => Delete(fullPath);

    public void DeleteDirectory(string fullPath, bool recursive)
    {
        string root = Trim(fullPath);
        foreach (string path in Snapshot().Select(kv => kv.Key).Where(p => IsUnder(p, root)).ToList())
        {
            _entries.Remove(path);
        }
    }

    public void SetLastWriteTimeUtc(string fullPath, DateTime lastWriteTimeUtc)
    {
        if (_entries.TryGetValue(Trim(fullPath), out Entry? entry))
        {
            entry.LastWriteUtc = lastWriteTimeUtc;
        }
    }

    public IReadOnlyList<DriveEntry> GetLogicalDrives() => Drives;

    // ------------------------------------------------------------------

    private List<KeyValuePair<string, Entry>> Snapshot() => [.. _entries];

    private static bool IsIncluded(
        string path,
        string root,
        FileEnumerationOptions? options,
        Func<string, bool>? descendInto)
    {
        options ??= new FileEnumerationOptions();

        List<string> ancestors = Ancestors(path, root);

        if (!options.RecurseSubdirectories && ancestors.Count > 1)
        {
            return false;
        }

        if (descendInto is null)
        {
            return true;
        }

        // root 自身不参与剪枝（它是枚举根，被枚举就说明已经决定进入）
        foreach (string ancestor in ancestors.Skip(1))
        {
            if (!descendInto(ancestor))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>从 root 到 path 之间的祖先目录（不含 path 自身，含 root）。</summary>
    private static List<string> Ancestors(string path, string root)
    {
        var result = new List<string> { Trim(root) };
        string relative = path[Trim(root).Length..].TrimStart('\\');
        string[] segments = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);

        string current = Trim(root);
        for (int i = 0; i < segments.Length - 1; i++)
        {
            current = current + "\\" + segments[i];
            result.Add(current);
        }

        return result;
    }

    private static bool IsUnder(string candidate, string root)
    {
        string c = Trim(candidate);
        string r = Trim(root);
        return c.Equals(r, StringComparison.OrdinalIgnoreCase)
               || c.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Trim(string path) => path.TrimEnd('\\', '/');

    private sealed class Entry
    {
        public bool IsDirectory { get; init; }

        public long Length { get; set; }

        public DateTime LastWriteUtc { get; set; }
    }
}
