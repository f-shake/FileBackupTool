using System.Collections.Concurrent;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Core.IO;

/// <summary>
/// 真实的文件系统实现。**唯一允许出现 <c>System.IO</c> 文件操作的地方。**
/// </summary>
/// <remarks>
/// 自己实现递归（而不是用 <c>EnumerationOptions.RecurseSubdirectories</c>），原因有二：
/// <list type="number">
/// <item>需要按目录剪枝（<c>descendInto</c>）——设计文档 03 §3 的 exclude 剪枝与 §5.4 的局部扫描。</item>
/// <item>需要**不进入重解析点**（目录符号链接 / junction）。.NET 的递归会跟随它们，
/// 遇到自引用链接就是无限递归。跳过这件事通过 <see cref="IDirectorySkipReporter"/> 交出去，不静默。</item>
/// </list>
/// </remarks>
public sealed class PhysicalFileSystem : IFileSystem, IDirectorySkipReporter
{
    private readonly IPathNormalizer _normalizer;
    private readonly ConcurrentQueue<SkippedDirectory> _skipped = new();

    public PhysicalFileSystem(IPathNormalizer normalizer) => _normalizer = normalizer;

    public bool FileExists(string fullPath) => File.Exists(Extend(fullPath));

    public bool DirectoryExists(string fullPath) => Directory.Exists(Extend(fullPath));

    public FileEntry? GetFile(string fullPath)
    {
        string p = Extend(fullPath);
        try
        {
            var info = new FileInfo(p);
            if (!info.Exists)
            {
                return null;
            }

            return new FileEntry(Shrink(info.FullName), info.Length, info.LastWriteTimeUtc, info.Attributes);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            return null;
        }
    }

