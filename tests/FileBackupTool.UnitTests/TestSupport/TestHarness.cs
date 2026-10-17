using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Hashing;
using FileBackupTool.Core.IO;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using FileBackupTool.Engine;
using FileBackupTool.Engine.Blobs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileBackupTool.UnitTests.TestSupport;

/// <summary>记录调用次数的清单写入器替身。</summary>
public sealed class RecordingManifestWriter : IManifestWriter
{
    public int CallCount { get; private set; }

    public IReadOnlyCollection<string> LastTouchedPaths { get; private set; } = [];

    public Task<ManifestWriteResult> WriteAsync(
        BackupTask task,
        BackupSnapshot latestSnapshot,
        IReadOnlyCollection<string> touchedRelativePaths,
        CancellationToken ct)
    {
        CallCount++;
        LastTouchedPaths = touchedRelativePaths;
        return Task.FromResult(new ManifestWriteResult(
            "recording", 0, latestSnapshot.ImageFileCount, latestSnapshot.ImageTotalBytes, 0));
    }
}

/// <summary>
/// 把引擎的整套服务装配起来，全部跑在内存文件系统 + 真实临时 SQLite 上。
/// </summary>
public sealed class TestHarness : IAsyncDisposable
{
    public const string SourceRoot = @"C:\source";
    public const string BackupRoot = @"C:\backup";
    public const string RestoreRoot = @"C:\restore";

    private TestHarness(TestDatabase db, InMemoryFileSystem fs, FakeClock clock, JobOptions jobOptions)
    {
        Db = db;
        Fs = fs;
        Clock = clock;
        Normalizer = WindowsPathNormalizer.Instance;
        JobOptions = jobOptions;

        TaskStore = new TaskStore(db.Factory, clock);
        SettingsStore = new SettingsStore(db.Factory, clock);
        FileRecords = new FileRecordStore(db.Factory);
        Chains = new SnapshotChainService(db.Factory);
        LogStore = new LogStore(db.Factory);
        BlobStores = new BlobStoreFactory(fs, Normalizer, Hash);
        LogSink = new BackupLogSink(LogStore, clock, NullLogger<BackupLogSink>.Instance);
        ManifestWriter = new RecordingManifestWriter();

        Integrity = new GarbageCollector(
            TaskStore, Chains, FileRecords, BlobStores, Hash, fs, clock,
            Options.Create(new StorageOptions()), NullLogger<GarbageCollector>.Instance);

        Backup = new BackupEngine(
            db.Factory, TaskStore, FileRecords, Chains, fs, Normalizer, Hash, BlobStores,
            ManifestWriter, Integrity, clock, LogSink, IoSwitch,
            Options.Create(jobOptions), Options.Create(new StorageOptions()),
            NullLoggerFactory.Instance, NullLogger<BackupEngine>.Instance);

        Restore = new RestoreService(
            db.Factory, TaskStore, Chains, Normalizer, fs, BlobStores, SettingsStore, LogSink,
            Options.Create(jobOptions), NullLogger<RestoreService>.Instance);

        MirrorQuery = new MirrorQueryService(
            db.Factory, Chains, FileRecords, Normalizer, NullLogger<MirrorQueryService>.Instance);

        Snapshots = new SnapshotService(
            Chains, FileRecords, clock, LogSink, NullLogger<SnapshotService>.Instance);

        Fs.AddDirectory(SourceRoot);
        Fs.AddDirectory(BackupRoot);
        Fs.AddDirectory(RestoreRoot);
    }

    public TestDatabase Db { get; }

    public InMemoryFileSystem Fs { get; }

    public FakeClock Clock { get; }

    public IPathNormalizer Normalizer { get; }

    public JobOptions JobOptions { get; }

    public IHashService Hash { get; } = Sha256HashService.Instance;

    /// <summary>低 IO 优先级开关（测试里默认开启，但内存文件系统没有句柄，实际是空操作）。</summary>
    public IoPrioritySwitch IoSwitch { get; } = new();

    public TaskStore TaskStore { get; }

    public SettingsStore SettingsStore { get; }

    public FileRecordStore FileRecords { get; }

    public SnapshotChainService Chains { get; }

    public LogStore LogStore { get; }

    public BlobStoreFactory BlobStores { get; }

    public BackupLogSink LogSink { get; }

    public RecordingManifestWriter ManifestWriter { get; }

    public IBackupService Backup { get; }

    public IRestoreService Restore { get; }

    public IIntegrityService Integrity { get; }

    public IMirrorQueryService MirrorQuery { get; }

    public ISnapshotService Snapshots { get; }

