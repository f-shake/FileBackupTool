using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.UnitTests.TestSupport;

/// <summary>
/// 内存文件系统。引擎/还原/回收的全部单元测试都跑在它上面。
/// </summary>
/// <remarks>
/// 两个刻意的设计：
/// <list type="number">
/// <item>内部用 <see cref="IPathNormalizer.Normalize"/> 之后的键存条目，但节点里保留
/// <b>原始大小写</b>——与 Windows 的真实行为一致（查找不区分大小写，枚举返回磁盘上的原样名字）。
/// 这样"大小写归一化"这类测试才有意义。</item>
/// <item>统计 IO 次数（<see cref="ReadOpenCount"/> / <see cref="CreateCount"/> 等）。
/// 设计文档 08 §1.4 要求断言"<c>Length</c> 预筛的无同长度分支不产生白写"，
/// 没有计数就没法断言。</item>
/// </list>
/// </remarks>
public sealed class InMemoryFileSystem(IPathNormalizer normalizer) : IFileSystem
{
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

    /// <summary>打开读取的次数（每调用一次 <see cref="OpenRead"/> 加一）。</summary>
    public int ReadOpenCount { get; private set; }

    /// <summary>创建写入的次数（每调用一次 <see cref="Create"/> 加一）。</summary>
    public int CreateCount { get; private set; }

    public int MoveCount { get; private set; }

    public int DeleteCount { get; private set; }

    public int EnumerateDirectoryCount { get; private set; }

    /// <summary>对某个路径的读操作应抛出的异常工厂（故障注入）。</summary>
    public Func<string, Exception?>? OpenReadFailure { get; set; }

    /// <summary>对某个路径的写操作应抛出的异常工厂（故障注入）。</summary>
    public Func<string, Exception?>? CreateFailure { get; set; }

    /// <summary>枚举某个目录时应抛出的异常工厂（故障注入）。</summary>
    public Func<string, Exception?>? EnumerateFailure { get; set; }

    /// <summary>
    /// 路径长度超过此值时，**读取**抛 <see cref="PathTooLongException"/>。null 表示不模拟。
    /// </summary>
    /// <remarks>
    /// 只作用于读取。若也作用于写入，备份目录/blobs 的路径本来就比源路径长，
    /// 设一个"只让某个源文件超长"的阈值会连 blob 临时文件一起打掉，
    /// 那就测不到"单个源文件因超长路径失败而被隔离计数"这件事了。
    /// </remarks>
    public int? PathTooLongThreshold { get; set; }

    /// <summary>新增/覆盖一个文件。测试里构造源数据用。</summary>
    public void AddFile(string fullPath, byte[] content, DateTime? fileTimeUtc = null, FileAttributes attributes = 0)
    {
        EnsureParentDirectories(fullPath);
        _nodes[Key(fullPath)] = new Node
        {
            FullPath = fullPath,
            IsDirectory = false,
            Content = content,
            FileTimeUtc = fileTimeUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Attributes = attributes,
        };
    }

    public void AddTextFile(string fullPath, string content, DateTime? fileTimeUtc = null) =>
        AddFile(fullPath, System.Text.Encoding.UTF8.GetBytes(content), fileTimeUtc);

    public void AddDirectory(string fullPath)
    {
        EnsureParentDirectories(fullPath);
        string key = Key(fullPath);
        if (!_nodes.ContainsKey(key))
        {
            _nodes[key] = new Node
            {
                FullPath = fullPath,
                IsDirectory = true,
                Content = null,
                FileTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                Attributes = 0,
            };
        }
    }

    public string? ReadText(string fullPath) =>
        _nodes.TryGetValue(Key(fullPath), out Node? node) && node.Content is not null
            ? System.Text.Encoding.UTF8.GetString(node.Content)
            : null;

