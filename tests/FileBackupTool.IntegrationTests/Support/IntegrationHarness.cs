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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace FileBackupTool.IntegrationTests.Support;

/// <summary>
/// 真实文件系统上的测试工作区。
/// </summary>
/// <remarks>
/// 与单元测试的 `TestHarness` 的关键差别：**文件系统是真的**。
/// 这是设计文档 08 §1.1 的要求——"还原出来的文件必须和源文件逐字节一致"是备份工具存在的全部理由，
/// 所以它必须是 CI 硬门槛，而且必须在真实 IO 上验证（内存文件系统测不出编码、
/// 时间戳精度、长路径、独占锁、Unicode 文件名这些事）。
/// </remarks>
public sealed class IntegrationHarness : IAsyncDisposable
{
    private IntegrationHarness(string root, IPathNormalizer normalizer, PhysicalFileSystem fs)
    {
        Root = root;
        Normalizer = normalizer;
        Fs = fs;
        SourceDir = Path.Combine(root, "source");
        BackupDir = Path.Combine(root, "backup");
        RestoreDir = Path.Combine(root, "restore");
        DataDir = Path.Combine(root, "data");
    }

    public string Root { get; }

    public string SourceDir { get; }

    public string BackupDir { get; }

    public string RestoreDir { get; }

    public string DataDir { get; }

    public IPathNormalizer Normalizer { get; }

    public PhysicalFileSystem Fs { get; }

    public IDbContextFactory<AppDbContext> DbFactory { get; private set; } = null!;

    public TaskStore Tasks { get; private set; } = null!;

    public SettingsStore Settings { get; private set; } = null!;

    public FileRecordStore FileRecords { get; private set; } = null!;

    public SnapshotChainService Chains { get; private set; } = null!;

    public BlobStoreFactory BlobStores { get; private set; } = null!;

    public IBackupService Backup { get; private set; } = null!;

    public IRestoreService Restore { get; private set; } = null!;

    public IIntegrityService Integrity { get; private set; } = null!;

    public IMirrorQueryService MirrorQuery { get; private set; } = null!;

    public ISnapshotService Snapshots { get; private set; } = null!;

    public static async Task<IntegrationHarness> CreateAsync(Action<JobOptions>? configure = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "fbt-it", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var normalizer = WindowsPathNormalizer.Instance;
        var fs = new PhysicalFileSystem(normalizer);
        var harness = new IntegrationHarness(root, normalizer, fs);

        await harness.InitializeAsync(configure).ConfigureAwait(false);
        return harness;
    }

