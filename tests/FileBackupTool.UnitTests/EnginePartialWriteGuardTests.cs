using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.IO;
using FileBackupTool.Core.Models;
using FileBackupTool.Data;
using FileBackupTool.Engine.Blobs;
using FileBackupTool.UnitTests.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 部分写入防护：**不许把还在写的文件当成完整文件固化**。
/// </summary>
/// <remarks>
/// 现实里的触发场景：用户正在用 ffmpeg 把几段 MP4 合成到 O 盘，或从相机卡往数据盘拷素材；
/// 此时监控/定时备份一跑，就会把那个"还没写完"的文件读走。
/// <para>
/// 危害不是"读到旧内容"这么轻：blob 名就是内容哈希，按读到的那几百兆算出来的哈希与内容
/// **完全自洽**，所以后续任何完整性校验都会替它作证 —— 只有比对源文件长度才看得见问题。
/// 这些用例盯的就是这条底线。
/// </para>
/// </remarks>
public class EnginePartialWriteGuardTests
{
    /// <summary>假时钟的起点，也是内存文件系统默认的文件时间。</summary>
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------
    // 前置守卫（时间年龄）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 刚写入的文件本轮跳过且不产生记录与blob()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();

        // 时钟在 T0，文件最后修改也是 T0 → 年龄 0 秒，判定"还在写"
        h.AddSourceFile("growing.mp4", "partial-content", T0);

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Equal(0, h.CountBlobs());
        Assert.Empty(await h.ReadImageAsync(task.Id, result.SnapshotId));
    }

    [Fact]
    public async Task 早已写完的文件照常备份()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();

        // 时钟 T0，文件时间 T0-120s → 年龄 120 秒 > 60 秒
        h.AddSourceFile("done.mp4", "complete-content", T0.AddSeconds(-120));

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(0, result.SkippedFileCount);
        Assert.Equal(1, h.CountBlobs());
    }

    [Fact]
    public async Task 时间戳略微超前时按仍在写入处理()
    {
        // 文件刚创建、时钟粒度或 NTP 小步调整都可能让时间戳略微超前
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("from-near-future.txt", "abc", T0.AddSeconds(10));

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal(0, h.CountBlobs());
    }

    [Fact]
    public async Task 时间戳大幅超前时照常备份而不是无限期跳过()
    {
        // 相机或另一台机器的时钟不准，文件时间可能领先一小时。
        // 若一直按"仍在写入"处理，它会永远进不了备份 —— 那比带一个不准的时间戳糟糕得多。
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("from-skewed-camera.txt", "abc", T0.AddHours(1));

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(0, result.SkippedFileCount);
        Assert.Equal(1, h.CountBlobs());
    }

    [Fact]
    public async Task 静默期配置为零时守卫关闭()
    {
        // 逃生口：排障/历史行为对照时用
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 0);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("just-created.txt", "abc", T0);

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(1, result.CreatedFileCount);
        Assert.Equal(0, result.SkippedFileCount);
    }

    // ------------------------------------------------------------------
    // 后置复核（拷贝期间发生变化）
    // ------------------------------------------------------------------

    [Fact]
    public async Task 拷贝期间文件变长时本轮作废该文件()
    {
        // 时间守卫关掉，只留后置复核 —— 这条路径才是唯一能兜住"元数据滞后"的一层
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 0);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("merging.mp4", "head", T0);

        string target = h.SourcePath("merging.mp4");
        h.Fs.OnOpenRead = path =>
        {
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                // 模拟"读到一半 ffmpeg 又写了一截"：长度变了、时间不变（元数据的典型形态）
                h.Fs.AddFile(target, System.Text.Encoding.UTF8.GetBytes("head-and-much-more"), T0);
                h.Fs.SetLastWriteTimeUtc(target, T0);
            }
        };

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(0, result.CreatedFileCount);
        Assert.Equal(1, result.SkippedFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.Equal(0, h.CountBlobs());
        Assert.Empty(await h.ReadImageAsync(task.Id, result.SnapshotId));
    }

    [Fact]
    public async Task 拷贝期间文件变长时临时blob被丢弃()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 0);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("merging.mp4", "head", T0);
        string target = h.SourcePath("merging.mp4");
        h.Fs.OnOpenRead = path =>
        {
            if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                h.Fs.AddFile(target, System.Text.Encoding.UTF8.GetBytes("head-and-much-more"), T0);
                h.Fs.SetLastWriteTimeUtc(target, T0);
            }
        };

        await h.BackupAsync(task.Id);

        // 临时文件不能在 .tmp 里留下垃圾：finally 里的 DiscardTemporary 必须生效
        Assert.Equal(0, h.CountBlobs());
    }

    // ------------------------------------------------------------------
    // 计数语义：跳过 ≠ 失败
    // ------------------------------------------------------------------

    [Fact]
    public async Task 大量跳过不会触发失败率中止()
    {
        // 失败率阈值压到 1%、最小样本 1：若把跳过算成失败，这一轮必然中止
        await using TestHarness h = await TestHarness.CreateAsync(o =>
        {
            o.MinFileAgeSeconds = 60;
            o.FailureRatioThreshold = 0.01;
            o.FailureRatioMinSamples = 1;
        });
        var task = await h.CreateTaskAsync();

        for (int i = 0; i < 10; i++)
        {
            h.AddSourceFile($"v{i}.mp4", "still-writing", T0);
        }

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Completed, result.Status);
        Assert.Equal(10, result.SkippedFileCount);
        Assert.Equal(0, result.FailedFileCount);
        Assert.False(result.HasFailures);
    }

    [Fact]
    public async Task 跳过的文件不稀释失败率()
    {
        // 1 个真失败 + 19 个跳过：若用 Processed 当分母会算成 1/20 = 5%（低于 10% 阈值），
        // "失败率超阈值就中止"这条保护会被跳过数稀释掉。
        // 分母只算真正尝试过的文件（Attempted），1/1 = 100% → 必须中止。
        await using TestHarness h = await TestHarness.CreateAsync(o =>
        {
            o.MinFileAgeSeconds = 60;
            o.FailureRatioThreshold = 0.10;
            o.FailureRatioMinSamples = 1;
        });
        var task = await h.CreateTaskAsync();

        string bad = h.SourcePath("zzz-bad.txt");

        // 文件名刻意排在写文件之后：枚举是 Ordinal 排序的，坏文件必须先"轮到"失败，
        // 19 个跳过才会先发生 —— 否则第一颗文件就失败中止，根本测不到"跳过是否稀释分母"。
        h.AddSourceFile("zzz-bad.txt", "boom", T0.AddSeconds(-120));
        h.Fs.OpenReadFailure = path =>
            string.Equals(path, bad, StringComparison.OrdinalIgnoreCase)
                ? new System.IO.IOException("模拟读取失败")
                : null;

        for (int i = 0; i < 19; i++)
        {
            h.AddSourceFile($"writing{i}.mp4", "still-going", T0);
        }

        BackupRunResult result = await h.BackupAsync(task.Id);

        Assert.Equal(SnapshotStatus.Aborted, result.Status);
        Assert.Contains("失败率", result.AbortReason);

        // 关键断言：19 个跳过**真的发生了**。
        // 没有这条，用旧的 Processed 分母同样会通过 —— 用例就守不住这次修复。
        Assert.Equal(19, result.SkippedFileCount);
    }

    [Fact]
    public async Task 跳过计数落库并可从快照读回()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("a.mp4", "x", T0);
        h.AddSourceFile("b.mp4", "y", T0);

        BackupRunResult result = await h.BackupAsync(task.Id);

        await using AppDbContext db = await h.Db.OpenAsync();
        var snapshot = await db.Snapshots.AsNoTracking()
            .SingleAsync(s => s.Id == result.SnapshotId);

        Assert.Equal(2, snapshot.SkippedFileCount);
        Assert.Equal(0, snapshot.FailedFileCount);
    }

    // ------------------------------------------------------------------
    // 自愈：下一轮自动补上
    // ------------------------------------------------------------------

    [Fact]
    public async Task 文件写完后下一轮备份自动补上()
    {
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 60);
        var task = await h.CreateTaskAsync();
        h.AddSourceFile("video.mp4", "content", T0);

        BackupRunResult first = await h.BackupAsync(task.Id);
        Assert.Equal(1, first.SkippedFileCount);
        Assert.Equal(0, h.CountBlobs());

        // 文件写完、静默期过去
        h.Clock.AdvanceSeconds(120);

        BackupRunResult second = await h.BackupAsync(task.Id);
        Assert.Equal(1, second.CreatedFileCount);
        Assert.Equal(0, second.SkippedFileCount);
        Assert.Equal(1, h.CountBlobs());
    }

    // ------------------------------------------------------------------
    // 低 IO 优先级（只能在进程内做烟雾测试：真实让路效果要真机争用才看得出来）
    // ------------------------------------------------------------------

    [Fact]
    public void 低IO优先级设置对空句柄返回false而不抛异常()
    {
        Assert.False(IoPriority.TryApplyBackground(null));
    }

    [Fact]
    public async Task 去重命中但长度对不上时作废()
    {
        // 哈希去重命中这条路径原来只做元数据复核，而元数据恰恰可能滞后。
        // 场景：枚举后文件被截断（或反之），按当前内容算出的哈希**恰好**命中池里已有的 blob，
        // 但两者长度不同 —— 交付它就会写一条"长度 L、内容却是别的东西"的记录。
        await using TestHarness h = await TestHarness.CreateAsync(o => o.MinFileAgeSeconds = 0);
        var task = await h.CreateTaskAsync();

        h.AddSourceFile("seed.txt", "abcdefgh", T0);   // 8 字节，先让它进池
        await h.BackupAsync(task.Id);

        string path = h.SourcePath("seed.txt");

        // 元数据撒谎：报告 3 字节、时间不变；真实内容仍是 8 字节，且其哈希已在池里
        h.Fs.OverrideGetFile = p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)
            ? new FileEntry(path, 3, T0, default)
            : null;

        var acquirer = new BlobAcquirer(
            h.Fs,
            h.BlobStores.Create(TestHarness.BackupRoot),
            h.Hash,
            h.JobOptions,
            h.IoSwitch,
            NullLoggerFactory.Instance);

        var entry = new FileEntry(path, 3, T0, default);

        // anyRecordWithSameLength: true → 走"先算哈希、命中就零写入"那条路径
        await Assert.ThrowsAsync<FileUnstableException>(
            () => acquirer.AcquireAsync(entry, anyRecordWithSameLength: true, CancellationToken.None));
    }

    [Fact]
    public void 低IO优先级设置对真实句柄不抛异常且失败即自我标记不可用()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fbt-ioprio-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(path, "probe");
        try
        {
            using FileStream stream = File.OpenRead(path);

            bool applied = IoPriority.TryApplyBackground(stream.SafeFileHandle);

            // 契约：要么真的设置成功，要么记下"这台机器没有这个能力"并不再尝试。
            // 不允许出现"返回 false 却仍然自称可用"——那等于每个文件白调一次系统调用。
            Assert.True(applied || !IoPriority.IsAvailable);

            if (applied)
            {
                Assert.True(IoPriority.HasEverApplied);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 开关默认开启且可运行时切换()
    {
        var toggle = new IoPrioritySwitch();
        Assert.True(toggle.Enabled);

        toggle.Enabled = false;
        Assert.False(toggle.Enabled);

        Assert.True(new IoPrioritySwitch(enabled: false).Enabled == false);
    }
}

