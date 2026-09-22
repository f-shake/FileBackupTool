using FileBackupTool.Monitoring;
using FileBackupTool.UnitTests.Monitoring.Fakes;
using Xunit;

namespace FileBackupTool.UnitTests.Monitoring;

/// <summary>
/// 部分写入防护（设计文档 03 §5.2，设计文档 08 §1.4 明确要求覆盖）。
/// </summary>
/// <remarks>
/// 核心断言只有一条：**在静默期窗口内持续变化的文件，永远不能进入"待备份"**。
/// 它对应的是备份场景里最贵的那个 bug——截断的内容被固化并算出哈希之后，
/// dedup 会把它当作合法内容反复复用，污染所有后续快照。
/// </remarks>
public sealed class PartialWriteGuardTests
{
    private const string Root = @"C:\src";
    private const string File = @"C:\src\docs\big.iso";

    [Fact]
    public async Task 静默期内持续变化的文件不得进入待备份()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddFile(File, 1, h.Clock.UtcNow);

        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 每 2 秒写一次，持续 60 秒 —— 远超 10 秒静默期与 45 秒合并窗口。
        // 若实现是"从第一个事件起的固定窗口"，这里就会放行。
        for (int i = 1; i <= 30; i++)
        {
            h.Fs.WriteFile(File, 1 + i, h.Clock.UtcNow);
            h.Raise(taskId, File, WatcherChangeKind.Modified);
            await h.AdvanceAndPumpAsync(2);
        }

        Assert.Empty(h.Trigger.Requests);
        Assert.Equal(1, h.Status(taskId).PendingChangeCount);
    }

    [Fact]
    public async Task 稳定之后才放行并按合并窗口派发一次()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(File, 100, h.Clock.UtcNow);

        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 静默期内（还没到 10 秒）不放行
        await h.AdvanceAndPumpAsync(9);
        Assert.Empty(h.Trigger.Requests);

        // 到点 → 放行到"待备份"，但还要过 5 秒局部扫描防抖
        await h.AdvanceAndPumpAsync(1);
        Assert.Empty(h.Trigger.Requests);

        // 过防抖 → 进入 45 秒合并窗口，此时才"有排程"
        await h.AdvanceAndPumpAsync(5);
        Assert.Empty(h.Trigger.Requests);
        Assert.Equal(["docs"], h.Status(taskId).ScheduledScopes!);

        // 静默期到 → 合并成一次局部扫描
        await h.AdvanceAndPumpAsync(45);
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.False(scan.IsFullScan);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
        Assert.Equal(taskId, scan.TaskId);
    }

    [Fact]
    public async Task 窗口内的新变更会重置静默期()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(File, 100, h.Clock.UtcNow);

        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 9 秒时又变了一次：静默期必须从头算，所以再等 9 秒仍然不放行。
        await h.AdvanceAndPumpAsync(9);
        h.Fs.WriteFile(File, 200, h.Clock.UtcNow);
        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        await h.AdvanceAndPumpAsync(9);
        Assert.Empty(h.Trigger.Requests);
        Assert.Equal(1, h.Status(taskId).PendingChangeCount);

        // 再等 1 秒（凑满重置后的 10 秒）才放行
        await h.AdvanceAndPumpAsync(1);
        await h.AdvanceAndPumpAsync(h.Monitoring.LocalScanDebounceSeconds);
        await h.AdvanceAndPumpAsync(h.Monitoring.CoalesceWindowSeconds);
        Assert.Single(h.Trigger.Requests);
    }

    [Fact]
    public async Task 被独占锁定的文件会指数退避重试且不提前放行()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(File, 100, h.Clock.UtcNow);
        h.Fs.FailGetFile(File, new IOException("文件被独占锁定"));

        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 第 1 次尝试失败 → 退避 0.5 秒（IoRetryBaseDelaySeconds）
        Assert.Equal(1, h.Fs.GetFileCalls(File));

        // 退避期内不重试
        await h.AdvanceAndPumpAsync(0.4);
        Assert.Equal(1, h.Fs.GetFileCalls(File));
        Assert.Empty(h.Trigger.Requests);

        // 越过退避点 → 重试。这次锁已经释放。
        h.Fs.ClearFailure(File);
        await h.AdvanceAndPumpAsync(0.2);
        Assert.Equal(2, h.Fs.GetFileCalls(File));
        Assert.Empty(h.Trigger.Requests);

        // 之后照常走静默期
        await h.SettleAndDispatchAsync();
        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 一直读不出来的文件永远不进入待备份()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(File, 100, h.Clock.UtcNow);
        h.Fs.FailGetFile(File, new IOException("文件被独占锁定"));

        h.Raise(taskId, File, WatcherChangeKind.Modified);
        await h.PumpAsync();

        // 重试用尽之后**保持待定**：宁可晚一点备份，也不能把读不出来的内容当成合法内容固化。
        for (int i = 0; i < 20; i++)
        {
            await h.AdvanceAndPumpAsync(60);
        }

        Assert.Empty(h.Trigger.Requests);
        Assert.Equal(1, h.Status(taskId).PendingChangeCount);
    }

    [Fact]
    public async Task 删除也是一种状态需要静默确认()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(File, 100, h.Clock.UtcNow);
        h.Fs.Delete(File);

        h.Raise(taskId, File, WatcherChangeKind.Deleted);
        await h.PumpAsync();
        await h.AdvanceAndPumpAsync(9);

        // 删除后马上重建（编辑器保存的常见形态）不该产生一次"删除 + 新增"
        Assert.Empty(h.Trigger.Requests);

        await h.AdvanceAndPumpAsync(1);
        await h.AdvanceAndPumpAsync(h.Monitoring.LocalScanDebounceSeconds);
        await h.AdvanceAndPumpAsync(h.Monitoring.CoalesceWindowSeconds);

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
    }
}
