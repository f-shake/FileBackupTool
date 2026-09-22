using System.Text.Json;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using FileBackupTool.Engine.Manifests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace FileBackupTool.UnitTests.TestSupport;

/// <summary>
/// 抢救清单写入器的测试夹具：**真实临时 SQLite** + 内存文件系统。
/// </summary>
/// <remarks>
/// 用真库而不是假仓储，理由与 <see cref="TestDatabase"/> 相同：清单的分片靠
/// <c>PreviousMirrorTable</c> 的 SQLite TEMP 表与 LIKE 前缀语义实现，
/// 换成内存字典就把真正会出错的地方（前缀转义、每路径取最新记录）绕过去了。
/// <para>
/// 刻意**不复用 <see cref="TestHarness"/>**：那个夹具把清单写入器换成了替身，
/// 而这里要测的正是真身。夹具直接往库里塞快照与文件记录，构造出各种链形状。
/// </para>
/// </remarks>
public sealed class ManifestTestFixture : IAsyncDisposable
{
    public const string SourceRoot = @"C:\source";
    public const string BackupRoot = @"C:\backup";
    public const string ManifestDirPath = @"C:\backup\.manifest";
    public const string ManifestFilePath = @"C:\backup\.manifest.json";
    public const string RootShardPath = @"C:\backup\.manifest\_root.json";

    private DateTime _nextBeginUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private ManifestTestFixture(TestDatabase db, InMemoryFileSystem fs, FakeClock clock, ManifestWriter writer)
    {
        Db = db;
        Fs = fs;
        Clock = clock;
        Writer = writer;

        Fs.AddDirectory(SourceRoot);
        Fs.AddDirectory(BackupRoot);
    }

    public TestDatabase Db { get; }

    public InMemoryFileSystem Fs { get; }

    public FakeClock Clock { get; }

    public ManifestWriter Writer { get; }

    public IPathNormalizer Normalizer { get; } = WindowsPathNormalizer.Instance;

    public Guid TaskId { get; private set; }

    public string TaskName { get; private set; } = "文档备份";

    public static async Task<ManifestTestFixture> CreateAsync(StorageOptions? storage = null)
    {
        TestDatabase db = await TestDatabase.CreateAsync().ConfigureAwait(false);
        var fs = new InMemoryFileSystem(WindowsPathNormalizer.Instance);
        var clock = new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var writer = new ManifestWriter(
            db.Factory,
            new SnapshotChainService(db.Factory),
            fs,
            WindowsPathNormalizer.Instance,
            clock,
            Options.Create(storage ?? new StorageOptions()),
            NullLogger<ManifestWriter>.Instance);

        return new ManifestTestFixture(db, fs, clock, writer);
    }

    /// <summary>单文件布局的阈值设为 1（任何非空镜像都走分片），片内上限设得足够大。</summary>
    public static StorageOptions ShardedOptions(int maxEntries = 100_000) =>
        new() { ManifestShardThreshold = 1, ManifestShardMaxEntries = maxEntries };

    // ---- 布局路径 ----

    public static string ShardPath(string segment) => $@"C:\backup\.manifest\{segment}.json";

    public static string SegmentDirectory(string segment) => $@"C:\backup\.manifest\{segment}";

    public static string SecondShardPath(string segment, string second) =>
        $@"C:\backup\.manifest\{segment}\{second}.json";

    public static string SegmentRootShardPath(string segment) =>
        $@"C:\backup\.manifest\{segment}\_root.json";

    // ---- 造数据 ----

    public async Task<BackupTask> AddTaskAsync(string name = "文档备份")
    {
        TaskName = name;
        var task = new BackupTask
        {
            Name = name,
            SourceDir = SourceRoot,
            BackupDir = BackupRoot,
            FilterJson = "{}",
            IsEnabled = true,
        };

        TaskId = task.Id;

        await using AppDbContext db = await Db.OpenAsync().ConfigureAwait(false);
        db.Tasks.Add(task);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return task;
    }

    /// <summary>
    /// 插入一个已完成快照。开始时间自增，保证链序与 <c>BeginTime</c> 序一致。
    /// </summary>
    /// <param name="imageFileCount">
    /// **镜像**规模（不是本次差异数）。清单头部的 <c>fileCount</c> 就取它——
    /// 刻意让它与"实际写进清单的条目数"不同，才能测出"没有 replay 整条链去数一遍"。
    /// </param>
    public async Task<BackupSnapshot> AddSnapshotAsync(
        SnapshotType type,
        long imageFileCount,
        long imageTotalBytes,
        SnapshotStatus status = SnapshotStatus.Completed)
    {
        var snapshot = new BackupSnapshot
        {
            TaskId = TaskId,
            Type = type,
            Status = status,
            BeginTime = _nextBeginUtc,
            EndTime = _nextBeginUtc.AddMinutes(1),
            ImageFileCount = imageFileCount,
            ImageTotalBytes = imageTotalBytes,
        };

        _nextBeginUtc = snapshot.EndTime!.Value.AddMinutes(5);

        await using AppDbContext db = await Db.OpenAsync().ConfigureAwait(false);
        db.Snapshots.Add(snapshot);
        await db.SaveChangesAsync().ConfigureAwait(false);
        return snapshot;
    }

    public async Task AddRecordAsync(
        int snapshotId,
        string relativePath,
        long length = 100,
        string? contentHash = "0000000000000000000000000000000000000000000000000000000000000000",
        FileRecordType type = FileRecordType.Created,
        DateTime? fileTimeUtc = null,
        bool isDeleted = false)
    {
        var record = new BackupFile
        {
            SnapshotId = snapshotId,
            TaskId = TaskId,
            RelativePath = relativePath,
            RelativePathKey = Normalizer.Normalize(relativePath),
            Type = type,
            Length = length,
            ContentHash = contentHash,
            FileTimeUtc = fileTimeUtc ?? new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
            IsDeleted = isDeleted,
        };

        await using AppDbContext db = await Db.OpenAsync().ConfigureAwait(false);
        db.Files.Add(record);
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    public Task<ManifestWriteResult> WriteAsync(
        BackupSnapshot snapshot,
        IReadOnlyCollection<string> touched)
    {
        BackupTask task = new()
        {
            Id = TaskId,
            Name = TaskName,
            SourceDir = SourceRoot,
            BackupDir = BackupRoot,
        };

        return Writer.WriteAsync(task, snapshot, touched, CancellationToken.None);
    }

    // ---- 断言辅助 ----

    public JsonDocument ReadJson(string path)
    {
        string text = Fs.ReadText(path)
                      ?? throw new InvalidOperationException($"清单文件不存在：{path}");

        return JsonDocument.Parse(text);
    }

    public static IReadOnlyList<JsonElement> Entries(JsonDocument manifest) =>
        [.. manifest.RootElement.GetProperty("files").EnumerateArray()];

    public static IReadOnlyList<string> EntryPaths(JsonDocument manifest) =>
        [.. Entries(manifest).Select(e => e.GetProperty("path").GetString() ?? string.Empty)];

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
