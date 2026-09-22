using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.Paths;
using FileBackupTool.Monitoring;
using FileBackupTool.UnitTests.Monitoring.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileBackupTool.UnitTests.Monitoring;

/// <summary>
/// 防抖字典 key 的大小写归一化（设计文档 08 §1.4 明确要求覆盖）。
/// </summary>
/// <remarks>
/// 设计文档 03 §5.6 点名要求："防抖字典的 key 必须在**平台相关的大小写归一化之后**构造"。
/// 违反它的后果不是"多扫一次"，而是**这个文件永远进不了备份**：
/// <c>IMG.JPG</c> 与 <c>img.jpg</c> 会各自成为一条待定记录，每次事件只重置其中一条的计时，
/// 另一条则永远处在"刚刚变化过"的状态里。
/// </remarks>
public sealed class PathKeyNormalizationTests
{
    private const string Root = @"C:\src";

    [Fact]
    public void 待定表中大小写不同的同一路径只算一条()
    {
        var clock = new FakeClock();
        var table = new PendingChangeTable(
            new FakeFileSystem(),
            WindowsPathNormalizer.Instance,
            clock,
            new JobOptions(),
            TimeSpan.FromSeconds(10),
            NullLogger.Instance);

        table.Track(@"C:\src\IMG.JPG", isDirectory: false);
        table.Track(@"C:\src\img.jpg", isDirectory: false);

        Assert.Equal(1, table.Count);
    }

    [Fact]
    public async Task 大小写不同的同一文件只产生一个待备份条目()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\IMG.JPG", 100, h.Clock.UtcNow);

        h.Raise(taskId, @"C:\src\docs\IMG.JPG", WatcherChangeKind.Modified);
        h.Raise(taskId, @"C:\src\docs\img.jpg", WatcherChangeKind.Modified);

        await h.PumpAsync();

        Assert.Equal(1, h.Status(taskId).PendingChangeCount);
    }

    [Fact]
    public async Task 大小写不同的同一文件只派发一次局部扫描()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\IMG.JPG", 100, h.Clock.UtcNow);

        h.Raise(taskId, @"C:\src\docs\IMG.JPG", WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 同一文件的后一次事件（大小写不同）不该重置静默期之外再叠加一份待办
        h.Fs.WriteFile(@"C:\src\docs\img.jpg", 200, h.Clock.UtcNow);
        await h.AdvanceAndPumpAsync(5);
        h.Raise(taskId, @"C:\src\docs\img.jpg", WatcherChangeKind.Modified);
        await h.PumpAsync();

        await h.SettleAndDispatchAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 源目录根上的变更按全量处理()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\root.txt", 100, h.Clock.UtcNow);

        // 直接落在源目录根上的文件：它的"脏目录"就是源根本身（空相对路径）。
        // 引擎把空相对路径当作全量扫描（BackupEngine.NormalizeDirtyScopes），所以这里
        // 交出去的其实是一次全量——这是引擎定的语义，监控器只负责如实转达。
        h.Raise(taskId, @"C:\src\root.txt", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal([string.Empty], scan.DirtyRelativePaths!);
    }
}