    /// <summary>当前所有文件路径（原始大小写）。</summary>
    public IReadOnlyList<string> AllFilePaths() =>
        [.. _nodes.Values.Where(n => !n.IsDirectory).Select(n => n.FullPath).OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>把"落盘"的字节数加总（用于断言"没有白写"）。</summary>
    public long TotalBytesWritten { get; private set; }

    // ----------------------------------------------------------------

    public bool FileExists(string fullPath) =>
        _nodes.TryGetValue(Key(fullPath), out Node? node) && !node.IsDirectory;

    public bool DirectoryExists(string fullPath) =>
        _nodes.TryGetValue(Key(fullPath), out Node? node) && node.IsDirectory;

    public FileEntry? GetFile(string fullPath)
    {
        if (!_nodes.TryGetValue(Key(fullPath), out Node? node) || node.IsDirectory)
        {
            return null;
        }

        return new FileEntry(node.FullPath, node.Content?.LongLength ?? 0, node.FileTimeUtc, node.Attributes);
    }

    public DirectoryEntry? GetDirectory(string fullPath)
    {
        if (!_nodes.TryGetValue(Key(fullPath), out Node? node) || !node.IsDirectory)
        {
            return null;
        }

        return new DirectoryEntry(node.FullPath, node.FileTimeUtc, node.Attributes);
    }

    public void CreateDirectory(string fullPath) => AddDirectory(fullPath);

    public IEnumerable<FileEntry> EnumerateFiles(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        FileEnumerationOptions opt = options ?? FileEnumerationOptions.Recursive;

        foreach ((string path, Node node) in EnumerateEntries(rootFullPath, opt, descendInto))
        {
            if (!node.IsDirectory && (node.Attributes & opt.AttributesToSkip) == 0)
            {
                yield return new FileEntry(node.FullPath, node.Content?.LongLength ?? 0, node.FileTimeUtc, node.Attributes);
            }
        }
    }

    public IEnumerable<DirectoryEntry> EnumerateDirectories(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null)
    {
        FileEnumerationOptions opt = options ?? FileEnumerationOptions.Recursive;

        foreach ((string path, Node node) in EnumerateEntries(rootFullPath, opt, descendInto))
        {
            if (node.IsDirectory && !_normalizer.AreEqual(path, rootFullPath)
                && (node.Attributes & opt.AttributesToSkip) == 0)
            {
                yield return new DirectoryEntry(node.FullPath, node.FileTimeUtc, node.Attributes);
            }
        }
    }

    public Stream OpenRead(string fullPath)
    {
        ReadOpenCount++;

        Exception? injected = OpenReadFailure?.Invoke(fullPath);
        if (injected is not null)
        {
            throw injected;
        }

        if (PathTooLongThreshold is int limit && fullPath.Length >= limit)
        {
            throw new PathTooLongException($"模拟的超长路径：{fullPath}");
        }

        if (!_nodes.TryGetValue(Key(fullPath), out Node? node) || node.IsDirectory)
        {
            throw new FileNotFoundException("文件不存在。", fullPath);
        }

        return new MemoryStream(node.Content ?? [], writable: false);
    }

    public Stream Create(string fullPath)
    {
        CreateCount++;

        Exception? injected = CreateFailure?.Invoke(fullPath);
        if (injected is not null)
        {
            throw injected;
        }

        EnsureParentDirectories(fullPath);
        return new WriteBackStream(this, fullPath);
    }

    public void MoveFile(string sourceFullPath, string destinationFullPath, bool overwrite)
    {
        MoveCount++;

        if (!_nodes.TryGetValue(Key(sourceFullPath), out Node? node))
        {
            throw new FileNotFoundException("源文件不存在。", sourceFullPath);
        }

        string destinationKey = Key(destinationFullPath);
        if (_nodes.ContainsKey(destinationKey) && !overwrite)
        {
            throw new IOException($"目标已存在：{destinationFullPath}");
        }

        EnsureParentDirectories(destinationFullPath);
        _nodes.Remove(Key(sourceFullPath));
        node.FullPath = destinationFullPath;
        _nodes[destinationKey] = node;
    }

    public void DeleteFile(string fullPath)
    {
        DeleteCount++;
        _nodes.Remove(Key(fullPath));
    }

    public void DeleteDirectory(string fullPath, bool recursive)
    {
        string prefix = Key(fullPath) + "\\";
        var toRemove = _nodes
            .Where(kv => kv.Key.Equals(Key(fullPath), StringComparison.Ordinal)
                         || kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();

        foreach (string key in toRemove)
        {
            _nodes.Remove(key);
        }
    }

    public void SetLastWriteTimeUtc(string fullPath, DateTime lastWriteTimeUtc)
    {
        if (_nodes.TryGetValue(Key(fullPath), out Node? node))
        {
            node.FileTimeUtc = lastWriteTimeUtc;
        }
    }

    public IReadOnlyList<DriveEntry> GetLogicalDrives() => [new DriveEntry(@"C:\", DriveType.Fixed)];

    // ----------------------------------------------------------------

    internal void CommitWrite(string fullPath, byte[] content)
    {
        EnsureParentDirectories(fullPath);
        TotalBytesWritten += content.LongLength;
        _nodes[Key(fullPath)] = new Node
        {
            FullPath = fullPath,
            IsDirectory = false,
            Content = content,
            FileTimeUtc = _nodes.TryGetValue(Key(fullPath), out Node? existing)
                ? existing.FileTimeUtc
                : new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Attributes = 0,
        };
    }

    private IEnumerable<(string Path, Node Node)> EnumerateEntries(
        string rootFullPath,
        FileEnumerationOptions options,
        Func<string, bool>? descendInto)
    {
        EnumerateDirectoryCount++;

        Exception? injected = EnumerateFailure?.Invoke(rootFullPath);
        if (injected is not null)
        {
            if (options.IgnoreInaccessible)
            {
                yield break;
            }

            throw injected;
        }

        List<Node> children = Children(Key(rootFullPath));

        foreach (Node child in children)
        {
            yield return (child.FullPath, child);
        }

        if (!options.RecurseSubdirectories)
        {
            yield break;
        }

        foreach (Node child in children.Where(c => c.IsDirectory))
        {
            if (descendInto is not null && !descendInto(child.FullPath))
            {
                continue;
            }

            foreach ((string path, Node node) in EnumerateEntries(child.FullPath, options, descendInto))
            {
                yield return (path, node);
            }
        }
    }

    private List<Node> Children(string rootKey) =>
        [.. _nodes.Values
            .Where(n => IsDirectChild(rootKey, n.FullPath))
            .OrderBy(n => n.FullPath, StringComparer.Ordinal)];

    private bool IsDirectChild(string rootKey, string candidateFullPath)
    {
        string candidateKey = Key(candidateFullPath);
        if (candidateKey.Equals(rootKey, StringComparison.Ordinal))
        {
            return false;
        }

        string prefix = rootKey.EndsWith('\\') ? rootKey : rootKey + "\\";
        if (!candidateKey.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return candidateKey[prefix.Length..].IndexOf('\\') < 0;
    }

    private void EnsureParentDirectories(string fullPath)
    {
        string? parent = _normalizer.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(parent))
        {
            string key = Key(parent);
            if (!_nodes.ContainsKey(key))
            {
                _nodes[key] = new Node
                {
                    FullPath = parent,
                    IsDirectory = true,
                    FileTimeUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                };
            }

            string? next = _normalizer.GetDirectoryName(parent);
            if (next == parent || string.IsNullOrEmpty(next))
            {
                break;
            }

            parent = next;
        }
    }

    private string Key(string path) => _normalizer.Normalize(path);

    private sealed class Node
    {
        public required string FullPath { get; set; }

        public bool IsDirectory { get; init; }

        public byte[]? Content { get; set; }

        public DateTime FileTimeUtc { get; set; }

        public FileAttributes Attributes { get; set; }
    }

    /// <summary>写入流：Dispose 时才把内容提交进文件表（模拟"写完才可见"）。</summary>
    private sealed class WriteBackStream(InMemoryFileSystem owner, string fullPath) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => _buffer.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => _buffer.Write(buffer);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                owner.CommitWrite(fullPath, _buffer.ToArray());
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            owner.CommitWrite(fullPath, _buffer.ToArray());
            await _buffer.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
