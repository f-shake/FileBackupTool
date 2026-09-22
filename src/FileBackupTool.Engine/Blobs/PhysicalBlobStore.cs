using System.Text.Json;
using System.Text.Json.Serialization;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Engine.Blobs;

/// <summary>
/// 内容寻址 blob 池。设计文档 02 §4 / §5。
/// </summary>
/// <remarks>
/// <code>
/// &lt;BackupDir&gt;\
/// ├─ blobs\
/// │  ├─ .tmp\                     ← 边拷边算的临时落地目录
/// │  └─ ab\cd\abcdef01…           ← 64 位 SHA-256 十六进制，裸文件，无扩展名
/// └─ _deleted\
///    ├─ blobs\ab\cd\abcdef01…     ← 回收站，目录结构保持不变
///    └─ manifest.json             ← 回收账本
/// </code>
/// <para>
/// 裸文件不带扩展名是<b>优点</b>：防止被备份软件、杀软、索引器二次处理。还原时从
/// <c>RelativePath</c> 重建扩展名。
/// </para>
/// </remarks>
public sealed class PhysicalBlobStore : IBlobStore
{
    public const string BlobsDirName = "blobs";
    public const string TempDirName = ".tmp";
    public const string DeletedDirName = "_deleted";
    public const string LedgerFileName = "manifest.json";

    private static readonly JsonSerializerOptions LedgerJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly IFileSystem _fs;
    private readonly IPathNormalizer _normalizer;
    private readonly IHashService _hash;
    private readonly string _backupDir;

    public PhysicalBlobStore(IFileSystem fs, IPathNormalizer normalizer, IHashService hash, string backupDir)
    {
        _fs = fs;
        _normalizer = normalizer;
        _hash = hash;
        _backupDir = normalizer.TrimTrailingSeparator(backupDir);
    }

    /// <summary>备份目录本身（账本、清单都相对它）。</summary>
    public string BackupDir => _backupDir;

    public string RootPath => _normalizer.Combine(_backupDir, BlobsDirName);

    public string RecycleBinRoot => _normalizer.Combine(_backupDir, DeletedDirName);

    private string TempRoot => _normalizer.Combine(RootPath, TempDirName);

    private string LedgerPath => _normalizer.Combine(RecycleBinRoot, LedgerFileName);

    public string GetRelativeBlobPath(string hash)
    {
        ValidateHash(hash);
        return _normalizer.Combine(_normalizer.Combine(BlobsDirName, Shard(hash, 0)),
            _normalizer.Combine(Shard(hash, 2), hash));
    }

    public string GetFullBlobPath(string hash) => _normalizer.Combine(_backupDir, GetRelativeBlobPath(hash));

    public string GetFullRecyclePath(string hash) =>
        _normalizer.Combine(RecycleBinRoot, _normalizer.Combine(BlobsDirName,
            _normalizer.Combine(_normalizer.Combine(Shard(hash, 0), Shard(hash, 2)), hash)));

    public bool Exists(string hash) => _fs.FileExists(GetFullBlobPath(hash));

    public bool ExistsInRecycleBin(string hash) => _fs.FileExists(GetFullRecyclePath(hash));

    public long GetLength(string hash) => _fs.GetFile(GetFullBlobPath(hash))?.Length ?? 0;

    public Stream OpenRead(string hash) => _fs.OpenRead(GetFullBlobPath(hash));

    public Stream CreateTemporary(out string token)
    {
        token = Guid.NewGuid().ToString("N");
        _fs.CreateDirectory(TempRoot);
        return _fs.Create(_normalizer.Combine(TempRoot, token));
    }

    public BlobPutResult CommitTemporary(string token, string hash)
    {
        ValidateHash(hash);
        string tempPath = _normalizer.Combine(TempRoot, token);
        string finalPath = GetFullBlobPath(hash);

        if (_fs.FileExists(finalPath))
        {
            // 池里已经有了：丢弃临时文件。**这次没有任何有效写入。**
            _fs.DeleteFile(tempPath);
            return new BlobPutResult(BlobPutOutcome.AlreadyExisted, hash, _fs.GetFile(finalPath)?.Length ?? 0);
        }

        long length = _fs.GetFile(tempPath)?.Length ?? 0;
        _fs.MoveFile(tempPath, finalPath, overwrite: false);
        return new BlobPutResult(BlobPutOutcome.Created, hash, length);
    }

    public void DiscardTemporary(string token)
    {
        try
        {
            _fs.DeleteFile(_normalizer.Combine(TempRoot, token));
        }
        catch (IOException)
        {
            // 临时文件本来就可能在，也可能已被清理
        }
    }

    public bool Recycle(string hash, string? lastKnownPath, long length, bool wasDirectory, DateTime movedAtUtc)
    {
        int moved = RecycleMany([new BlobRecycleRequest(hash, length, lastKnownPath, wasDirectory)], movedAtUtc);
        return moved > 0;
    }

