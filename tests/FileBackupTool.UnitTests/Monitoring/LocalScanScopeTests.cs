using FileBackupTool.Core.Monitoring;
using FileBackupTool.Monitoring;
using FileBackupTool.UnitTests.Monitoring.Fakes;
using Xunit;

namespace FileBackupTool.UnitTests.Monitoring;

/// <summary>
/// 局部扫描（设计文档 03 §5.4，设计文档 08 §1.4 明确要求覆盖）。
/// </summary>
/// <remarks>
/// 监控器在这一节里的职责只有一个：**把正确的范围交给引擎**。
/// 它不是那个做"继承"的人——继承发生在引擎按脏目录枚举、并按镜像链 replay 的时候。
/// 所以这里的断言全部是"交给引擎的 <c>DirtyRelativePaths</c> 是什么"。
/// </remarks>
public sealed class LocalScanScopeTests
{
    private const string Root = @"C:\src";

    [Fact]
    public async Task 只重扫发生变化的目录()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddDirectory(@"C:\src\photos");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);
        h.Fs.AddFile(@"C:\src\photos\b.jpg", 100, h.Clock.UtcNow);

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
        Assert.DoesNotContain("photos", scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 范围外的删除不会被误判()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddDirectory(@"C:\src\photos");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);
        h.Fs.AddFile(@"C:\src\photos\b.jpg", 100, h.Clock.UtcNow);

        h.Fs.Delete(@"C:\src\docs\a.txt");
        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Deleted);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        // 引擎会在交给它的范围**之内**做"磁盘上没有 → 删除"的墓碑判定。
        // 所以只要这里只交出 docs，photos 子树就不可能被误判成删除。
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
        Assert.DoesNotContain("photos", scan.DirtyRelativePaths!);
        Assert.DoesNotContain(string.Empty, scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 多个脏目录合并为同一个范围集合()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddDirectory(@"C:\src\photos");
        h.Fs.AddDirectory(@"C:\src\music");
        h.Fs.AddDirectory(@"C:\src\docs\deep");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);
        h.Fs.AddFile(@"C:\src\photos\b.jpg", 100, h.Clock.UtcNow);

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);
        h.Raise(taskId, @"C:\src\photos\b.jpg", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        // 一次备份覆盖多个脏目录 → 合并成**同一个**请求（快照仍然覆盖整个源）
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs", "photos"], scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 脏目录占比超过阈值时退化为全量扫描()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();

        // 共 3 个目录，脏 2 个 = 66% > 30%（LocalScanDegradeRatio 默认值）
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddDirectory(@"C:\src\photos");
        h.Fs.AddDirectory(@"C:\src\music");
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);
        h.Fs.AddFile(@"C:\src\photos\b.jpg", 100, h.Clock.UtcNow);

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);
        h.Raise(taskId, @"C:\src\photos\b.jpg", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan, "脏目录占比超阈值时必须退化为全量扫描。");
        Assert.Contains("退化", scan.Reason);
    }

    [Fact]
    public async Task 占比不高时保持局部扫描()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();

        // 12 个目录，脏 1 个 = 8% < 30%
        for (int i = 0; i < 12; i++)
        {
            h.Fs.AddDirectory($@"C:\src\d{i}");
        }

        h.Fs.AddFile(@"C:\src\d0\a.txt", 100, h.Clock.UtcNow);
        h.Raise(taskId, @"C:\src\d0\a.txt", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.False(scan.IsFullScan);
        Assert.Equal(["d0"], scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 周期性全量扫描兜底会无条件触发()
    {
        using var h = new MonitoringHarness();
        h.Monitoring.FullScanFallbackHours = 6;
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();

        // 期间没有任何变更 —— "无条件"就是不依赖监控器报没报事件
        await h.AdvanceAndPumpAsync(6 * 3600 - 1);
        Assert.Empty(h.Trigger.Requests);

        await h.AdvanceAndPumpAsync(1);
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan);
        Assert.NotNull(h.Status(taskId).LastFullScanUtc);
        Assert.NotNull(h.Status(taskId).LastScanUtc);
    }

    [Fact]
    public async Task 网络路径放弃watcher只跑轮询()
    {
        using var h = new MonitoringHarness();
        h.Fs.Drives.Add(new Core.Abstractions.DriveEntry("Z:\\", DriveType.Network));
        Guid taskId = h.AddRoot(@"Z:\share");

        await h.ReloadAsync();

        // UNC / 映射盘一律不建 watcher：FileSystemWatcher 在那里是**静默失效**的
        Assert.Empty(h.Watchers.Created);

        RootMonitorStatus status = h.Status(taskId);
        Assert.Equal(RootMonitorMode.Polling, status.Mode);

        await h.AdvanceAndPumpAsync(3 * 60 - 1);
        Assert.Empty(h.Trigger.Requests);

        await h.AdvanceAndPumpAsync(1);
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan);
        Assert.Contains("轮询", scan.Reason);
    }

    [Fact]
    public async Task UNC路径也走轮询()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(@"\\nas\backup");

        await h.ReloadAsync();

        Assert.Empty(h.Watchers.Created);
        Assert.Equal(RootMonitorMode.Polling, h.Status(taskId).Mode);
    }

    [Fact]
    public async Task 监控器自身的排除目录不产生事件()
    {
        using var h = new MonitoringHarness();
        h.Storage.DataDirectory = @"C:\src\App_Data";
        Guid taskId = h.AddRoot(Root, backupDir: @"C:\src\_backup");

        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\App_Data");
        h.Fs.AddDirectory(@"C:\src\_backup");
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);

        // SQLite 的边车文件：每次事务提交都写，是最高频的噪声源（设计文档 03 §5.6）
        h.Raise(taskId, @"C:\src\App_Data\filebackuptool.db-wal", WatcherChangeKind.Modified);

        // 备份自己的 blob 池：不排除就会"备份触发备份"自激
        h.Raise(taskId, @"C:\src\_backup\blobs\ab\cd\1234", WatcherChangeKind.Created);

        await h.PumpAsync();

        Assert.Equal(0, h.Status(taskId).PendingChangeCount);

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);
        await h.PumpAsync();
        Assert.Equal(1, h.Status(taskId).PendingChangeCount);
    }
}