    /// <summary>
    /// 是否重解析点（符号链接 / junction），**不跟随**链接。
    /// </summary>
    /// <remarks>
    /// 用 <c>File.GetAttributes</c>：它读的是**目录项自身**，因此对指向不存在目标的链接
    /// 同样返回 <c>ReparsePoint</c>（实测确认，.NET 10 / Windows 10）。
    /// 这正是它必须独立于 <c>Exists</c> 的原因——悬空的**目录**链接在
    /// <c>DirectoryInfo.Exists</c> 眼里是"不存在"，但它会把写操作引到别处。
    /// </remarks>
    public bool IsReparsePoint(string fullPath)
    {
        try
        {
            return (File.GetAttributes(Extend(fullPath)) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            // 确实不存在的路径会抛 FileNotFoundException —— 那本来就不是重解析点
            return false;
        }
    }

    public DirectoryEntry? GetDirectory(string fullPath)
    {
        string p = Extend(fullPath);        try
        {
            var info = new DirectoryInfo(p);
            if (!info.Exists)
            {
                return null;
            }

            return new DirectoryEntry(Shrink(info.FullName), info.LastWriteTimeUtc, info.Attributes);
        }
        catch (Exception ex) when (IsBenign(ex))
        {
            return null;
        }
    }

    public void CreateDirectory(string fullPath) => Directory.CreateDirectory(Extend(fullPath));

    public IEnumerable<FileEntry> EnumerateFiles(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        FileEnumerationOptions opt = options ?? FileEnumerationOptions.Recursive;
        var files = new List<FileEntry>();
        var childDirs = new List<string>();

        // 逐目录物化，而不是整棵树物化：百万文件全程流式的关键。
        ReadDirectory(rootFullPath, opt, files, childDirs, reportSkipped: true);

        foreach (FileEntry f in files)
        {
            yield return f;
        }

        if (!opt.RecurseSubdirectories)
        {
            yield break;
        }

        foreach (string childDir in childDirs)
        {
            if (descendInto is not null && !descendInto(childDir))
            {
                continue;
            }

            foreach (FileEntry f in EnumerateFiles(childDir, opt, descendInto))
            {
                yield return f;
            }
        }
    }

    public IEnumerable<DirectoryEntry> EnumerateDirectories(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        FileEnumerationOptions opt = options ?? FileEnumerationOptions.Recursive;
        var files = new List<FileEntry>();
        var childDirs = new List<string>();

        ReadDirectory(rootFullPath, opt, files, childDirs, reportSkipped: false);

        foreach (string childDir in childDirs)
        {
            DirectoryEntry? entry = TryDescribe(childDir, opt.IgnoreInaccessible);
            if (entry.HasValue && (entry.Value.Attributes & opt.AttributesToSkip) == 0)
            {
                yield return entry.Value;
            }
        }

        if (!opt.RecurseSubdirectories)
        {
            yield break;
        }

        foreach (string childDir in childDirs)
        {
            if (descendInto is not null && !descendInto(childDir))
            {
                continue;
            }

            foreach (DirectoryEntry d in EnumerateDirectories(childDir, opt, descendInto))
            {
                yield return d;
            }
        }
    }

    public Stream OpenRead(string fullPath, bool backgroundIo = false)
    {
        var stream = new FileStream(Extend(fullPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            Streams.StreamHelper.DefaultBufferSize, FileOptions.SequentialScan);

        if (backgroundIo)
        {
            // 让路是"锦上添花"：非 Windows、文件系统不支持、句柄不允许都会安静地返回 false，
            // 不影响这次读取（TryApplyBackground 约定永不抛）。
            IoPriority.TryApplyBackground(stream.SafeFileHandle);
        }

        return stream;
    }

    public Stream Create(string fullPath)
    {
        string p = Extend(fullPath);
        string? parent = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        // 设计文档 03 §3.5：**不使用 WriteThrough**。
        // WriteThrough 绕过操作系统写缓存，每次写都要等底层介质确认才返回，
        // 正好废掉写回缓存本来能做的批量合并与重排——与机械盘是最不利的组合。
        // 备份是可重跑的，最坏情况是某个 blob 写了一半，而内容寻址天然能发现
        // （文件名就是哈希，重算即可验证）。用"可检测、可重做"换吞吐是划算的。
        return new FileStream(p, FileMode.Create, FileAccess.Write, FileShare.None,
            Streams.StreamHelper.DefaultBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public void MoveFile(string sourceFullPath, string destinationFullPath, bool overwrite)
    {
        string dst = Extend(destinationFullPath);
        string? parent = Path.GetDirectoryName(dst);
        if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Move(Extend(sourceFullPath), dst, overwrite);
    }

    public void DeleteFile(string fullPath)
    {
        string p = Extend(fullPath);
        if (File.Exists(p))
        {
            File.Delete(p);
        }
    }

    public void DeleteDirectory(string fullPath, bool recursive)
    {
        string p = Extend(fullPath);
        if (Directory.Exists(p))
        {
            Directory.Delete(p, recursive);
        }
    }

    public void SetLastWriteTimeUtc(string fullPath, DateTime lastWriteTimeUtc) =>
        File.SetLastWriteTimeUtc(Extend(fullPath), lastWriteTimeUtc);

    public IReadOnlyList<DriveEntry> GetLogicalDrives()
    {
        var result = new List<DriveEntry>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (IOException)
        {
            return result;
        }

        foreach (DriveInfo d in drives)
        {
            try
            {
                if (!d.IsReady && d.DriveType is DriveType.Network or DriveType.NoRootDirectory)
                {
                    // 未就绪的网络盘仍然列出来，但类型照实报
                }

                result.Add(new DriveEntry(d.RootDirectory.FullName, d.DriveType));
            }
            catch (Exception ex) when (IsBenign(ex))
            {
                // 单个驱动器探测失败不影响其他驱动器
            }
        }

        return result;
    }

    public IReadOnlyList<SkippedDirectory> DrainSkippedDirectories()
    {
        var list = new List<SkippedDirectory>();
        while (_skipped.TryDequeue(out SkippedDirectory s))
        {
            list.Add(s);
        }

        return list;
    }

    // ------------------------------------------------------------------

    private void ReadDirectory(
        string dirFullPath,
        FileEnumerationOptions options,
        List<FileEntry> files,
        List<string> childDirs,
        bool reportSkipped)
    {
        string extended = Extend(dirFullPath);
        try
        {
            var di = new DirectoryInfo(extended);
            if (!di.Exists)
            {
                return;
            }

            foreach (FileSystemInfo info in di.EnumerateFileSystemInfos("*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                IgnoreInaccessible = options.IgnoreInaccessible,
                AttributesToSkip = 0,
                ReturnSpecialDirectories = false,
            }))
            {
                FileAttributes attrs;
                string full;
                DateTime mtime;
                long length;

                try
                {
                    attrs = info.Attributes;
                    full = Shrink(info.FullName);
                    mtime = info.LastWriteTimeUtc;
                    length = info is FileInfo fi ? fi.Length : 0;
                }
                catch (Exception ex) when (IsBenign(ex))
                {
                    // 单个条目元数据读不出来就跳过它（并让上层的失败计数/日志体现出来）
                    continue;
                }

                if ((attrs & options.AttributesToSkip) != 0)
                {
                    continue;
                }

                if ((attrs & FileAttributes.Directory) != 0)
                {
                    if ((attrs & FileAttributes.ReparsePoint) != 0)
                    {
                        // 目录符号链接 / junction：**不进入**（否则自引用链接会无限递归）。
                        // 但这件事必须让用户看得见——"不静默"是设计文档 03 §7 的立场。
                        if (reportSkipped)
                        {
                            _skipped.Enqueue(new SkippedDirectory(
                                full, "目录重解析点（符号链接/junction），未进入", Deliberate: true));
                        }

                        continue;
                    }

                    childDirs.Add(full);
                }
                else
                {
                    files.Add(new FileEntry(full, length, mtime, attrs));
                }
            }
        }
        catch (Exception ex) when (options.IgnoreInaccessible && IsBenign(ex))
        {
            if (reportSkipped)
            {
                _skipped.Enqueue(new SkippedDirectory(Shrink(extended), $"无法枚举目录：{ex.GetType().Name}"));
            }
        }
    }

    private DirectoryEntry? TryDescribe(string fullPath, bool ignoreInaccessible)
    {
        try
        {
            var di = new DirectoryInfo(Extend(fullPath));
            return di.Exists
                ? new DirectoryEntry(Shrink(di.FullName), di.LastWriteTimeUtc, di.Attributes)
                : null;
        }
        catch (Exception ex) when (ignoreInaccessible && IsBenign(ex))
        {
            return null;
        }
    }

    private string Extend(string path)
    {
        if (string.IsNullOrEmpty(path) || !_normalizer.IsAbsolute(path))
        {
            return path;
        }

        return _normalizer.ToExtendedPath(_normalizer.TrimTrailingSeparator(path));
    }

    /// <summary>
    /// 去掉 <c>\\?\</c> / <c>\\?\UNC\</c> 前缀，让外部看到的是普通路径。
    /// 否则枚举返回的路径与实际传入的根对不上，相对路径计算会全线失效。
    /// </summary>
    private static string Shrink(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static bool IsBenign(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or NotSupportedException or ArgumentException;
}