    public int RecycleMany(IReadOnlyList<BlobRecycleRequest> requests, DateTime movedAtUtc)
    {
        if (requests.Count == 0)
        {
            return 0;
        }

        var ledger = ReadLedger();
        var byHash = ledger.Blobs.ToDictionary(e => e.Hash, StringComparer.Ordinal);
        int moved = 0;

        foreach (BlobRecycleRequest request in requests)
        {
            string source = GetFullBlobPath(request.Hash);
            if (!_fs.FileExists(source))
            {
                continue;
            }

            string destination = GetFullRecyclePath(request.Hash);

            // 同卷 -> 元数据操作，几乎零成本。跨卷会退化成整文件复制，
            // 所以回收站**必须在备份目录内、与 blob 池同卷**（设计文档 02 §5.1）。
            _fs.MoveFile(source, destination, overwrite: true);
            moved++;

            byHash[request.Hash] = new LedgerEntry
            {
                Hash = request.Hash,
                Length = request.Length,
                LastPath = request.LastKnownPath,
                MovedAt = movedAtUtc,
                WasDirectory = request.WasDirectory,
            };
        }

        if (moved > 0)
        {
            WriteLedger(new Ledger
            {
                MovedAt = movedAtUtc,
                Blobs = [.. byHash.Values.OrderBy(e => e.Hash, StringComparer.Ordinal)],
            });
        }

        return moved;
    }

    public RecycleBinStatus GetRecycleBinStatus()
    {
        var entries = new List<RecycleLedgerEntry>();
        var ledger = ReadLedger();
        var byHash = ledger.Blobs.ToDictionary(e => e.Hash, StringComparer.Ordinal);

        int count = 0;
        long total = 0;
        foreach (string hash in EnumerateRecycleBinHashes())
        {
            count++;
            long length = _fs.GetFile(GetFullRecyclePath(hash))?.Length ?? 0;
            total += length;

            if (byHash.TryGetValue(hash, out LedgerEntry? entry))
            {
                entries.Add(new RecycleLedgerEntry(hash, length, entry.LastPath, entry.MovedAt, entry.WasDirectory));
            }
            else
            {
                // 账本里没有（例如用户手动拷进来的）——仍然列出来，"待用户手动删除"不能变成"待用户盲删"
                entries.Add(new RecycleLedgerEntry(hash, length, null, default, false));
            }
        }

        return new RecycleBinStatus(count, total, entries);
    }

    public void ClearRecycleBin()
    {
        // 硬删除，不可逆。设计文档 06 §4.6 要求 UI 二次确认。
        _fs.DeleteDirectory(RecycleBinRoot, recursive: true);
    }

    public IEnumerable<string> EnumerateBlobHashes() => EnumerateHashes(RootPath, skipTemp: true);

    public IEnumerable<string> EnumerateRecycleBinHashes() =>
        EnumerateHashes(_normalizer.Combine(RecycleBinRoot, BlobsDirName), skipTemp: false);

    public void CleanTemporary()
    {
        if (!_fs.DirectoryExists(TempRoot))
        {
            return;
        }

        foreach (FileEntry file in _fs.EnumerateFiles(TempRoot, FileEnumerationOptions.Recursive))
        {
            try
            {
                _fs.DeleteFile(file.FullPath);
            }
            catch (IOException)
            {
                // 正在被占用就留着，下次再来
            }
        }
    }

    // ------------------------------------------------------------------

    private IEnumerable<string> EnumerateHashes(string root, bool skipTemp)
    {
        if (!_fs.DirectoryExists(root))
        {
            yield break;
        }

        string tempRoot = TempRoot;
        foreach (FileEntry file in _fs.EnumerateFiles(
                     root,
                     FileEnumerationOptions.Recursive,
                     descendInto: d => !skipTemp || !_normalizer.AreEqual(d, tempRoot)))
        {
            string name = _normalizer.GetFileName(file.FullPath);

            // 按**哈希格式**识别，而不是旧实现那种"文件名长度 == 32"的启发式——
            // 那会误伤用户自有的 32 字符文件名（设计文档 03 §8）。
            if (_hash.IsValidHash(name))
            {
                yield return name;
            }
        }
    }

    private static string Shard(string hash, int start) => hash.Substring(start, 2);

    private void ValidateHash(string hash)
    {
        if (!_hash.IsValidHash(hash))
        {
            throw new ArgumentException($"不是合法的 {_hash.Name} 哈希：{hash}", nameof(hash));
        }
    }

    private Ledger ReadLedger()
    {
        if (!_fs.FileExists(LedgerPath))
        {
            return new Ledger();
        }

        try
        {
            using Stream stream = _fs.OpenRead(LedgerPath);
            return JsonSerializer.Deserialize<Ledger>(stream, LedgerJsonOptions) ?? new Ledger();
        }
        catch (JsonException)
        {
            return new Ledger();
        }
        catch (IOException)
        {
            return new Ledger();
        }
    }

    private void WriteLedger(Ledger ledger)
    {
        _fs.CreateDirectory(RecycleBinRoot);
        using Stream stream = _fs.Create(LedgerPath);
        JsonSerializer.Serialize(stream, ledger, LedgerJsonOptions);
    }

    private sealed class Ledger
    {
        [JsonPropertyName("movedAt")]
        public DateTime MovedAt { get; set; }

        [JsonPropertyName("blobs")]
        public List<LedgerEntry> Blobs { get; set; } = [];
    }

    private sealed class LedgerEntry
    {
        [JsonPropertyName("hash")]
        public string Hash { get; set; } = string.Empty;

        [JsonPropertyName("length")]
        public long Length { get; set; }

        [JsonPropertyName("lastPath")]
        public string? LastPath { get; set; }

        [JsonPropertyName("movedAt")]
        public DateTime MovedAt { get; set; }

        [JsonPropertyName("wasDirectory")]
        public bool WasDirectory { get; set; }
    }
}
