using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Engine.Blobs;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 完整校验与 blob 回收。设计文档 03 §8 / 04 §2.4 / 02 §5。
/// </summary>
public class IntegrityTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task 无引用的blob被移入回收站()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        // 造一个没有任何记录引用的孤儿 blob
        string orphanHash = await PutOrphanBlobAsync(h, "孤儿内容");
        Assert.Equal(2, h.CountBlobs());

        GcResult result = await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);

        Assert.Equal(1, result.RecycledBlobCount);
        Assert.Equal(1, h.CountBlobs());
        Assert.Equal(1, h.CountRecycleBinBlobs());
        Assert.False(h.BlobStores.Create(TestHarness.BackupRoot).Exists(orphanHash));
        Assert.True(h.BlobStores.Create(TestHarness.BackupRoot).ExistsInRecycleBin(orphanHash));
    }

    [Fact]
    public async Task 回收账本记录blob的大小与时间_查不到来源路径时如实留空()
    {
        // 这里暴露了设计文档 02 §5.2 自身的一个矛盾：
        // 回收判定是"**没有任何记录（含软删除的记录）**引用它才可回收"，
        // 那么可回收的 blob 按定义就查不到任何记录，`lastPath` 也就无从得知。
        // 文档说"这些信息在软删除的记录里都是现成的"——这个前提不成立。
        //
        // 现实中 blob 变成垃圾的主要来源正是"**崩溃残留**"：
        // blob 已经写进池子，而记录所在的那一批还没提交。这种情况下本来就没有路径可记。
        // 所以账本诚实地把"能查到的"填上、"查不到的"留空，而不是编一个出来。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        string orphan = await PutOrphanBlobAsync(h, "orphan-content");
        string orphanHash = orphan;

        GcResult result = await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);
        Assert.Equal(1, result.RecycledBlobCount);

        RecycleBinStatus status = await h.Integrity.GetRecycleBinAsync(task.Id, CancellationToken.None);
        Assert.Equal(1, status.BlobCount);

        RecycleLedgerEntry entry = Assert.Single(status.Entries);
        Assert.Equal(orphanHash, entry.Hash);
        Assert.Equal("orphan-content".Length, entry.Length);
        Assert.Null(entry.LastPath);
    }

    [Fact]
    public async Task 回收账本能查到来源路径时把它填上()
    {
        // 另一条路径：记录仍然在（只是不构成"引用"）时，账本能给出它曾经是什么文件。
        // 例如"先备份、后把该快照的记录硬删掉"这种情况。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\重要文件.txt", "内容", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, run.SnapshotId);
        string hash = image[@"docs\重要文件.txt"];

        // 直接把 blob 移进回收站，但把"来源路径"通过同一个入口告诉它
        h.BlobStores.Create(TestHarness.BackupRoot).Recycle(hash, @"docs\重要文件.txt", 6, false, h.Clock.UtcNow);

        RecycleBinStatus status = await h.Integrity.GetRecycleBinAsync(task.Id, CancellationToken.None);
        RecycleLedgerEntry entry = Assert.Single(status.Entries);
        Assert.Equal(@"docs\重要文件.txt", entry.LastPath);
    }

    [Fact]
    public async Task 软删除的快照其blob不被回收()
    {
        // 设计文档 02 §5.1：软删除的记录**仍算有效引用**。
        // 这修正了旧设计的一个矛盾：旧代码的 GC 过滤 IsDeleted == false，
        // 导致软删除的快照其 blob 会被物理删除——"软删除"名不副实，且比没有更危险。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile("b.txt", "bbb", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);
        Assert.Equal(2, h.CountBlobs());

        DeleteImpact impact = (await h.Snapshots.PreviewDeleteAsync(run.SnapshotId, CancellationToken.None))!;
        Assert.Equal(1, impact.AffectedCount);
        await h.Snapshots.DeleteAsync(run.SnapshotId, CancellationToken.None);

        GcResult result = await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);

        Assert.Equal(0, result.RecycledBlobCount);
        Assert.Equal(2, h.CountBlobs());
        Assert.Equal(0, h.CountRecycleBinBlobs());
    }

    [Fact]
    public async Task 增量回收只检查给定范围()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        string orphan = await PutOrphanBlobAsync(h, "孤儿");

        // 给定一个不含该孤儿哈希的范围 → 不应被回收
        GcResult scoped = await h.Integrity.CollectAsync(task.Id, ["deadbeef"], CancellationToken.None);
        Assert.Equal(0, scoped.RecycledBlobCount);
        Assert.True(h.BlobStores.Create(TestHarness.BackupRoot).Exists(orphan));

        // 全池回收 → 被回收
        GcResult full = await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);
        Assert.Equal(1, full.RecycledBlobCount);
    }

    [Fact]
    public async Task 备份结束后自动做增量回收()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        // 把 a.txt 换成完全不同的内容 → 旧 blob 失去引用
        h.AddSourceFile("a.txt", "全新内容", T2);
        await h.BackupAsync(task.Id, SnapshotType.Increment);

        // 旧版本仍在历史快照里被引用 → 不该被回收（这正是"快照可回溯"的基础）
        Assert.Equal(0, h.CountRecycleBinBlobs());
        Assert.Equal(2, h.CountBlobs());
    }

    [Fact]
    public async Task 完整校验报告多余文件()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);
        string orphan = await PutOrphanBlobAsync(h, "孤儿");

        IntegrityReport report = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: false, CancellationToken.None);

        Assert.Contains(orphan, report.RedundantBlobs);
        Assert.Empty(report.LostBlobs);
        Assert.Empty(report.BrokenChains);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task 完整校验报告丢失文件()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, run.SnapshotId);
        h.Fs.DeleteFile(h.BlobStores.Create(TestHarness.BackupRoot).GetFullBlobPath(image["a.txt"]));

        IntegrityReport report = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: false, CancellationToken.None);

        // 有记录引用但 blob 既不在池中也不在回收站 → 会导致还原失败，必须显著告警
        Assert.Single(report.LostBlobs);
        Assert.False(report.IsHealthy);
    }

    [Fact]
    public async Task 深度校验能发现内容与文件名不符的blob()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, run.SnapshotId);
        string hash = image["a.txt"];

        // 篡改 blob 内容（文件名是哈希，重算即可发现）
        h.Fs.AddTextFile(h.BlobStores.Create(TestHarness.BackupRoot).GetFullBlobPath(hash), "被篡改");

        IntegrityReport light = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: false, CancellationToken.None);
        Assert.Empty(light.CorruptedBlobs);

        IntegrityReport deep = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: true, CancellationToken.None);
        Assert.Contains(hash, deep.CorruptedBlobs);
    }

    [Fact]
    public async Task 健康的数据完整性校验通过()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile(@"docs\b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);
        h.AddSourceFile("c.txt", "ccc", T2);
        await h.BackupAsync(task.Id, SnapshotType.Increment);

        IntegrityReport report = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: true, CancellationToken.None);

        Assert.True(report.IsHealthy);
        Assert.Equal(3, report.CheckedBlobCount);
    }

    [Fact]
    public async Task 清空回收站是硬删除()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);
        string orphan = await PutOrphanBlobAsync(h, "孤儿");
        await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);

        Assert.Equal(1, h.CountRecycleBinBlobs());

        await h.Integrity.ClearRecycleBinAsync(task.Id, CancellationToken.None);

        Assert.Equal(0, h.CountRecycleBinBlobs());
        Assert.False(h.BlobStores.Create(TestHarness.BackupRoot).ExistsInRecycleBin(orphan));

        RecycleBinStatus status = await h.Integrity.GetRecycleBinAsync(task.Id, CancellationToken.None);
        Assert.Equal(0, status.BlobCount);
        Assert.Equal(0, status.TotalBytes);
    }

    [Fact]
    public async Task 回收站空间统计反映实际占用()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        await PutOrphanBlobAsync(h, "0123456789");
        await h.Integrity.CollectAsync(task.Id, null, CancellationToken.None);

        RecycleBinStatus status = await h.Integrity.GetRecycleBinAsync(task.Id, CancellationToken.None);
        Assert.Equal(1, status.BlobCount);
        Assert.Equal(10, status.TotalBytes);
    }

    [Fact]
    public async Task 回收站里的blob不算丢失()
    {
        // GC 判定"丢失"时必须把回收站也算作"还在"，
        // 否则刚回收的 blob 会被立刻报成丢失。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, run.SnapshotId);
        string hash = image["a.txt"];

        // 手动把 blob 挪进回收站（记录仍在）
        h.BlobStores.Create(TestHarness.BackupRoot).Recycle(hash, "a.txt", 3, false, h.Clock.UtcNow);

        IntegrityReport report = await h.Integrity.VerifyAsync(task.Id, deepHashCheck: false, CancellationToken.None);
        Assert.Empty(report.LostBlobs);
    }

    [Fact]
    public async Task 备份后清理临时文件()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        // 模拟上次崩溃留下的临时文件
        string backupDir = TestHarness.BackupRoot;
        string tmp = TestHarness.Combine(TestHarness.Combine(backupDir, PhysicalBlobStore.BlobsDirName),
            PhysicalBlobStore.TempDirName);
        h.Fs.AddTextFile(TestHarness.Combine(tmp, "残留"), "半截内容");
        Assert.NotEmpty(h.Fs.EnumerateFiles(tmp, Core.Abstractions.FileEnumerationOptions.Recursive).ToList());

        h.BlobStores.Create(backupDir).CleanTemporary();

        Assert.Empty(h.Fs.EnumerateFiles(tmp, Core.Abstractions.FileEnumerationOptions.Recursive).ToList());
    }

    private static async Task<string> PutOrphanBlobAsync(TestHarness h, string content)
    {
        string hash = await h.Hash.ComputeHashAsync(
            new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), CancellationToken.None);

        PhysicalBlobStore store = h.BlobStores.Create(TestHarness.BackupRoot);
        h.Fs.AddTextFile(store.GetFullBlobPath(hash), content);
        return hash;
    }
}
