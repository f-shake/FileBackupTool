using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 快照列表与级联删除。设计文档 04 §2。
/// </summary>
public class SnapshotServiceTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

    private static async Task<(TestHarness H, Guid TaskId, List<int> Ids)> BuildChainAsync()
    {
        TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);

        var ids = new List<int>();
        ids.Add((await h.BackupAsync(task.Id, SnapshotType.Full)).SnapshotId);
        h.AddSourceFile("b.txt", "bbb", T2);
        ids.Add((await h.BackupAsync(task.Id, SnapshotType.Increment)).SnapshotId);
        h.AddSourceFile("c.txt", "ccc", T2);
        ids.Add((await h.BackupAsync(task.Id, SnapshotType.Increment)).SnapshotId);
        h.AddSourceFile("d.txt", "ddd", T2);
        ids.Add((await h.BackupAsync(task.Id, SnapshotType.Full)).SnapshotId);
        h.AddSourceFile("e.txt", "eee", T2);
        ids.Add((await h.BackupAsync(task.Id, SnapshotType.Increment)).SnapshotId);

        return (h, task.Id, ids);
    }

    [Fact]
    public async Task 按全量快照分组()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        PagedResult<SnapshotGroup> page = await h.Snapshots.ListAsync(
            taskId, null, null, null, 1, 50, CancellationToken.None);

        Assert.Equal(2, page.Total);
        Assert.Equal(2, page.Items.Count);

        // 最新的组在前
        SnapshotGroup newest = page.Items[0];
        Assert.Equal(ids[3], newest.Full.Id);
        Assert.Equal([ids[4]], newest.Increments.Select(s => s.Id));

        SnapshotGroup oldest = page.Items[1];
        Assert.Equal(ids[0], oldest.Full.Id);
        Assert.Equal([ids[1], ids[2]], oldest.Increments.Select(s => s.Id));
    }

    [Fact]
    public async Task 级联删除影响清单与实际删除范围一致()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        DeleteImpact impact = (await h.Snapshots.PreviewDeleteAsync(ids[1], CancellationToken.None))!;

        Assert.Equal([ids[1], ids[2]], impact.Affected.Select(s => s.Id));
        Assert.Equal(ids[1], impact.Target.Id);
        Assert.True(impact.TotalFileRecords > 0);

        await h.Snapshots.DeleteAsync(ids[1], CancellationToken.None);

        List<BackupSnapshot> remaining = await h.Chains.GetVisibleAsync(taskId, CancellationToken.None);
        Assert.Equal([ids[0], ids[3], ids[4]], remaining.Select(s => s.Id));

        // 清单里列出的每一个都真的被删了，且没有多删
        HashSet<int> deleted = [.. new[] { ids[0], ids[1], ids[2], ids[3], ids[4] }.Except(remaining.Select(s => s.Id))];
        Assert.Equal(impact.Affected.Select(s => s.Id).ToHashSet(), deleted);
    }

    [Fact]
    public async Task 删除中间增量后其后的链仍然可replay()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        // 删除 inc1 会级联删掉 inc2，但 full1 与 full2 都还在
        await h.Snapshots.DeleteAsync(ids[1], CancellationToken.None);

        // full1 的镜像里只有 a.txt
        Dictionary<string, string> image1 = await h.ReadImageAsync(taskId, ids[0]);
        Assert.Single(image1);

        // full2 的镜像完整
        Dictionary<string, string> image4 = await h.ReadImageAsync(taskId, ids[3]);
        Assert.Equal(4, image4.Count);

        // inc3 仍可 replay
        Dictionary<string, string> image5 = await h.ReadImageAsync(taskId, ids[4]);
        Assert.Equal(5, image5.Count);
    }

    [Fact]
    public async Task 删除快照是软删除且不释放空间()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        int blobsBefore = h.CountBlobs();

        await h.Snapshots.DeleteAsync(ids[4], CancellationToken.None);

        await using AppDbContext db = await h.Db.OpenAsync();
        BackupSnapshot deleted = (await h.Chains.GetAsync(ids[4], CancellationToken.None))!;
        Assert.True(deleted.IsDeleted);
        Assert.NotNull(deleted.DeletedAt);

        // 物理记录仍在（软删除），blob 空间未释放
        Assert.True(db.Files.Any(f => f.SnapshotId == ids[4]));
        Assert.Equal(blobsBefore, h.CountBlobs());

        // 而且因为记录还在引用，GC 也不会动它们
        GcResult gc = await h.Integrity.CollectAsync(taskId, null, CancellationToken.None);
        Assert.Equal(0, gc.RecycledBlobCount);
    }

    [Fact]
    public async Task 软删除的快照不出现在列表里()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        await h.Snapshots.DeleteAsync(ids[3], CancellationToken.None);

        PagedResult<SnapshotGroup> page = await h.Snapshots.ListAsync(
            taskId, null, null, null, 1, 50, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(ids[0], page.Items[0].Full.Id);
        Assert.Equal([ids[1], ids[2]], page.Items[0].Increments.Select(s => s.Id));
    }

    [Fact]
    public async Task 按类型筛选()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        PagedResult<SnapshotGroup> onlyIncrements = await h.Snapshots.ListAsync(
            taskId, SnapshotType.Increment, null, null, 1, 50, CancellationToken.None);

        Assert.Equal(2, onlyIncrements.Total);
        Assert.All(onlyIncrements.Items, g => Assert.True(g.Increments.Count > 0));
    }

    [Fact]
    public async Task 快照列表分页()
    {
        (TestHarness h, Guid taskId, List<int> ids) = await BuildChainAsync();
        await using TestHarness keepAlive = h;

        PagedResult<SnapshotGroup> first = await h.Snapshots.ListAsync(
            taskId, null, null, null, 1, 1, CancellationToken.None);

        Assert.Equal(2, first.Total);
        Assert.Single(first.Items);
        Assert.Equal(1, first.Page);
        Assert.Equal(1, first.PageSize);
    }

    [Fact]
    public async Task 删除不存在的快照抛异常()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Snapshots.DeleteAsync(9999, CancellationToken.None));

        Assert.Null(await h.Snapshots.PreviewDeleteAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task 快照计数与占用空间被记录()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile("b.txt", "bbbbb", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        BackupSnapshot snapshot = (await h.Chains.GetAsync(run.SnapshotId, CancellationToken.None))!;

        Assert.Equal(SnapshotStatus.Completed, snapshot.Status);
        Assert.Equal(2, snapshot.CreatedFileCount);
        Assert.Equal(0, snapshot.FailedFileCount);
        Assert.Equal(8, snapshot.TotalBytes);
        Assert.Equal(2, snapshot.ImageFileCount);
        Assert.Equal(8, snapshot.ImageTotalBytes);
        Assert.NotNull(snapshot.EndTime);
        Assert.False(snapshot.UsedPartialScan);
    }
}
