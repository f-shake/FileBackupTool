using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 镜像浏览与文件历史。设计文档 02 §3.1、06 §4.2/§4.3。
/// </summary>
public class MirrorQueryTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

    private static async Task<(TestHarness H, Guid TaskId, int FullId, int IncId)> BuildAsync()
    {
        TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("root.txt", "root", T1);
        h.AddSourceFile(@"docs\a.txt", "a", T1);
        h.AddSourceFile(@"docs\b.txt", "b", T1);
        h.AddSourceFile(@"docs\sub\c.txt", "c", T1);
        h.AddSourceFile(@"photos\p.jpg", "p", T1);
        int full = (await h.BackupAsync(task.Id)).SnapshotId;

        h.AddSourceFile(@"docs\a.txt", "a-modified", T2);
        h.RemoveSourceFile(@"docs\b.txt");
        h.AddSourceFile(@"docs\d.txt", "d", T2);
        int inc = (await h.BackupAsync(task.Id, SnapshotType.Increment)).SnapshotId;

        return (h, task.Id, full, inc);
    }

    [Fact]
    public async Task 列根目录只返回一层()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, string.Empty, 1, 100, CancellationToken.None);

        // root.txt 是文件；docs / photos 是目录
        Assert.Equal(3, page.Total);
        Assert.Contains(page.Items, e => e.Name == "root.txt" && !e.IsDirectory);
        Assert.Contains(page.Items, e => e.Name == "docs" && e.IsDirectory);
        Assert.Contains(page.Items, e => e.Name == "photos" && e.IsDirectory);

        // 深层条目不应出现
        Assert.DoesNotContain(page.Items, e => e.Name == "c.txt");
    }

    [Fact]
    public async Task 列子目录()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, @"docs", 1, 100, CancellationToken.None);

        Assert.Equal(3, page.Total);   // a.txt, d.txt, sub
        Assert.Contains(page.Items, e => e.Name == "a.txt" && !e.IsDirectory);
        Assert.Contains(page.Items, e => e.Name == "d.txt" && !e.IsDirectory);
        Assert.Contains(page.Items, e => e.Name == "sub" && e.IsDirectory);
        Assert.DoesNotContain(page.Items, e => e.Name == "b.txt");   // 已删除
    }

    [Fact]
    public async Task 列目录时能看到增量里的改动()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, @"docs", 1, 100, CancellationToken.None);

        MirrorEntry a = page.Items.Single(e => e.Name == "a.txt");
        Assert.Equal("a-modified".Length, a.Length);
        Assert.Equal(T2, a.FileTimeUtc);
    }

    [Fact]
    public async Task 列目录支持分页()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page1 = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, "docs", 1, 2, CancellationToken.None);
        PagedResult<MirrorEntry> page2 = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, "docs", 2, 2, CancellationToken.None);

        Assert.Equal(3, page1.Total);
        Assert.Equal(2, page1.Items.Count);
        Assert.Single(page2.Items);
        Assert.Empty(page1.Items.Select(e => e.Name).Intersect(page2.Items.Select(e => e.Name)));
    }

    [Fact]
    public async Task 列不存在的目录返回空()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.ListDirectoryAsync(
            taskId, inc, "nonexistent", 1, 100, CancellationToken.None);

        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task 目录树骨架包含所有层级()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        IReadOnlyList<string> tree = await h.MirrorQuery.ListDirectoryTreeAsync(
            taskId, inc, string.Empty, CancellationToken.None);

        Assert.Contains("docs", tree);
        Assert.Contains("photos", tree);
        Assert.Contains(@"docs\sub", tree);
    }

    [Fact]
    public async Task 文件历史包含每个版本()
    {
        (TestHarness h, Guid taskId, int full, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        IReadOnlyList<FileVersionEntry> history = await h.MirrorQuery.GetFileHistoryAsync(
            taskId, @"docs\a.txt", CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal(full, history[0].SnapshotId);
        Assert.Equal(FileRecordType.Created, history[0].RecordType);
        Assert.Equal(inc, history[1].SnapshotId);
        Assert.Equal(FileRecordType.Modified, history[1].RecordType);
    }

    [Fact]
    public async Task 文件历史包含删除墓碑()
    {
        (TestHarness h, Guid taskId, _, _) = await BuildAsync();
        await using TestHarness keepAlive = h;

        IReadOnlyList<FileVersionEntry> history = await h.MirrorQuery.GetFileHistoryAsync(
            taskId, @"docs\b.txt", CancellationToken.None);

        Assert.Equal(2, history.Count);
        Assert.Equal(FileRecordType.Deleted, history[1].RecordType);
        Assert.Null(history[1].ContentHash);
    }

    [Fact]
    public async Task 按文件名搜索()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.SearchAsync(
            taskId, inc, ".txt", 1, 100, CancellationToken.None);

        Assert.Equal(4, page.Total);   // root.txt, docs\a.txt, docs\d.txt, docs\sub\c.txt
        Assert.All(page.Items, e => Assert.False(e.IsDirectory));
        Assert.DoesNotContain(page.Items, e => e.Name.EndsWith(".jpg", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 搜索空关键字返回空()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.SearchAsync(
            taskId, inc, "  ", 1, 100, CancellationToken.None);

        Assert.Equal(0, page.Total);
    }

    [Fact]
    public async Task 列变更内容()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        PagedResult<MirrorEntry> created = await h.MirrorQuery.ListChangesAsync(
            inc, FileRecordType.Created, 1, 100, CancellationToken.None);
        PagedResult<MirrorEntry> modified = await h.MirrorQuery.ListChangesAsync(
            inc, FileRecordType.Modified, 1, 100, CancellationToken.None);
        PagedResult<MirrorEntry> deleted = await h.MirrorQuery.ListChangesAsync(
            inc, FileRecordType.Deleted, 1, 100, CancellationToken.None);

        Assert.Equal(1, created.Total);
        Assert.Equal(1, modified.Total);
        Assert.Equal(1, deleted.Total);
        Assert.Equal("d.txt", created.Items[0].Name);
        Assert.Equal("a.txt", modified.Items[0].Name);
        Assert.Equal("b.txt", deleted.Items[0].Name);

        PagedResult<MirrorEntry> all = await h.MirrorQuery.ListChangesAsync(
            inc, null, 1, 100, CancellationToken.None);
        Assert.Equal(3, all.Total);
    }

    [Fact]
    public async Task 单文件元数据能查到继承来的文件()
    {
        // 该快照本身没有这个文件的记录，但它从更早的快照继承而来
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        MirrorEntry? entry = await h.MirrorQuery.GetFileAsync(inc, @"docs\sub\c.txt", CancellationToken.None);

        Assert.NotNull(entry);
        Assert.Equal("c.txt", entry.Name);
        Assert.Equal(1, entry.Length);
    }

    [Fact]
    public async Task 单文件元数据查不存在的文件返回空()
    {
        (TestHarness h, Guid taskId, _, int inc) = await BuildAsync();
        await using TestHarness keepAlive = h;

        Assert.Null(await h.MirrorQuery.GetFileAsync(inc, @"docs\zzz.txt", CancellationToken.None));
    }

    [Fact]
    public async Task 虚拟全量的条目哈希为空()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        int vf = (await h.BackupAsync(task.Id, SnapshotType.VirtualFull)).SnapshotId;

        PagedResult<MirrorEntry> page = await h.MirrorQuery.ListDirectoryAsync(
            task.Id, vf, string.Empty, 1, 100, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Null(page.Items[0].ContentHash);
    }
}
