using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 还原。设计文档 04 §1。
/// </summary>
/// <remarks>
/// 唯一的硬要求（设计文档 08 §1.1）：<b>还原出来的文件必须和源文件逐字节一致。</b>
/// </remarks>
public class RestoreTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private static async Task<TestHarness> WithRootsAsync()
    {
        TestHarness h = await TestHarness.CreateAsync();
        await h.SettingsStore.SetAllowedRestoreRootsAsync([TestHarness.RestoreRoot], CancellationToken.None);
        return h;
    }

    // ------------------------------------------------------------------
    // 往返一致性（CI 硬门槛）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 往返一致_多快照多增量含删除与改名()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();

        // 快照 1：三个文件
        h.AddSourceFile("readme.txt", "第一版内容", T1);
        h.AddSourceFile(@"docs\报告.txt", "报告初稿", T1);
        h.AddSourceFile(@"docs\old.txt", "将被删除", T1);
        BackupRunResult s1 = await h.BackupAsync(task.Id);

        // 快照 2：改一个、删一个、加一个
        h.AddSourceFile("readme.txt", "第二版内容——长了一些", T2);
        h.RemoveSourceFile(@"docs\old.txt");
        h.AddSourceFile(@"photos\图.txt", "图片内容", T2);
        BackupRunResult s2 = await h.BackupAsync(task.Id, SnapshotType.Increment);

        // 快照 3：再改一次
        h.AddSourceFile(@"docs\报告.txt", "报告终稿", T3);
        BackupRunResult s3 = await h.BackupAsync(task.Id, SnapshotType.Increment);

        // 三个快照分别还原到不同的目标根，逐一逐字节比对
        await AssertRoundTripAsync(h, task.Id, s1.SnapshotId, new Dictionary<string, string>
        {
            ["readme.txt"] = "第一版内容",
            [@"docs\报告.txt"] = "报告初稿",
            [@"docs\old.txt"] = "将被删除",
        });

        await AssertRoundTripAsync(h, task.Id, s2.SnapshotId, new Dictionary<string, string>
        {
            ["readme.txt"] = "第二版内容——长了一些",
            [@"docs\报告.txt"] = "报告初稿",
            [@"photos\图.txt"] = "图片内容",
        });

        await AssertRoundTripAsync(h, task.Id, s3.SnapshotId, new Dictionary<string, string>
        {
            ["readme.txt"] = "第二版内容——长了一些",
            [@"docs\报告.txt"] = "报告终稿",
            [@"photos\图.txt"] = "图片内容",
        });
    }

    [Fact]
    public async Task 往返一致_二进制与零字节文件逐字节相同()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();

        // 覆盖 0x00 / 0xFF、非对齐长度、空文件——文本比对测不出这些
        byte[] payload = new byte[4096 + 7];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i * 31 % 256);
        }

        payload[0] = 0;
        payload[1] = 255;
        payload[2] = 0;
        payload[^1] = 254;

        h.AddSourceFile(@"bin\data.bin", payload, T1);
        h.AddSourceFile("zero.bin", [], T1);

        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);
        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(2, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);

        byte[] restored = ReadAllBytes(h, h.RestorePath(@"bin\data.bin"));
        Assert.Equal(payload.Length, restored.Length);
        Assert.Equal(payload, restored);

        Assert.Empty(ReadAllBytes(h, h.RestorePath("zero.bin")));
    }

    private static byte[] ReadAllBytes(TestHarness h, string path)
    {
        using Stream stream = h.Fs.OpenRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static async Task AssertRoundTripAsync(
        TestHarness h,
        Guid taskId,
        int snapshotId,
        Dictionary<string, string> expected)
    {
        string target = TestHarness.Combine(TestHarness.RestoreRoot, "s" + snapshotId);

        var plan = await h.Restore.PlanAsync(
            new RestoreRequest(taskId, snapshotId, [], target, RestoreConflictPolicy.Skip),
            CancellationToken.None);

        Assert.Equal(expected.Count, plan.TotalCount);
        Assert.Equal(0, plan.UnavailableCount);

        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(expected.Count, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.UnavailableCount);

        foreach ((string relative, string content) in expected)
        {
            string restored = h.Fs.ReadText(TestHarness.Combine(target, relative))
                              ?? throw new Xunit.Sdk.XunitException($"未还原：{relative}");
            Assert.Equal(content, restored);
        }

        // 还原出来的文件集合必须与镜像**完全一致**（多一个少一个都算错）
        List<string> actual = [.. h.Fs.AllFilePaths()
            .Where(p => p.StartsWith(target + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[(target.Length + 1)..])
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];

        Assert.Equal(
            expected.Keys.OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
            actual);
    }

    [Fact]
    public async Task 还原恢复原始时间戳()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "content", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);
        await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Core.Abstractions.FileEntry? restored = h.Fs.GetFile(h.RestorePath("a.txt"));
        Assert.NotNull(restored);
        Assert.Equal(T1, restored.Value.FileTimeUtc);
    }

    // ------------------------------------------------------------------
    // 粒度（设计文档 04 §1.2）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 目录粒度只还原该子树()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\a.txt", "a", T1);
        h.AddSourceFile(@"docs\sub\b.txt", "b", T1);
        h.AddSourceFile(@"photos\c.txt", "c", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [@"docs"], TestHarness.RestoreRoot,
                RestoreConflictPolicy.Skip),
            CancellationToken.None);

        Assert.Equal(2, plan.TotalCount);
        await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal("a", h.Fs.ReadText(h.RestorePath(@"docs\a.txt")));
        Assert.Equal("b", h.Fs.ReadText(h.RestorePath(@"docs\sub\b.txt")));
        Assert.Null(h.Fs.ReadText(h.RestorePath(@"photos\c.txt")));
    }

    [Fact]
    public async Task 多选文件粒度()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "a", T1);
        h.AddSourceFile("b.txt", "b", T1);
        h.AddSourceFile("c.txt", "c", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, ["a.txt", "c.txt"], TestHarness.RestoreRoot,
                RestoreConflictPolicy.Skip),
            CancellationToken.None);

        Assert.Equal(2, plan.TotalCount);
        await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal("a", h.Fs.ReadText(h.RestorePath("a.txt")));
        Assert.Null(h.Fs.ReadText(h.RestorePath("b.txt")));
        Assert.Equal("c", h.Fs.ReadText(h.RestorePath("c.txt")));
    }

    // ------------------------------------------------------------------
    // 冲突策略（设计文档 04 §1.4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 冲突默认跳过且不静默覆盖()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "来自备份", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        // 目标位置先放一个不同的文件
        h.Fs.AddTextFile(h.RestorePath("a.txt"), "目标原有内容", T3);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);

        Assert.Equal(1, plan.ConflictCount);
        Assert.Equal(0, plan.PlannedCount);

        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(0, result.RestoredCount);
        // 旧设计的"另存为"用 FileMode.Create 会静默覆盖——必须修掉
        Assert.Equal("目标原有内容", h.Fs.ReadText(h.RestorePath("a.txt")));
    }

    [Fact]
    public async Task 冲突覆盖()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "来自备份", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);
        h.Fs.AddTextFile(h.RestorePath("a.txt"), "目标原有内容", T3);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Overwrite),
            CancellationToken.None);
        await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal("来自备份", h.Fs.ReadText(h.RestorePath("a.txt")));
    }

    [Fact]
    public async Task 冲突保留两者()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "来自备份", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);
        h.Fs.AddTextFile(h.RestorePath("a.txt"), "目标原有内容", T3);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.KeepBoth),
            CancellationToken.None);
        await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal("目标原有内容", h.Fs.ReadText(h.RestorePath("a.txt")));
        Assert.Equal("来自备份", h.Fs.ReadText(h.RestorePath("a (1).txt")));
    }

    [Fact]
    public async Task 冲突较新者优先_目标较新时不覆盖()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "来自备份", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);
        h.Fs.AddTextFile(h.RestorePath("a.txt"), "目标更新", T3);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.NewerWins),
            CancellationToken.None);
        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, result.SkippedCount);
        Assert.Equal("目标更新", h.Fs.ReadText(h.RestorePath("a.txt")));
    }

    [Fact]
    public async Task 冲突较新者优先_目标较旧时覆盖()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "来自备份", T2);
        BackupRunResult run = await h.BackupAsync(task.Id);
        h.Fs.AddTextFile(h.RestorePath("a.txt"), "目标较旧", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.NewerWins),
            CancellationToken.None);
        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, result.RestoredCount);
        Assert.Equal("来自备份", h.Fs.ReadText(h.RestorePath("a.txt")));
    }

    // ------------------------------------------------------------------
    // 不可恢复的情况（设计文档 04 §1.6）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 虚拟全量快照的文件被逐条标记为不可恢复()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile("b.txt", "bbb", T1);
        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.VirtualFull);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);

        // 不是一句"整快照不可恢复"，而是**逐文件**的清单
        Assert.Equal(2, plan.UnavailableCount);
        Assert.Equal(0, plan.PlannedCount);
        Assert.All(plan.UnavailableEntries, e => Assert.Contains("虚拟快照", e.UnavailableReason));

        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);
        Assert.Equal(2, result.UnavailableCount);
        Assert.Equal(0, result.RestoredCount);
    }

    [Fact]
    public async Task blob丢失时标记为文件丢失()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        // 直接从池里把 blob 删掉（模拟磁盘损坏/被误删）
        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, run.SnapshotId);
        string hash = image["a.txt"];
        h.Fs.DeleteFile(h.BlobStores.Create(TestHarness.BackupRoot).GetFullBlobPath(hash));

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);

        // 关键：**计划阶段**就要列出来，不能等用户点了确认、开始执行才发现
        Assert.Equal(1, plan.UnavailableCount);
        Assert.Equal(0, plan.PlannedCount);
        Assert.Contains("文件丢失", plan.UnavailableEntries[0].UnavailableReason);

        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, result.UnavailableCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Contains("文件丢失", result.Failures[0].Reason);
    }

    // ------------------------------------------------------------------
    // 白名单（设计文档 04 §1.3，最重要的安全缓解）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 白名单之外的目标在计划阶段就被拒绝()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], @"C:\Windows\System32",
                RestoreConflictPolicy.Skip),
            CancellationToken.None));
    }

    [Fact]
    public async Task 白名单为空时还原不可用()
    {
        await using TestHarness h = await TestHarness.CreateAsync();   // 刻意不配置白名单
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None));
    }

    [Fact]
    public async Task 含相对片段的目标被拒绝_穿越防护()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [],
                TestHarness.Combine(TestHarness.RestoreRoot, @"..\windows"), RestoreConflictPolicy.Skip),
            CancellationToken.None));
    }

    [Fact]
    public async Task 相对路径里含相对片段被拒绝()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [@"..\..\windows\evil.dll"],
                TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None));
    }

    [Fact]
    public async Task 还原路径超出白名单根时被拒绝()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        // 与白名单根前缀相似但不是它的子树
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot + "2",
                RestoreConflictPolicy.Skip),
            CancellationToken.None));
    }

    [Fact]
    public async Task 还原纳入结果汇总且失败清单可查()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("ok.txt", "ok", T1);
        h.AddSourceFile("bad.txt", "bad", T1);
        BackupRunResult run = await h.BackupAsync(task.Id);

        h.Fs.CreateFailure = path => path.EndsWith("bad.txt", StringComparison.OrdinalIgnoreCase)
            ? new IOException("模拟写失败")
            : null;

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);
        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, result.RestoredCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Single(result.Failures);
        Assert.Equal("bad.txt", result.Failures[0].RelativePath);
    }

    [Fact]
    public async Task 还原空快照成功且不写任何文件()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);
        RestoreResult result = await h.Restore.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(0, plan.TotalCount);
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task 还原可以取消()
    {
        await using TestHarness h = await WithRootsAsync();
        var task = await h.CreateTaskAsync();
        for (int i = 0; i < 20; i++)
        {
            h.AddSourceFile($"f{i}.txt", "content-" + i, T1);
        }

        BackupRunResult run = await h.BackupAsync(task.Id);

        RestorePlan plan = await h.Restore.PlanAsync(
            new RestoreRequest(task.Id, run.SnapshotId, [], TestHarness.RestoreRoot, RestoreConflictPolicy.Skip),
            CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Restore.ExecuteAsync(plan, null, cts.Token));
    }
}