    private async Task InitializeAsync(Action<JobOptions>? configure)
    {
        Directory.CreateDirectory(SourceDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(RestoreDir);
        Directory.CreateDirectory(DataDir);

        var options = new JobOptions { IoRetryBaseDelaySeconds = 0 };
        configure?.Invoke(options);

        DbContextOptions<AppDbContext> dbOptions = DatabaseBootstrapper.BuildOptions(
            Path.Combine(DataDir, "filebackuptool.db"));

        DbFactory = new DelegateDbContextFactory(dbOptions);

        await using (AppDbContext db = await DbFactory.CreateDbContextAsync().ConfigureAwait(false))
        {
            await DatabaseBootstrapper.MigrateAsync(db).ConfigureAwait(false);
        }

        var clock = SystemClock.Instance;
        var hash = Sha256HashService.Instance;

        Tasks = new TaskStore(DbFactory, clock);
        Settings = new SettingsStore(DbFactory, clock);
        FileRecords = new FileRecordStore(DbFactory);
        Chains = new SnapshotChainService(DbFactory);
        BlobStores = new BlobStoreFactory(Fs, Normalizer, hash);

        var logStore = new LogStore(DbFactory);
        var logSink = new BackupLogSink(logStore, clock, NullLogger<BackupLogSink>.Instance);

        Integrity = new GarbageCollector(
            Tasks, Chains, FileRecords, BlobStores, hash, Fs, clock,
            Options.Create(new StorageOptions { DataDirectory = DataDir }),
            NullLogger<GarbageCollector>.Instance);

        Backup = new BackupEngine(
            DbFactory, Tasks, FileRecords, Chains, Fs, Normalizer, hash, BlobStores,
            NullManifestWriter.Instance, Integrity, clock, logSink,
            Options.Create(options),
            Options.Create(new StorageOptions { DataDirectory = DataDir }),
            NullLoggerFactory.Instance,
            NullLogger<BackupEngine>.Instance);

        Restore = new RestoreService(
            DbFactory, Tasks, Chains, Normalizer, Fs, BlobStores, Settings, logSink,
            Options.Create(options), NullLogger<RestoreService>.Instance);

        MirrorQuery = new MirrorQueryService(
            DbFactory, Chains, FileRecords, Normalizer, NullLogger<MirrorQueryService>.Instance);

        Snapshots = new SnapshotService(
            Chains, FileRecords, clock, logSink, NullLogger<SnapshotService>.Instance);

        await Settings.SetAllowedRestoreRootsAsync([RestoreDir], CancellationToken.None).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------

    /// <summary>在源目录里写一个文件（自动建父目录、写内容、设时间戳）。</summary>
    public void WriteSourceFile(string relativePath, string content, DateTime? fileTimeUtc = null) =>
        WriteSourceFile(relativePath, System.Text.Encoding.UTF8.GetBytes(content), fileTimeUtc);

    public void WriteSourceFile(string relativePath, byte[] content, DateTime? fileTimeUtc = null)
    {
        string full = Path.Combine(SourceDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);

        if (fileTimeUtc.HasValue)
        {
            File.SetLastWriteTimeUtc(full, fileTimeUtc.Value);
        }
    }

    public void DeleteSourceFile(string relativePath) => File.Delete(Path.Combine(SourceDir, relativePath));

    public void DeleteSourceDirectory(string relativePath) =>
        Directory.Delete(Path.Combine(SourceDir, relativePath), recursive: true);

    public void CreateSourceDirectory(string relativePath) =>
        Directory.CreateDirectory(Path.Combine(SourceDir, relativePath));

    public async Task<BackupTask> CreateTaskAsync(
        string name = "集成测试任务",
        string? sourceDir = null,
        string? backupDir = null)
    {
        var task = new BackupTask
        {
            Name = name,
            SourceDir = sourceDir ?? SourceDir,
            BackupDir = backupDir ?? BackupDir,
            IsEnabled = true,
        };

        await Tasks.AddAsync(task, CancellationToken.None).ConfigureAwait(false);
        return task;
    }

    public Task<BackupRunResult> BackupAsync(Guid taskId, SnapshotType? type = null) =>
        Backup.RunAsync(taskId, type, null, null, CancellationToken.None);

    public async Task<RestoreResult> RestoreAsync(Guid taskId, int snapshotId, string targetDir)
    {
        RestorePlan plan = await Restore.PlanAsync(
            new RestoreRequest(taskId, snapshotId, [], targetDir, RestoreConflictPolicy.Skip),
            CancellationToken.None).ConfigureAwait(false);

        return await Restore.ExecuteAsync(plan, null, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>还原到工作区里一个独立的子目录（避免多次还原互相干扰）。</summary>
    public Task<RestoreResult> RestoreToAsync(Guid taskId, int snapshotId, string subDirectory) =>
        RestoreAsync(taskId, snapshotId, Path.Combine(RestoreDir, subDirectory));

    public int CountBlobs() => BlobStores.Create(BackupDir).EnumerateBlobHashes().Count();

    // ------------------------------------------------------------------
    // 目录快照 / 比对
    // ------------------------------------------------------------------

    /// <summary>把一棵目录树读成 <c>相对路径 → 内容哈希</c>（用于逐字节比对）。</summary>
    public static Dictionary<string, string> SnapshotTree(string root, IPathNormalizer normalizer)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
        {
            return result;
        }

        foreach (string file in Directory.EnumerateFiles(root, "*", new EnumerationOptions
                 {
                     RecurseSubdirectories = true,
                     IgnoreInaccessible = true,
                     AttributesToSkip = 0,
                 }))
        {
            string relative = normalizer.GetRelativePathOrNull(root, file) ?? file;
            using var stream = File.OpenRead(file);
            result[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }

        return result;
    }

    /// <summary>断言两棵目录树逐字节一致（内容 + 文件集合 + 相对路径）。</summary>
    public static void AssertTreesIdentical(string expectedRoot, string actualRoot, IPathNormalizer normalizer)
    {
        Dictionary<string, string> expected = SnapshotTree(expectedRoot, normalizer);
        Dictionary<string, string> actual = SnapshotTree(actualRoot, normalizer);

        Assert.Equal(
            expected.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase),
            actual.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));

        foreach ((string relative, string hash) in expected)
        {
            Assert.Equal(hash, actual[relative]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // 清理失败不该让测试失败
        }
    }

    private sealed class DelegateDbContextFactory(DbContextOptions<AppDbContext> options)
        : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppDbContext(options));
    }
}