    public static async Task<TestHarness> CreateAsync(Action<JobOptions>? configure = null)
    {
        var options = new JobOptions
        {
            // 测试里把重试的退避压到 0，避免真的等待
            IoRetryBaseDelaySeconds = 0,
            DbBatchSize = 50,

            // **默认关闭"文件年龄"守卫**：绝大多数用例关心的是别的行为，而它们写文件时
            // 用的时间戳就是"刚刚"——守卫开着会让每个用例都跳过全部文件。
            // 专门验证守卫的用例在 configure 里把它打开。
            MinFileAgeSeconds = 0,
        };

        configure?.Invoke(options);

        TestDatabase db = await TestDatabase.CreateAsync().ConfigureAwait(false);
        var fs = new InMemoryFileSystem(WindowsPathNormalizer.Instance);
        var clock = new FakeClock();
        return new TestHarness(db, fs, clock, options);
    }

    /// <summary>把一个源文件写进内存文件系统。</summary>
    public void AddSourceFile(string relativePath, string content, DateTime? fileTimeUtc = null) =>
        Fs.AddTextFile(Combine(SourceRoot, relativePath), content, fileTimeUtc);

    public void AddSourceFile(string relativePath, byte[] content, DateTime? fileTimeUtc = null) =>
        Fs.AddFile(Combine(SourceRoot, relativePath), content, fileTimeUtc);

    public void RemoveSourceFile(string relativePath) => Fs.DeleteFile(Combine(SourceRoot, relativePath));

    public string SourcePath(string relativePath) => Combine(SourceRoot, relativePath);

    public string RestorePath(string relativePath) => Combine(RestoreRoot, relativePath);

    public static string Combine(string root, string relative) =>
        WindowsPathNormalizer.Instance.Combine(root, relative);

    public async Task<BackupTask> CreateTaskAsync(
        string name = "测试任务",
        string? sourceDir = null,
        string? backupDir = null,
        bool byInterval = false,
        int maxIncrements = 30,
        bool virtualDefault = false,
        string filterJson = "{}")
    {
        var task = new BackupTask
        {
            Name = name,
            SourceDir = sourceDir ?? SourceRoot,
            BackupDir = backupDir ?? BackupRoot,
            ByTimeInterval = byInterval,
            TimeIntervalMinutes = 60,
            MaxAutoIncrementBackupCount = maxIncrements,
            IsDefaultVirtualBackup = virtualDefault,
            FilterJson = filterJson,
            IsEnabled = true,
        };

        await TaskStore.AddAsync(task, CancellationToken.None).ConfigureAwait(false);
        return task;
    }

    public Task<BackupRunResult> BackupAsync(
        Guid taskId,
        SnapshotType? type = null,
        IReadOnlyList<string>? dirtyScopes = null) =>
        Backup.RunAsync(taskId, type, dirtyScopes, null, CancellationToken.None);

    /// <summary>重建"当前镜像"（路径相对源目录 → 内容哈希）。用于往返一致性断言。</summary>
    public async Task<Dictionary<string, string>> ReadImageAsync(Guid taskId, int snapshotId)
    {
        await using AppDbContext db = await Db.OpenAsync().ConfigureAwait(false);
        List<int> chainIds = [];
        List<BackupSnapshot> chain =
            await Chains.ResolveChainAsync(taskId, snapshotId, CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("链断裂");

        chainIds.AddRange(chain.Select(s => s.Id));

        List<BackupFile> records = await FileRecords
            .GetChainRecordsInPrefixAsync(taskId, chainIds, null, CancellationToken.None)
            .ConfigureAwait(false);

        var replayed = FileBackupTool.Engine.Mirror.MirrorReplay.Replay(records);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (FileBackupTool.Engine.Mirror.MirrorItem item in replayed.Items.Values)
        {
            result[item.Path] = item.ContentHash ?? "<virtual>";
        }

        return result;
    }

    /// <summary>读取 blob 池里的内容（按哈希）。</summary>
    public string ReadBlobText(string hash, string backupDir = BackupRoot)
    {
        PhysicalBlobStore store = BlobStores.Create(backupDir);
        using Stream stream = store.OpenRead(hash);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public int CountBlobs(string backupDir = BackupRoot) =>
        BlobStores.Create(backupDir).EnumerateBlobHashes().Count();

    public int CountRecycleBinBlobs(string backupDir = BackupRoot) =>
        BlobStores.Create(backupDir).EnumerateRecycleBinHashes().Count();

    public async ValueTask DisposeAsync() => await Db.DisposeAsync().ConfigureAwait(false);
}
