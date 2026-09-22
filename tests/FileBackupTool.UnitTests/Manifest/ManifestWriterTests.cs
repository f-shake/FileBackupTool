using System.Globalization;
using System.Text;
using System.Text.Json;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests.Manifest;

/// <summary>
/// 抢救清单写入器。设计文档 02 §6 / 决策总览 #11。
/// </summary>
/// <remarks>
/// 全部跑在**真实临时 SQLite**（清单的分片靠 TEMP 表与 LIKE 前缀语义实现）
/// 与内存文件系统之上。
/// </remarks>
public sealed class ManifestWriterTests
{
    private const string Docs = "docs";
    private const string Media = "media";

    private const string RealHash =
        "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    // ------------------------------------------------------------------
    // 分片布局
    // ------------------------------------------------------------------

    [Fact]
    public async Task ShardedLayout_SplitsByFirstLevelDirectory_AndRootLevelFiles()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        BackupSnapshot snapshot = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 4, imageTotalBytes: 400);
        await fx.AddRecordAsync(snapshot.Id, @"docs\2026年度报告.docx", length: 10);
        await fx.AddRecordAsync(snapshot.Id, @"docs\b.txt", length: 20);
        await fx.AddRecordAsync(snapshot.Id, @"media\v.mp4", length: 30);
        await fx.AddRecordAsync(snapshot.Id, "readme.txt", length: 40);

        ManifestWriteResult result = await fx.WriteAsync(
            snapshot,
            [@"docs\2026年度报告.docx", @"docs\b.txt", @"media\v.mp4", "readme.txt"]);

        Assert.Equal("sharded", result.Layout);
        Assert.Equal(3, result.ShardCount);

        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Docs)));
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Media)));

        // 源目录根下直接的文件没有一级子目录可用 → 单独成片，片名固定为 _root
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.RootShardPath));

        // 单文件版本不该与分片并存
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));

        // 没超过片内上限 → 不该出现二级目录
        Assert.False(fx.Fs.DirectoryExists(ManifestTestFixture.SegmentDirectory(Docs)));

        using JsonDocument docs = fx.ReadJson(ManifestTestFixture.ShardPath(Docs));
        Assert.Equal(
            Sorted([@"docs\2026年度报告.docx", @"docs\b.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(docs)));

        using JsonDocument root = fx.ReadJson(ManifestTestFixture.RootShardPath);
        Assert.Equal(Sorted(["readme.txt"]), Sorted(ManifestTestFixture.EntryPaths(root)));

        using JsonDocument media = fx.ReadJson(ManifestTestFixture.ShardPath(Media));
        Assert.Equal(Sorted([@"media\v.mp4"]), Sorted(ManifestTestFixture.EntryPaths(media)));
    }

    [Fact]
    public async Task SecondLevelSharding_WhenSegmentExceedsMaxEntries()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(
            ManifestTestFixture.ShardedOptions(maxEntries: 2));
        await fx.AddTaskAsync();

        BackupSnapshot snapshot = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 5, imageTotalBytes: 500);
        await fx.AddRecordAsync(snapshot.Id, @"docs\a\1.txt");
        await fx.AddRecordAsync(snapshot.Id, @"docs\a\2.txt");
        await fx.AddRecordAsync(snapshot.Id, @"docs\b\1.txt");
        await fx.AddRecordAsync(snapshot.Id, @"docs\c.txt");
        await fx.AddRecordAsync(snapshot.Id, @"media\v.mp4");

        ManifestWriteResult result = await fx.WriteAsync(
            snapshot,
            [@"docs\a\1.txt", @"docs\a\2.txt", @"docs\b\1.txt", @"docs\c.txt", @"media\v.mp4"]);

        Assert.Equal("sharded", result.Layout);

        // 二级布局与一级片**不能并存**——否则同一个文件会在两份片里各出现一次
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Docs)));

        Assert.True(fx.Fs.FileExists(ManifestTestFixture.SecondShardPath(Docs, "a")));
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.SecondShardPath(Docs, "b")));

        // 该一级目录下直接的文件 → <一级>\_root.json
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.SegmentRootShardPath(Docs)));

        // 没超过上限的段仍然是一级片
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Media)));

        // 没有根级文件 → 不产生 _root 片
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.RootShardPath));

        Assert.Equal(4, result.ShardCount);

        using JsonDocument a = fx.ReadJson(ManifestTestFixture.SecondShardPath(Docs, "a"));
        Assert.Equal(
            Sorted([@"docs\a\1.txt", @"docs\a\2.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(a)));

        using JsonDocument b = fx.ReadJson(ManifestTestFixture.SecondShardPath(Docs, "b"));
        Assert.Equal(Sorted([@"docs\b\1.txt"]), Sorted(ManifestTestFixture.EntryPaths(b)));

        using JsonDocument root = fx.ReadJson(ManifestTestFixture.SegmentRootShardPath(Docs));
        Assert.Equal(Sorted([@"docs\c.txt"]), Sorted(ManifestTestFixture.EntryPaths(root)));
    }

    // ------------------------------------------------------------------
    // 单文件阈值切换
    // ------------------------------------------------------------------

    [Fact]
    public async Task ThresholdSwitch_ShardsToSingleFile_RemovesShardDirectory()
    {
        var storage = new StorageOptions { ManifestShardThreshold = 3, ManifestShardMaxEntries = 100_000 };
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(storage);
        await fx.AddTaskAsync();

        BackupSnapshot first = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 5, imageTotalBytes: 500);
        await fx.AddRecordAsync(first.Id, @"docs\a.txt");
        await fx.AddRecordAsync(first.Id, @"docs\b.txt");
        await fx.AddRecordAsync(first.Id, @"media\v.mp4");
        await fx.AddRecordAsync(first.Id, "root.txt");
        await fx.AddRecordAsync(first.Id, @"extra\x.txt");

        await fx.WriteAsync(first, [@"docs\a.txt", @"docs\b.txt", @"media\v.mp4", "root.txt", @"extra\x.txt"]);

        Assert.True(fx.Fs.DirectoryExists(ManifestTestFixture.ManifestDirPath));
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));

        // 删掉 3 个文件 → 镜像降到 2 个，低于阈值 → 退回单文件
        BackupSnapshot second = await fx.AddSnapshotAsync(SnapshotType.Increment, imageFileCount: 2, imageTotalBytes: 200);
        await fx.AddRecordAsync(second.Id, @"docs\b.txt", type: FileRecordType.Deleted, contentHash: null);
        await fx.AddRecordAsync(second.Id, @"media\v.mp4", type: FileRecordType.Deleted, contentHash: null);
        await fx.AddRecordAsync(second.Id, @"extra\x.txt", type: FileRecordType.Deleted, contentHash: null);

        ManifestWriteResult result = await fx.WriteAsync(
            second, [@"docs\b.txt", @"media\v.mp4", @"extra\x.txt"]);

        Assert.Equal("single", result.Layout);
        Assert.Equal(1, result.ShardCount);
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));

        // 退回单文件时必须删除**整个** .manifest 目录：两套布局并存的清单无法判断哪份是当前的
        Assert.False(fx.Fs.DirectoryExists(ManifestTestFixture.ManifestDirPath));

        using JsonDocument manifest = fx.ReadJson(ManifestTestFixture.ManifestFilePath);
        Assert.Equal(
            Sorted([@"docs\a.txt", "root.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(manifest)));
    }

    [Fact]
    public async Task ThresholdSwitch_SingleFileToShards_RemovesSingleFile()
    {
        var storage = new StorageOptions { ManifestShardThreshold = 3, ManifestShardMaxEntries = 100_000 };
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(storage);
        await fx.AddTaskAsync();

        BackupSnapshot first = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 2, imageTotalBytes: 200);
        await fx.AddRecordAsync(first.Id, @"docs\a.txt");
        await fx.AddRecordAsync(first.Id, "root.txt");

        await fx.WriteAsync(first, [@"docs\a.txt", "root.txt"]);

        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));
        Assert.False(fx.Fs.DirectoryExists(ManifestTestFixture.ManifestDirPath));

        BackupSnapshot second = await fx.AddSnapshotAsync(SnapshotType.Increment, imageFileCount: 3, imageTotalBytes: 300);
        await fx.AddRecordAsync(second.Id, @"docs\b.txt");

        ManifestWriteResult result = await fx.WriteAsync(second, [@"docs\b.txt"]);

        Assert.Equal("sharded", result.Layout);
        Assert.Equal(2, result.ShardCount);

        // 切到分片时必须删除单文件版本
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));

        // 首次分片没有"地基"（.manifest 目录不存在）→ 必须整体重建，
        // 否则只写被触及的 docs 片会给出一份**缺 root.txt** 的清单
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.RootShardPath));

        using JsonDocument docs = fx.ReadJson(ManifestTestFixture.ShardPath(Docs));
        Assert.Equal(
            Sorted([@"docs\a.txt", @"docs\b.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(docs)));

        using JsonDocument root = fx.ReadJson(ManifestTestFixture.RootShardPath);
        Assert.Equal(Sorted(["root.txt"]), Sorted(ManifestTestFixture.EntryPaths(root)));
    }

    // ------------------------------------------------------------------
    // 增量只重写受影响的片
    // ------------------------------------------------------------------

    [Fact]
    public async Task IncrementalWrite_RewritesOnlyAffectedShards()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        BackupSnapshot first = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 3, imageTotalBytes: 300);
        await fx.AddRecordAsync(first.Id, @"docs\a.txt");
        await fx.AddRecordAsync(first.Id, @"media\v.mp4");
        await fx.AddRecordAsync(first.Id, "root.txt");

        await fx.WriteAsync(first, [@"docs\a.txt", @"media\v.mp4", "root.txt"]);

        string docsBefore = fx.Fs.ReadText(ManifestTestFixture.ShardPath(Docs))!;
        string mediaBefore = fx.Fs.ReadText(ManifestTestFixture.ShardPath(Media))!;
        string rootBefore = fx.Fs.ReadText(ManifestTestFixture.RootShardPath)!;

        // 时钟推进：若 media 片被重写，其 generatedAt 一定会变（内容断言才有意义）
        fx.Clock.AdvanceSeconds(120);
        int createsBefore = fx.Fs.CreateCount;
        long bytesBefore = fx.Fs.TotalBytesWritten;

        BackupSnapshot second = await fx.AddSnapshotAsync(SnapshotType.Increment, imageFileCount: 4, imageTotalBytes: 400);
        await fx.AddRecordAsync(second.Id, @"docs\b.txt");

        ManifestWriteResult result = await fx.WriteAsync(second, [@"docs\b.txt"]);

        // touchedRelativePaths 只涉及一级段 docs → 只有 docs 片被写
        Assert.Equal(1, result.ShardCount);
        Assert.Equal(1, fx.Fs.CreateCount - createsBefore);
        Assert.Equal(
            (long)Encoding.UTF8.GetByteCount(fx.Fs.ReadText(ManifestTestFixture.ShardPath(Docs))!),
            fx.Fs.TotalBytesWritten - bytesBefore);

        // 未被触及的片：内容一字未动（等价于文件没被重写）
        Assert.Equal(mediaBefore, fx.Fs.ReadText(ManifestTestFixture.ShardPath(Media)));
        Assert.Equal(rootBefore, fx.Fs.ReadText(ManifestTestFixture.RootShardPath));

        Assert.NotEqual(docsBefore, fx.Fs.ReadText(ManifestTestFixture.ShardPath(Docs)));

        using JsonDocument docs = fx.ReadJson(ManifestTestFixture.ShardPath(Docs));
        Assert.Equal(
            Sorted([@"docs\a.txt", @"docs\b.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(docs)));
    }

    [Fact]
    public async Task EmptyShard_IsDeleted_WhenAllItsEntriesAreGone()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        BackupSnapshot first = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 2, imageTotalBytes: 200);
        await fx.AddRecordAsync(first.Id, @"docs\a.txt");
        await fx.AddRecordAsync(first.Id, @"media\v.mp4");

        await fx.WriteAsync(first, [@"docs\a.txt", @"media\v.mp4"]);

        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Docs)));

        BackupSnapshot second = await fx.AddSnapshotAsync(SnapshotType.Increment, imageFileCount: 1, imageTotalBytes: 100);
        await fx.AddRecordAsync(second.Id, @"docs\a.txt", type: FileRecordType.Deleted, contentHash: null);

        ManifestWriteResult result = await fx.WriteAsync(second, [@"docs\a.txt"]);

        // 被删除条目所在的一级片被重写 → 0 条目 → 片文件消失
        Assert.Equal(0, result.ShardCount);
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Docs)));
        Assert.False(fx.Fs.DirectoryExists(ManifestTestFixture.SegmentDirectory(Docs)));

        // 其它片不受影响
        Assert.True(fx.Fs.FileExists(ManifestTestFixture.ShardPath(Media)));
    }

    // ------------------------------------------------------------------
    // 头部与条目
    // ------------------------------------------------------------------

    [Fact]
    public async Task HeaderFields_UseSnapshotImageSize_NotEntryCount()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync("文档备份");

        BackupSnapshot snapshot = await fx.AddSnapshotAsync(
            SnapshotType.Full, imageFileCount: 12_345, imageTotalBytes: 9_876_543_210L);
        await fx.AddRecordAsync(
            snapshot.Id,
            @"docs\a.txt",
            length: 100,
            contentHash: RealHash,
            fileTimeUtc: new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc));

        await fx.WriteAsync(snapshot, [@"docs\a.txt"]);

        using JsonDocument manifest = fx.ReadJson(ManifestTestFixture.ShardPath(Docs));
        JsonElement root = manifest.RootElement;

        Assert.Equal("FileBackupTool", root.GetProperty("tool").GetString());
        Assert.Equal(1, root.GetProperty("manifestVersion").GetInt32());
        Assert.Equal(fx.TaskId.ToString("D"), root.GetProperty("taskId").GetString());
        Assert.Equal(fx.TaskName, root.GetProperty("taskName").GetString());
        Assert.Equal(ManifestTestFixture.SourceRoot, root.GetProperty("sourceDir").GetString());
        Assert.Equal(ManifestTestFixture.BackupRoot, root.GetProperty("backupDir").GetString());
        Assert.Equal("blobs/<hash[0:2]>/<hash[2:4]>/<hash>", root.GetProperty("blobLayout").GetString());

        // 没有本软件也要能据此还原 → 还原方法必须写在清单里（设计文档 02 §6.1 原文）
        Assert.Equal(
            "对每个条目，把 blobs/<hash[0:2]>/<hash[2:4]>/<hash> 复制到 path 指定的位置。",
            root.GetProperty("restoreHint").GetString());

        Assert.Equal(snapshot.Id, root.GetProperty("latestSnapshotId").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("latestSnapshotTime").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("generatedAt").GetString()));

        // fileCount / totalBytes 取快照上增量维护的**镜像规模**，
        // 而不是"本次写进清单的条目数"（这里是 1）——replay 整条链去数一遍是百万文件下不可接受的
        Assert.Equal(12_345L, root.GetProperty("fileCount").GetInt64());
        Assert.Equal(9_876_543_210L, root.GetProperty("totalBytes").GetInt64());

        IReadOnlyList<JsonElement> entries = ManifestTestFixture.Entries(manifest);
        Assert.Single(entries);

        JsonElement entry = entries[0];
        Assert.Equal(@"docs\a.txt", entry.GetProperty("path").GetString());
        Assert.Equal(RealHash, entry.GetProperty("hash").GetString());
        Assert.Equal(100L, entry.GetProperty("size").GetInt64());

        // mtime 必须是**带时区标记的 UTC**：写得含糊（无 Z）会让任何工具无从判断是哪个时刻
        string mtime = entry.GetProperty("mtime").GetString()!;
        Assert.EndsWith("Z", mtime, StringComparison.Ordinal);
        Assert.Equal(
            new DateTime(2026, 5, 1, 8, 0, 0, DateTimeKind.Utc),
            DateTime.Parse(mtime, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    [Fact]
    public async Task EveryShardCarriesTheFullHeader()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        BackupSnapshot snapshot = await fx.AddSnapshotAsync(SnapshotType.Full, imageFileCount: 2, imageTotalBytes: 222);
        await fx.AddRecordAsync(snapshot.Id, @"docs\a.txt");
        await fx.AddRecordAsync(snapshot.Id, "root.txt");

        await fx.WriteAsync(snapshot, [@"docs\a.txt", "root.txt"]);

        // 片被单独拿走（只抢救到一个文件）时仍然自解释
        foreach (string path in
                 new[] { ManifestTestFixture.ShardPath(Docs), ManifestTestFixture.RootShardPath })
        {
            using JsonDocument shard = fx.ReadJson(path);
            Assert.Equal(2L, shard.RootElement.GetProperty("fileCount").GetInt64());
            Assert.Equal(222L, shard.RootElement.GetProperty("totalBytes").GetInt64());
            Assert.Equal(snapshot.Id, shard.RootElement.GetProperty("latestSnapshotId").GetInt32());
            Assert.Equal(ManifestTestFixture.SourceRoot, shard.RootElement.GetProperty("sourceDir").GetString());
        }
    }

    [Fact]
    public async Task VirtualFull_EntriesAreWrittenWithNullHash()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        // 虚拟全量不写 blob → ContentHash 为 null
        BackupSnapshot virtualFull = await fx.AddSnapshotAsync(
            SnapshotType.VirtualFull, imageFileCount: 2, imageTotalBytes: 200);
        await fx.AddRecordAsync(virtualFull.Id, @"docs\a.txt", length: 11, contentHash: null);
        await fx.AddRecordAsync(virtualFull.Id, @"docs\c.txt", length: 33, contentHash: null);

        BackupSnapshot increment = await fx.AddSnapshotAsync(
            SnapshotType.Increment, imageFileCount: 3, imageTotalBytes: 300);
        await fx.AddRecordAsync(increment.Id, @"docs\b.txt", length: 22, contentHash: RealHash);

        await fx.WriteAsync(increment, [@"docs\b.txt"]);

        using JsonDocument docs = fx.ReadJson(ManifestTestFixture.ShardPath(Docs));
        IReadOnlyList<JsonElement> entries = ManifestTestFixture.Entries(docs);

        // 条目一个都不能丢——清单必须与镜像一一对应，否则它会**静默**地少列文件
        Assert.Equal(
            Sorted([@"docs\a.txt", @"docs\b.txt", @"docs\c.txt"]),
            Sorted(ManifestTestFixture.EntryPaths(docs)));

        JsonElement virtualEntry = entries.Single(e => e.GetProperty("path").GetString() == @"docs\a.txt");

        // 如实写 null：既不能丢条目，也不能伪造一个哈希
        Assert.Equal(JsonValueKind.Null, virtualEntry.GetProperty("hash").ValueKind);
        Assert.Equal(11L, virtualEntry.GetProperty("size").GetInt64());

        JsonElement realEntry = entries.Single(e => e.GetProperty("path").GetString() == @"docs\b.txt");
        Assert.Equal(RealHash, realEntry.GetProperty("hash").GetString());
    }

    // ------------------------------------------------------------------
    // 链断裂
    // ------------------------------------------------------------------

    [Fact]
    public async Task BrokenChain_ReportsInconsistency_AndWritesNothing()
    {
        await using ManifestTestFixture fx = await ManifestTestFixture.CreateAsync(ManifestTestFixture.ShardedOptions());
        await fx.AddTaskAsync();

        // 增量却找不到它所属的全量 → 链断裂，镜像无法重建
        BackupSnapshot orphan = await fx.AddSnapshotAsync(SnapshotType.Increment, imageFileCount: 1, imageTotalBytes: 10);
        await fx.AddRecordAsync(orphan.Id, @"docs\a.txt");

        ManifestWriteResult result = await fx.WriteAsync(orphan, [@"docs\a.txt"]);

        Assert.Equal("broken-chain", result.Layout);
        Assert.Equal(1, result.MirrorInconsistencies);

        // 规模字段照旧取自快照（虽然镜像是空的）
        Assert.Equal(1L, result.FileCount);
        Assert.Equal(10L, result.TotalBytes);

        // 拿不到完整镜像时**什么都不写**：一份错的清单比没有清单更危险
        Assert.False(fx.Fs.FileExists(ManifestTestFixture.ManifestFilePath));
        Assert.False(fx.Fs.DirectoryExists(ManifestTestFixture.ManifestDirPath));
    }

    // ------------------------------------------------------------------

    private static string[] Sorted(IEnumerable<string> paths) =>
        [.. paths.OrderBy(p => p, StringComparer.Ordinal)];
}
