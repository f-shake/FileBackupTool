using FileBackupTool.Core.Monitoring;
using FileBackupTool.Monitoring;
using FileBackupTool.UnitTests.Monitoring.Fakes;
using Xunit;

namespace FileBackupTool.UnitTests.Monitoring;

/// <summary>
/// 三个监控钩子（设计文档 03 §5.1，设计文档 08 §1.4 明确要求覆盖）。
/// </summary>
/// <remarks>
/// 它们存在的理由是那条协作契约：**快速路径不承载任何正确性**。
/// 所以快速路径一旦失效（建不起来 / 丢事件 / 被反压），唯一正确的反应都是
/// "排一次全量扫描"，而不是"尽力而为"。参考项目 GalleryCloud 三个钩子**一个都没有**，
/// 后果是"监控静默失效"这件事永远没人知道。
/// </remarks>
public sealed class MonitorHookTests
{
    private const string Root = @"C:\src";

    // ------------------------------------------------------------------
    // 钩子 ①：监控器失效
    // ------------------------------------------------------------------

    [Fact]
    public async Task 钩子1_监控器构造失败会立即排一次全量扫描()
    {
        using var h = new MonitoringHarness();
        h.Watchers.FailOnCreate = new IOException("不支持的文件系统");
        Guid taskId = h.AddRoot(Root);

        await h.ReloadAsync();

        RootMonitorStatus status = h.Status(taskId);
        Assert.Equal(RootMonitorMode.Failed, status.Mode);
        Assert.NotNull(status.Error);

        await h.PumpAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan, "监控器失效时必须排全量扫描，而不是局部扫描。");
        Assert.Null(scan.DirtyRelativePaths);
    }

    [Fact]
    public async Task 钩子1_watcher的错误事件会立即排一次全量扫描()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();

        // 缓冲区溢出：事件已经被静默丢弃了，只有全量扫描能把状态修回来。
        h.Watcher(taskId).RaiseError("内部缓冲区溢出");

        await h.PumpAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan);

        RootMonitorStatus status = h.Status(taskId);
        Assert.Equal(RootMonitorMode.Failed, status.Mode);
        Assert.Contains("溢出", status.Error);
    }

    [Fact]
    public async Task 钩子1_错误之后的冷却期内不重建冷却期外允许重建()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        Assert.Single(h.Watchers.Created);

        h.Watcher(taskId).RaiseError("内部缓冲区溢出");
        await h.PumpAsync();
        Assert.Single(h.Watchers.Created);

        // 冷却期内不重建（防止错误风暴变成重建风暴）
        await h.AdvanceAndPumpAsync(20);
        Assert.Single(h.Watchers.Created);
        Assert.Equal(RootMonitorMode.Failed, h.Status(taskId).Mode);

        // 冷却期到 → 换一个新的监控器，快速路径恢复
        await h.AdvanceAndPumpAsync(11);
        Assert.Equal(2, h.Watchers.Created.Count);
        Assert.True(h.Watchers.Created[0].Disposed);
        Assert.Equal(RootMonitorMode.Watcher, h.Status(taskId).Mode);
    }

    // ------------------------------------------------------------------
    // 钩子 ②：队列反压
    // ------------------------------------------------------------------

    [Fact]
    public async Task 钩子2_队列满时不丢事件而是反压并排一次全量扫描()
    {
        using var h = new MonitoringHarness(o => o.EventQueueCapacity = 1);
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.Fs.AddDirectory(@"C:\src\photos");
        h.Fs.AddFile(@"C:\src\docs\a.txt", 1, h.Clock.UtcNow);
        h.Fs.AddFile(@"C:\src\photos\b.jpg", 1, h.Clock.UtcNow);

        // 第一条占满容量为 1 的队列（消费者还没跑）。
        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);

        // 第二条必须被反压挡住，而不是被 TryWrite 悄悄丢掉。
        Task blocked = Task.Run(() => h.Raise(taskId, @"C:\src\photos\b.jpg", WatcherChangeKind.Modified));

        await TestWait.UntilAsync(() => h.Status(taskId).DroppedEventCount >= 1);

        await h.PumpAsync();

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan, "队列反压意味着事件可能已经丢了，必须排全量扫描。");

        // 被反压的那条最终仍然送达（这是"不丢"的直接证据）
        await blocked.WaitAsync(TimeSpan.FromSeconds(5));
        await h.PumpAsync();
        Assert.Equal(2, h.Status(taskId).PendingChangeCount);
    }

    // ------------------------------------------------------------------
    // 钩子 ③：启动自检
    // ------------------------------------------------------------------

    [Fact]
    public async Task 钩子3_启动自检对每个根无条件排一次全量扫描()
    {
        using var h = new MonitoringHarness();
        Guid first = h.AddRoot(@"C:\src1");
        Guid second = h.AddRoot(@"C:\src2");
        await h.ReloadAsync();

        // 两个监控器都建得好好的 —— 依然要扫。"无条件"就是不看监控器状态。
        Assert.Equal(2, h.Watchers.Created.Count);

        await h.Monitor.StartupSelfCheckAsync(CancellationToken.None);

        Assert.Equal(2, h.Trigger.Requests.Count);
        Assert.All(h.Trigger.Requests, r => Assert.True(r.IsFullScan));
        Assert.Contains(h.Trigger.Requests, r => r.TaskId == first);
        Assert.Contains(h.Trigger.Requests, r => r.TaskId == second);
        Assert.All(h.Trigger.Requests, r => Assert.Contains("启动自检", r.Reason));
    }

    [Fact]
    public async Task 未启用监控的任务不建监控器也不排扫描()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root, watchEnabled: false);
        await h.ReloadAsync();

        Assert.Empty(h.Watchers.Created);

        RootMonitorStatus status = h.Status(taskId);
        Assert.Equal(RootMonitorMode.Disabled, status.Mode);

        // 兜底定时器不能给"没在监控"的任务排扫描
        await h.AdvanceAndPumpAsync(24 * 3600);
        Assert.Empty(h.Trigger.Requests);
    }

    [Fact]
    public async Task 手动扫描请求会立刻派发()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");

        Assert.True(await h.Monitor.RequestScanAsync(taskId, fullScan: true, CancellationToken.None));

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.True(scan.IsFullScan);
        Assert.False(await h.Monitor.RequestScanAsync(Guid.NewGuid(), true, CancellationToken.None));
    }

    [Fact]
    public async Task 投递失败不会丢掉脏目录()
    {
        using var h = new MonitoringHarness();
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);

        h.Trigger.FailOnRequest = new InvalidOperationException("作业队列拒绝");

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);
        await h.PumpAsync();
        await h.SettleAndDispatchAsync();
        Assert.Empty(h.Trigger.Requests);

        // 队列恢复 → 放回去的范围还在，会重试
        h.Trigger.FailOnRequest = null;
        await h.AdvanceAndPumpAsync(h.Monitoring.CoalesceWindowSeconds);

        TriggeredScan scan = Assert.Single(h.Trigger.Requests);
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);
    }

    [Fact]
    public async Task 生产循环可以被假时钟驱动()
    {
        using var h = new MonitoringHarness();
        h.Clock.AutoAdvance = false;
        Guid taskId = h.AddRoot(Root);
        await h.ReloadAsync();
        h.Fs.AddDirectory(@"C:\src\docs");
        h.AddPaddingDirectories(taskId);
        h.Fs.AddFile(@"C:\src\docs\a.txt", 100, h.Clock.UtcNow);

        using var cts = new CancellationTokenSource();
        Task loop = h.Monitor.RunAsync(cts.Token);
        Task<TriggeredScan> dispatch = h.Trigger.FirstRequest;

        h.Raise(taskId, @"C:\src\docs\a.txt", WatcherChangeKind.Modified);

        // 场景时间（10 秒静默期 + 5 秒防抖 + 45 秒合并窗口）全部由假时钟给，
        // 这里只做**调度**握手：先等循环真的停在等待点上，再推进时钟，然后让出一点时间片，
        // 好让循环的续体真的跑起来。
        // 不握手（例如"推一次时钟再 Task.Yield() 若干次"）会丢唤醒：循环还没进入等待、
        // 推进就已经发生，释放的是一次没人等的信号，于是"推进八次却一步都没走"。
        for (int round = 0; round < 40 && !dispatch.IsCompleted; round++)
        {
            await TestWait.UntilAsync(() => h.Clock.WaitingForAdvance || dispatch.IsCompleted);
            h.Clock.Advance(60);
            await TestWait.SchedulingYieldAsync();
        }

        Assert.True(dispatch.IsCompleted, "监控循环没有派发扫描。");
        TriggeredScan scan = await dispatch;
        Assert.Equal(["docs"], scan.DirtyRelativePaths!);

        cts.Cancel();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
