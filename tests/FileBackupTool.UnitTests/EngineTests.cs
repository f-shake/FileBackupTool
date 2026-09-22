using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 备份引擎。设计文档 08 §1.4 的单元测试清单。
/// </summary>
public class EngineTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // 全量
    // ------------------------------------------------------------------

    [Fact]
    public async Task 全量备份写入blob并建立镜像()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile(@"docs\b.txt", "bbbbb", T1);

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(2, result.CreatedFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Equal(8, result.TotalBytes);
        Assert.Equal(2, h.CountBlobs());

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(2, image.Count);
        Assert.Contains(@"docs\b.txt", image.Keys);
        Assert.Equal("bbbbb", h.ReadBlobText(image[@"docs\b.txt"]));
    }

    [Fact]
    public async Task 空源目录的全量备份成功且镜像为空()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(0, result.ImageFileCount);
        Assert.Empty(await h.ReadImageAsync(task.Id, result.SnapshotId));
    }

    // ------------------------------------------------------------------
    // 内容寻址去重（设计文档 03 §3.3、08 §1.4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 同一内容在两个路径下只产生一个blob()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"one\same.txt", "一模一样的内容", T1);
        h.AddSourceFile(@"two\copy.txt", "一模一样的内容", T1);

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(2, result.CreatedFileCount);
        Assert.Equal(1, h.CountBlobs());

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(image[@"one\same.txt"], image[@"two\copy.txt"]);
    }

    [Fact]
    public async Task 无同长度记录时走边拷边算且不产生白写()
    {
        // 长度全新的文件必然内容全新 → 边拷边算的写入**不可能**被丢弃
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("only.txt", "unique-length-payload", T1);

        int createsBefore = h.Fs.CreateCount;
        int deletesBefore = h.Fs.DeleteCount;

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(1, h.Fs.CreateCount - createsBefore);
        Assert.Equal(0, h.Fs.DeleteCount - deletesBefore);   // 没有任何临时文件被丢弃
        Assert.Equal(1, h.CountBlobs());
        Assert.Equal(result.TotalBytes, "unique-length-payload".Length);
    }

    [Fact]
    public async Task 同长度且blob已存在时零写入()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("a.txt", "0123456789", T1);      // 10 字节
        h.AddSourceFile("b.txt", "abcdefghij", T1);      // 同样 10 字节，内容不同
        await h.BackupAsync(task.Id);
        Assert.Equal(2, h.CountBlobs());

        // 再来一个 10 字节、内容与 a.txt 相同的新文件：
        // Length 预筛会把它判到"先算哈希"那条路径上，命中已有 blob → 0 次写
        h.AddSourceFile("c.txt", "0123456789", T1);

        int createsBefore = h.Fs.CreateCount;
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(0, h.Fs.CreateCount - createsBefore);
        Assert.Equal(0, result.TotalBytes);
        Assert.Equal(2, h.CountBlobs());

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(image["a.txt"], image["c.txt"]);
    }

    [Fact]
    public async Task 同长度但blob不存在时先算哈希再拷贝()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("a.txt", "0123456789", T1);
        await h.BackupAsync(task.Id);

        // 同长度、全新内容 → 无法证明唯一，先算哈希（未命中）再拷一遍
        h.AddSourceFile("b.txt", "abcdefghij", T1);

        int createsBefore = h.Fs.CreateCount;
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(1, h.Fs.CreateCount - createsBefore);
        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(2, h.CountBlobs());
    }

    // ------------------------------------------------------------------
    // 增量（设计文档 03 §3.2）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 增量只记录变化且未变化不产生记录()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile("b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);

        h.AddSourceFile("a.txt", "aaaaa", T2);                       // 内容与时间都变了
        h.AddSourceFile("c.txt", "ccc", T2);                         // 新增

        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(1, result.ModifiedFileCount);
        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(0, result.DeletedFileCount);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(3, image.Count);
        Assert.Equal("aaaaa", h.ReadBlobText(image["a.txt"]));
    }

    [Fact]
    public async Task 增量记录删除墓碑()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        h.AddSourceFile("b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);

        h.RemoveSourceFile("b.txt");
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(1, result.DeletedFileCount);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Single(image);
        Assert.True(image.ContainsKey("a.txt"));
        Assert.False(image.ContainsKey("b.txt"));
    }

    [Fact]
    public async Task 大小写不同视为同一路径()
    {
        // 设计文档 02 §3.2：Windows 上 IMG.JPG 与 img.jpg 是同一个文件。
        // 旧设计只在防抖字典的 key 上做了归一化，存进数据库的路径没归一化 → 产生重复行。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("IMG.JPG", "first", T1);
        await h.BackupAsync(task.Id);

        h.AddSourceFile("img.jpg", "second", T2);

        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(1, result.ModifiedFileCount);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Single(image);
        Assert.Equal("second", h.ReadBlobText(image.Values.Single()));
    }

    [Fact]
    public async Task 镜像规模在增量中正确维护()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);       // 3
        h.AddSourceFile("b.txt", "bbbbb", T1);     // 5
        BackupRunResult full = await h.BackupAsync(task.Id);

        Assert.Equal(2, full.ImageFileCount);
        Assert.Equal(8, full.ImageTotalBytes);

        h.AddSourceFile("a.txt", new string('a', 10), T2);   // 改成 10 字节
        BackupRunResult inc1 = await h.BackupAsync(task.Id, SnapshotType.Increment);
        Assert.Equal(2, inc1.ImageFileCount);
        Assert.Equal(15, inc1.ImageTotalBytes);

        h.RemoveSourceFile("b.txt");
        BackupRunResult inc2 = await h.BackupAsync(task.Id, SnapshotType.Increment);
        Assert.Equal(1, inc2.ImageFileCount);
        Assert.Equal(10, inc2.ImageTotalBytes);
    }

    // ------------------------------------------------------------------
    // 局部扫描（设计文档 03 §5.4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 局部扫描不误判范围外的删除()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\a.txt", "aaa", T1);
        h.AddSourceFile(@"photos\b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);

        // photos 下的文件被删了，但这次只扫 docs
        h.RemoveSourceFile(@"photos\b.txt");
        BackupRunResult result = await h.BackupAsync(
            task.Id, SnapshotType.Increment, [@"docs"]);

        Assert.Equal(0, result.DeletedFileCount);
        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(0, result.ModifiedFileCount);

        // 范围外的文件是**继承**来的，仍在镜像里
        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(2, image.Count);
        Assert.Contains(@"photos\b.txt", image.Keys);
    }

    [Fact]
    public async Task 局部扫描在范围内正确记录删除与新增()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\a.txt", "aaa", T1);
        h.AddSourceFile(@"docs\gone.txt", "xxx", T1);
        h.AddSourceFile(@"photos\b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);

        h.RemoveSourceFile(@"docs\gone.txt");
        h.AddSourceFile(@"docs\new.txt", "nnn", T2);

        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment, [@"docs"]);

        Assert.Equal(1, result.DeletedFileCount);
        Assert.Equal(1, result.CreatedFileCount);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(3, image.Count);
        Assert.Contains(@"docs\new.txt", image.Keys);
        Assert.DoesNotContain(@"docs\gone.txt", image.Keys);
        Assert.Contains(@"photos\b.txt", image.Keys);
    }

    [Fact]
    public async Task 局部扫描嵌套脏目录被压实()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\2026\01\a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        h.AddSourceFile(@"docs\2026\01\a.txt", "aaaa", T2);

        // 同时给"祖先"和"后代"两个脏目录，压缩后只应扫一次，不产生重复记录
        BackupRunResult result = await h.BackupAsync(
            task.Id, SnapshotType.Increment, [@"docs", @"docs\2026\01"]);

        Assert.Equal(1, result.ModifiedFileCount);
        Assert.Equal(1, result.CreatedFileCount + result.ModifiedFileCount + result.DeletedFileCount);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Single(image);
    }

    [Fact]
    public async Task 局部扫描的脏目录越界被忽略()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        await h.BackupAsync(task.Id);

        // 越界的脏目录会被忽略；全部被忽略时退化为全量扫描
        BackupRunResult result = await h.BackupAsync(
            task.Id, SnapshotType.Increment, [@"..\..\windows"]);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(0, result.CreatedFileCount);
    }

    [Fact]
    public async Task 局部扫描时全部脏目录都消失不会崩()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\a.txt", "aaa", T1);
        h.AddSourceFile(@"photos\b.txt", "bbb", T1);
        await h.BackupAsync(task.Id);

        h.Fs.DeleteDirectory(h.SourcePath("docs"), recursive: true);
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment, [@"docs"]);

        Assert.Equal(1, result.DeletedFileCount);
        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Single(image);
        Assert.Contains(@"photos\b.txt", image.Keys);
    }

    // ------------------------------------------------------------------
    // 逐文件失败隔离（设计文档 03 §7.1）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 单个文件失败不炸掉整个备份并计入失败计数()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("good1.txt", "111", T1);
        h.AddSourceFile("locked.txt", "222", T1);
        h.AddSourceFile("good2.txt", "333", T1);

        h.Fs.OpenReadFailure = path =>
            path.EndsWith("locked.txt", StringComparison.OrdinalIgnoreCase)
                ? new UnauthorizedAccessException("被独占锁定")
                : null;

        BackupRunResult result = await h.BackupAsync(task.Id);

        // 旧设计的 bug：只 catch IOException，而 UnauthorizedAccessException 不是它的子类，
        // 遇到一个无权限的文件会让整个备份直接炸掉。这里断言它被隔离了。
        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedFileCount);
        Assert.Equal(2, result.CreatedFileCount);
        Assert.True(result.HasFailures);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, result.SnapshotId);
        Assert.Equal(2, image.Count);
        Assert.DoesNotContain("locked.txt", image.Keys);
    }

    [Fact]
    public async Task 超长路径计入失败计数而不是被静默跳过()
    {
        // 设计文档 01 §6.1：PathTooLongException 是 IOException 的子类，
        // 会被逐文件失败处理吞掉。这不可接受——必须计入失败计数并显著提示。
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("ok.txt", "fine", T1);
        h.AddSourceFile("a-very-long-name.txt", "unreachable", T1);

        h.Fs.PathTooLongThreshold = h.SourcePath("a-very-long-name.txt").Length;

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedFileCount);
        Assert.Equal(1, result.CreatedFileCount);
        Assert.True(result.HasFailures);
    }

    [Fact]
    public async Task 连续失败次数超过阈值时中止整个备份()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o =>
        {
            o.ConsecutiveFailureThreshold = 3;
            o.FailureRatioMinSamples = 1000;   // 关掉失败率判定，只测连续失败
            o.DbBatchSize = 1;
        });

        var task = await h.CreateTaskAsync();
        for (int i = 0; i < 10; i++)
        {
            h.AddSourceFile($"f{i}.txt", "x", T1);
        }

        h.Fs.OpenReadFailure = _ => new IOException("模拟磁盘错误");

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Aborted, result.Status);
        Assert.NotNull(result.AbortReason);
        Assert.Contains("连续失败", result.AbortReason);
    }

    [Fact]
    public async Task 失败率超过阈值时中止整个备份()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o =>
        {
            o.FailureRatioThreshold = 0.20;
            o.FailureRatioMinSamples = 4;
            o.ConsecutiveFailureThreshold = 1000;
            o.DbBatchSize = 1;
        });

        var task = await h.CreateTaskAsync();
        for (int i = 0; i < 10; i++)
        {
            h.AddSourceFile($"f{i}.txt", "x", T1);
        }

        h.Fs.OpenReadFailure = _ => new IOException("模拟磁盘错误");

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Aborted, result.Status);
        Assert.Contains("失败率", result.AbortReason!);
    }

    [Fact]
    public async Task 少量文件时的个别失败不会中止备份()
    {
        // 失败率指标是用来识别**系统性**故障的；3 个文件坏 1 个不是系统性故障
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "1", T1);
        h.AddSourceFile("bad.txt", "2", T1);
        h.AddSourceFile("c.txt", "3", T1);

        h.Fs.OpenReadFailure = path => path.Contains("bad", StringComparison.OrdinalIgnoreCase)
            ? new IOException("坏")
            : null;

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(1, result.FailedFileCount);
    }

    // ------------------------------------------------------------------
    // 快照状态（设计文档 02 §2.2、03 §7.3/§7.4）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 取消备份把快照标记为Aborted()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.DbBatchSize = 1);
        var task = await h.CreateTaskAsync();
        for (int i = 0; i < 5; i++)
        {
            h.AddSourceFile($"f{i}.txt", "x", T1);
        }

        using var cts = new CancellationTokenSource();
        BackupRunResult result = await h.Backup.RunAsync(
            task.Id, SnapshotType.Full, null, new CancelOnNthReport(cts, 1), cts.Token);

        Assert.Equal(SnapshotStatus.Aborted, result.Status);
        Assert.Contains("取消", result.AbortReason!);

        Core.Entities.BackupSnapshot snapshot = (await h.Chains.GetAsync(result.SnapshotId, CancellationToken.None))!;
        Assert.Equal(SnapshotStatus.Aborted, snapshot.Status);
        Assert.NotNull(snapshot.EndTime);
    }

    [Fact]
    public async Task 启动自愈把InProgress快照标记为Aborted()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        await using (var db = await h.Db.OpenAsync())
        {
            db.Snapshots.Add(new Core.Entities.BackupSnapshot
            {
                TaskId = task.Id,
                Type = SnapshotType.Full,
                Status = SnapshotStatus.InProgress,
                BeginTime = T1,
            });
            await db.SaveChangesAsync();
        }

        List<int> healed = await h.Chains.AbortStaleInProgressAsync(
            "进程在备份过程中退出", h.Clock.UtcNow, CancellationToken.None);

        Assert.Single(healed);

        Core.Entities.BackupSnapshot snapshot = (await h.Chains.GetAsync(healed[0], CancellationToken.None))!;
        Assert.Equal(SnapshotStatus.Aborted, snapshot.Status);
        Assert.NotNull(snapshot.EndTime);
        Assert.Contains("进程在备份过程中退出", snapshot.AbortReason);
    }

    [Fact]
    public async Task Aborted快照的记录不参与镜像重建()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.DbBatchSize = 1);
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);
        BackupRunResult full = await h.BackupAsync(task.Id);

        // 造一个"跑到一半被取消"的增量：它会写入**部分**记录
        h.AddSourceFile("b.txt", "bbb", T2);
        using var cts = new CancellationTokenSource();
        BackupRunResult aborted = await h.Backup.RunAsync(
            task.Id, SnapshotType.Increment, null, new CancelOnNthReport(cts, 2), cts.Token);

        Assert.Equal(SnapshotStatus.Aborted, aborted.Status);

        // 先确认场景是真实的：这个 Aborted 快照确实留下了记录
        await using (var db = await h.Db.OpenAsync())
        {
            int recorded = db.Files.Count(f => f.SnapshotId == aborted.SnapshotId);
            Assert.True(recorded > 0, "这个测试要验证的是「半截记录被排除」，所以必须先有半截记录");
        }

        // Aborted 快照的记录不参与链 → b.txt 不在镜像里
        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, aborted.SnapshotId);
        Assert.Single(image);
        Assert.True(image.ContainsKey("a.txt"));

        // 下一次增量应当把 b.txt 当成新增（而不是"已经存在"）
        BackupRunResult next = await h.BackupAsync(task.Id, SnapshotType.Increment);
        Assert.Equal(1, next.CreatedFileCount);

        Dictionary<string, string> image2 = await h.ReadImageAsync(task.Id, next.SnapshotId);
        Assert.Equal(2, image2.Count);
        Assert.Equal(full.SnapshotId + 2, next.SnapshotId);
    }

    [Fact]
    public async Task 没有上一个全量时增量自动退化为全量()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);

        // 首次备份就请求增量 → 没有可 replay 的上一个镜像 → 退化为全量
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.Increment);

        Assert.Equal(SnapshotType.Full, result.Type);
        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(SnapshotStatus.Completed, result.Status);
    }

    [Fact]
    public async Task 虚拟全量不写blob且标记为不可恢复()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.txt", "aaa", T1);

        int createsBefore = h.Fs.CreateCount;
        BackupRunResult result = await h.BackupAsync(task.Id, SnapshotType.VirtualFull);

        Assert.Equal(SnapshotType.VirtualFull, result.Type);
        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(0, h.Fs.CreateCount - createsBefore);
        Assert.Equal(0, h.CountBlobs());

        Core.Entities.BackupSnapshot snapshot = (await h.Chains.GetAsync(result.SnapshotId, CancellationToken.None))!;
        Assert.False(snapshot.IsRestorable);
    }

    [Fact]
    public async Task 清单写入器在备份完成后被调用并收到涉及路径()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();
        h.AddSourceFile(@"docs\a.txt", "aaa", T1);

        await h.BackupAsync(task.Id);

        Assert.Equal(1, h.ManifestWriter.CallCount);
        Assert.Contains(@"docs\a.txt", h.ManifestWriter.LastTouchedPaths);
    }

    [Fact]
    public async Task 备份被取消时不写清单()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.DbBatchSize = 1);
        var task = await h.CreateTaskAsync();
        for (int i = 0; i < 5; i++)
        {
            h.AddSourceFile($"f{i}.txt", "x", T1);
        }

        using var cts = new CancellationTokenSource();
        BackupRunResult result = await h.Backup.RunAsync(
            task.Id, SnapshotType.Full, null, new CancelOnNthReport(cts, 1), cts.Token);

        Assert.Equal(SnapshotStatus.Aborted, result.Status);
        Assert.Equal(0, h.ManifestWriter.CallCount);
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// **全量快照必须覆盖整个源，哪怕这次是带着脏目录触发的。**
    /// </summary>
    /// <remarks>
    /// 这是实测修掉的一个静默数据损坏：监控触发时会带着"脏目录"来，而按策略这一次可能要跑
    /// **全量**（例如还没有任何 Full，或增量数已超上限）。全量没有可继承的上一个镜像，
    /// 它的语义是"一个快照仍然覆盖整个源"（03 §5.4）——脏目录必须被丢弃。
    /// <para>
    /// 不丢的话，这个"全量"的镜像只含被扫到的那棵子树，而它会被后续增量当作基准：
    /// 链从此长期不一致，抢救清单还会被重写成只剩那棵子树。而且这**极容易触发**——
    /// 任务刚打开监控、第一个快照之前正好有个文件被改动，就会走到这条路径。
    /// </para>
    /// <para>
    /// 反向的守卫见下一条：增量**必须**继续尊重脏目录（局部扫描是它的语义），
    /// 所以修复只对非增量生效。
    /// </para>
    /// </remarks>
    [Fact]
    public async Task 全量备份即使带着脏目录也必须覆盖整个源()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("root.txt", "root", T1);
        h.AddSourceFile(@"docs\doc.txt", "doc", T1);
        h.AddSourceFile(@"media\pic.txt", "pic", T1);

        BackupRunResult full = await h.BackupAsync(task.Id, SnapshotType.Full, [@"docs"]);

        Assert.Equal(SnapshotStatus.Completed, full.Status);

        Dictionary<string, string> image = await h.ReadImageAsync(task.Id, full.SnapshotId);
        Assert.Equal(3, image.Count);
        Assert.True(image.ContainsKey("root.txt"), "源目录根下的文件必须在这个全量镜像里");
        Assert.True(image.ContainsKey(@"docs\doc.txt"), "脏目录里的文件在镜像里");
        Assert.True(image.ContainsKey(@"media\pic.txt"), "**未**被列为脏目录的那棵子树也必须在这个全量镜像里");
    }

    /// <summary>增量的反向守卫：局部扫描是增量的语义，必须继续尊重脏目录。</summary>
    [Fact]
    public async Task 增量备份带脏目录时只重扫该目录_未变化目录由镜像继承()
    {
        await using TestHarness h = await TestHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("root.txt", "root", T1);
        h.AddSourceFile(@"docs\doc.txt", "doc-v1", T1);
        h.AddSourceFile(@"media\pic.txt", "pic", T1);

        BackupRunResult full = await h.BackupAsync(task.Id, SnapshotType.Full);
        // ReadImageAsync 给的是 路径 → **内容哈希**（不是内容）
        Dictionary<string, string> before = await h.ReadImageAsync(task.Id, full.SnapshotId);

        // 只动 docs 里的一个文件
        h.AddSourceFile(@"docs\doc.txt", "doc-v2", T2);

        BackupRunResult inc = await h.BackupAsync(task.Id, SnapshotType.Increment, [@"docs"]);

        Assert.Equal(1, inc.CreatedFileCount + inc.ModifiedFileCount);

        Dictionary<string, string> after = await h.ReadImageAsync(task.Id, inc.SnapshotId);
        Assert.Equal(3, after.Count);
        Assert.NotEqual(before[@"docs\doc.txt"], after[@"docs\doc.txt"]);   // 改了 → 新哈希
        Assert.Equal(before["root.txt"], after["root.txt"]);               // 未触及 → 由镜像继承
        Assert.Equal(before[@"media\pic.txt"], after[@"media\pic.txt"]);   // 子树外 → 必须继承下来
    }

    // ------------------------------------------------------------------

    /// <summary>第 N 次收到进度就取消——用来稳定地制造"跑到一半被取消"。</summary>
    private sealed class CancelOnNthReport(CancellationTokenSource cts, int n) : IProgress<JobProgress>
    {
        private int _count;

        public void Report(JobProgress value)
        {
            if (++_count >= n)
            {
                cts.Cancel();
            }
        }
    }
}
