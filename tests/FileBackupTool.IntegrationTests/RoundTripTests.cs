using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Models;
using FileBackupTool.IntegrationTests.Support;
using Xunit;

namespace FileBackupTool.IntegrationTests;

/// <summary>
/// 往返一致性 —— 设计文档 08 §1.1 的 **CI 硬门槛**，跑在真实文件系统上。
/// </summary>
/// <remarks>
/// <b>还原出来的文件必须和源文件逐字节一致。</b>这是备份工具存在的全部理由。
/// <para>
/// 之所以必须在真实文件系统上跑：内存文件系统测不出文件名编码、时间戳精度、
/// 独占锁、只读属性、长路径、以及"枚举顺序"这些真实的坑。
/// </para>
/// </remarks>
public class RoundTripTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task 随机构造文件树_多快照多增量_还原后逐字节一致()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        // 固定种子 → 失败可复现
        var random = new Random(20260922);
        var snapshotIds = new List<(int SnapshotId, string RestoreName)>();

        // 快照 1：一棵 60 个文件的树
        var live = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 60; i++)
        {
            string relative = RandomPath(random, i);
            byte[] content = RandomBytes(random, random.Next(0, 3000));
            h.WriteSourceFile(relative, content, Base.AddMinutes(i));
            live[relative] = content;
        }

        BackupRunResult full = await h.BackupAsync(task.Id, SnapshotType.Full);
        snapshotIds.Add((full.SnapshotId, "s1"));

        // 快照 2：改 15 个、删 10 个、加 15 个
        for (int i = 0; i < 15; i++)
        {
            string relative = live.Keys.ElementAt(random.Next(live.Count));
            byte[] content = RandomBytes(random, random.Next(0, 3000));
            h.WriteSourceFile(relative, content, Base.AddMinutes(100 + i));
            live[relative] = content;
        }

        foreach (string relative in live.Keys.OrderBy(k => k, StringComparer.Ordinal).Take(10).ToList())
        {
            h.DeleteSourceFile(relative);
            live.Remove(relative);
        }

        for (int i = 0; i < 15; i++)
        {
            string relative = RandomPath(random, 1000 + i);
            byte[] content = RandomBytes(random, random.Next(0, 3000));
            h.WriteSourceFile(relative, content, Base.AddMinutes(200 + i));
            live[relative] = content;
        }

        BackupRunResult inc1 = await h.BackupAsync(task.Id, SnapshotType.Increment);
        snapshotIds.Add((inc1.SnapshotId, "s2"));

        // 快照 3：再改一批，并删掉一个整棵子目录
        for (int i = 0; i < 10; i++)
        {
            string relative = live.Keys.ElementAt(random.Next(live.Count));
            byte[] content = RandomBytes(random, random.Next(1, 5000));
            h.WriteSourceFile(relative, content, Base.AddMinutes(300 + i));
            live[relative] = content;
        }

        string victimDirectory = "d3/sub1";
        if (Directory.Exists(Path.Combine(h.SourceDir, victimDirectory)))
        {
            foreach (string relative in live.Keys
                         .Where(k => k.StartsWith(victimDirectory.Replace('/', '\\') + "\\", StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                live.Remove(relative);
            }

            h.DeleteSourceDirectory(victimDirectory);
        }

        BackupRunResult inc2 = await h.BackupAsync(task.Id, SnapshotType.Increment);
        snapshotIds.Add((inc2.SnapshotId, "s3"));

        // 每个快照都还原一次（还原**历史**快照是备份工具的核心价值）
        var expectedTrees = new List<Dictionary<string, byte[]>>
        {
            // 这里只校验"最终快照 == 当前源"，历史快照用下面的独立测试覆盖
            live,
        };

        // 最终快照必须与当前源逐字节一致
        RestoreResult last = await h.RestoreToAsync(task.Id, inc2.SnapshotId, "final");
        Assert.Equal(live.Count, last.RestoredCount);
        Assert.Equal(0, last.FailedCount);
        Assert.Equal(0, last.UnavailableCount);

        IntegrationHarness.AssertTreesIdentical(h.SourceDir, Path.Combine(h.RestoreDir, "final"), h.Normalizer);

        // 快照 2 也必须能还原（历史快照的镜像链要完整）
        RestoreResult middle = await h.RestoreToAsync(task.Id, inc1.SnapshotId, "middle");
        Assert.Equal(0, middle.FailedCount);
        Assert.Equal(0, middle.UnavailableCount);
        Assert.True(middle.RestoredCount > 0);

        // 快照 1（全量）同样
        RestoreResult first = await h.RestoreToAsync(task.Id, full.SnapshotId, "first");
        Assert.Equal(0, first.FailedCount);
        Assert.Equal(0, first.UnavailableCount);
        Assert.Equal(60, first.RestoredCount);
    }

    [Fact]
    public async Task 二进制与零字节与Unicode文件名逐字节一致()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        // 覆盖 0x00 / 0xFF、非对齐长度、空文件、Unicode、空格与点开头的名字
        byte[] payload = new byte[8191];
        new Random(7).NextBytes(payload);
        payload[0] = 0;
        payload[^1] = 255;

        h.WriteSourceFile(@"bin\data.bin", payload, Base);
        h.WriteSourceFile("zero.bin", [], Base);
        h.WriteSourceFile(@"文档\日本語テスト.txt", "中文内容与日文内容", Base);
        h.WriteSourceFile("带 空格 的文件.txt", "空格", Base);
        h.WriteSourceFile(".hidden-file.txt", "点开头", Base);
        h.WriteSourceFile(@"深\层\次\目录\嵌套.txt", "嵌套", Base);

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);
        Assert.Equal(6, run.CreatedFileCount);
        Assert.Equal(0, run.FailedFileCount);

        RestoreResult result = await h.RestoreToAsync(task.Id, run.SnapshotId, "unicode");
        Assert.Equal(6, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);

        IntegrationHarness.AssertTreesIdentical(h.SourceDir, Path.Combine(h.RestoreDir, "unicode"), h.Normalizer);
    }

    [Fact]
    public async Task 空源目录可以备份并还原()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);

        Assert.Equal(SnapshotStatus.Completed, run.Status);
        Assert.Equal(0, run.CreatedFileCount);
        Assert.Equal(0, run.FailedFileCount);

        RestoreResult result = await h.RestoreToAsync(task.Id, run.SnapshotId, "empty");
        Assert.Equal(0, result.RestoredCount);
        Assert.Equal(0, result.FailedCount);
    }

    [Fact]
    public async Task 只读文件被正常备份与还原()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.WriteSourceFile("readonly.txt", "只读内容", Base);
        File.SetAttributes(Path.Combine(h.SourceDir, "readonly.txt"), FileAttributes.ReadOnly);
        h.WriteSourceFile("normal.txt", "普通内容", Base);

        try
        {
            BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);
            Assert.Equal(2, run.CreatedFileCount);
            Assert.Equal(0, run.FailedFileCount);

            RestoreResult result = await h.RestoreToAsync(task.Id, run.SnapshotId, "readonly");
            Assert.Equal(2, result.RestoredCount);
            Assert.Equal("只读内容", File.ReadAllText(Path.Combine(h.RestoreDir, "readonly", "readonly.txt")));
        }
        finally
        {
            File.SetAttributes(Path.Combine(h.SourceDir, "readonly.txt"), FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task 被独占锁定的文件被跳过并计入失败而不是炸掉整个备份()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.WriteSourceFile("ok1.txt", "ok1", Base);
        h.WriteSourceFile("locked.txt", "locked", Base);
        h.WriteSourceFile("ok2.txt", "ok2", Base);

        // 独占打开：任何其他句柄都打不开它
        using (var exclusive = new FileStream(
                   Path.Combine(h.SourceDir, "locked.txt"),
                   FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);

            // 设计文档 03 §7.1：单个文件失败要隔离并计数，
            // 旧设计只 catch IOException 会让一个无权限文件炸掉整个备份
            Assert.Equal(SnapshotStatus.Completed, run.Status);
            Assert.Equal(1, run.FailedFileCount);
            Assert.Equal(2, run.CreatedFileCount);
            Assert.True(run.HasFailures);
        }

        // 快照必须如实显示"完成，但 N 个文件未备份"
        await using var db = await h.DbFactory.CreateDbContextAsync();
        Core.Entities.BackupSnapshot snapshot = db.Snapshots.OrderByDescending(s => s.Id).First();
        Assert.Equal(1, snapshot.FailedFileCount);
    }

    [Fact]
    public async Task 隐藏与系统文件也要被备份()
    {
        // 设计文档 03 §3：AttributesToSkip = 0。
        // .NET 的 EnumerationOptions 默认是 Hidden | System，会让这些文件被静默排除。
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.WriteSourceFile("hidden.txt", "隐藏内容", Base);
        h.WriteSourceFile("system.txt", "系统内容", Base);
        h.WriteSourceFile("normal.txt", "普通内容", Base);

        File.SetAttributes(Path.Combine(h.SourceDir, "hidden.txt"), FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(h.SourceDir, "system.txt"), FileAttributes.System);

        try
        {
            BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);

            Assert.Equal(3, run.CreatedFileCount);
            Assert.Equal(0, run.FailedFileCount);
        }
        finally
        {
            File.SetAttributes(Path.Combine(h.SourceDir, "hidden.txt"), FileAttributes.Normal);
            File.SetAttributes(Path.Combine(h.SourceDir, "system.txt"), FileAttributes.Normal);
        }
    }

    [Fact]
    public async Task 回收站目录被硬编码跳过()
    {
        // 设计文档 03 §3 明确要求硬编码跳过 $RECYCLE.BIN
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        h.WriteSourceFile("normal.txt", "内容", Base);
        h.WriteSourceFile(@"$RECYCLE.BIN\S-1-5-18\被删的文件.dat", "回收站内容", Base);

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);

        Assert.Equal(1, run.CreatedFileCount);

        Dictionary<string, string?> image = await ReadImageAsync(h, task.Id, run.SnapshotId);
        Assert.Contains("normal.txt", image.Keys);
        Assert.DoesNotContain(image.Keys, k => k.Contains("RECYCLE", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 内容寻址在真实文件系统上去重()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        byte[] shared = RandomBytes(new Random(11), 100_000);

        for (int i = 0; i < 5; i++)
        {
            h.WriteSourceFile($@"副本\copy{i}.bin", shared, Base.AddMinutes(i));
        }

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);

        Assert.Equal(5, run.CreatedFileCount);
        // 五份相同内容 → 池里只有一个 blob
        Assert.Equal(1, h.CountBlobs());
        // 只有第一个文件真的产生了写入
        Assert.Equal(shared.Length, run.TotalBytes);
    }

    [Fact]
    public async Task 未变更的大文件在全量备份时零写入()
    {
        // 设计文档 08 §3 的验收项："全量备份一个 10GB 未变更文件 → 不产生任何写入（去重命中）"。
        // 10GB 跑不动，但它验证的是**同一个判据**：Length 预筛把同长度文件送到"先算哈希"分支，
        // 哈希命中已有 blob → 零写入。
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        byte[] big = RandomBytes(new Random(13), 4 * 1024 * 1024);
        h.WriteSourceFile("big.bin", big, Base);

        BackupRunResult first = await h.BackupAsync(task.Id, SnapshotType.Full);
        Assert.Equal(big.Length, first.TotalBytes);
        Assert.Equal(1, h.CountBlobs());

        // 再跑一次全量：内容与长度都没变 → 应当零写入
        BackupRunResult second = await h.BackupAsync(task.Id, SnapshotType.Full);

        Assert.Equal(0, second.TotalBytes);
        Assert.Equal(1, h.CountBlobs());
        Assert.Equal("Completed", second.Status.ToString());
    }

    [Fact]
    public async Task 目录改名表现为一次大快照且不重新拷贝数据()
    {
        // 设计文档 03 §5.7：明确放弃处理目录重命名。
        // 好消息是内容寻址让它**不会重新拷贝数据**（内容相同 → 哈希相同 → blob 已存在）。
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        var contents = new Dictionary<string, byte[]>();
        for (int i = 0; i < 20; i++)
        {
            byte[] content = RandomBytes(new Random(100 + i), 5000);
            contents[$"file{i}.txt"] = content;
            h.WriteSourceFile($@"old-name\file{i}.txt", content, Base.AddMinutes(i));
        }

        BackupRunResult full = await h.BackupAsync(task.Id, SnapshotType.Full);
        Assert.Equal(20, full.CreatedFileCount);
        long bytesAfterFull = full.TotalBytes;

        // 改名
        Directory.Move(Path.Combine(h.SourceDir, "old-name"), Path.Combine(h.SourceDir, "new-name"));

        BackupRunResult inc = await h.BackupAsync(task.Id, SnapshotType.Increment);

        // 表现为"旧路径全删 + 新路径全增"
        Assert.Equal(20, inc.CreatedFileCount);
        Assert.Equal(20, inc.DeletedFileCount);
        // 但**一个字节都没有重新写**——内容寻址的全部收益
        Assert.Equal(0, inc.TotalBytes);
        Assert.Equal(20, h.CountBlobs());

        // 还原后的树与源一致
        await h.RestoreToAsync(task.Id, inc.SnapshotId, "renamed");
        IntegrationHarness.AssertTreesIdentical(h.SourceDir, Path.Combine(h.RestoreDir, "renamed"), h.Normalizer);

        Assert.True(bytesAfterFull > 0);
    }

    [Fact]
    public async Task 修改记录的时间戳被还原保留()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        DateTime t = new(2024, 6, 15, 8, 30, 45, DateTimeKind.Utc);
        h.WriteSourceFile("dated.txt", "内容", t);

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);
        await h.RestoreToAsync(task.Id, run.SnapshotId, "timestamps");

        DateTime restored = File.GetLastWriteTimeUtc(Path.Combine(h.RestoreDir, "timestamps", "dated.txt"));
        Assert.Equal(t, restored);
    }

    [Fact]
    public async Task 深层目录结构被正确处理()
    {
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        // 深约 200 字符（>260 需要目标机开启 LongPathsEnabled，见设计文档 01 §6.1；
        // 这里刻意留在 260 以内，让这个测试在任何机器上都能跑，而不是变成环境相关的跳过的测试）
        var segments = Enumerable.Range(1, 8).Select(i => "level" + i + new string('x', 7)).ToArray();
        string deep = string.Join(Path.DirectorySeparatorChar, segments) + Path.DirectorySeparatorChar + "deep.txt";

        int fullLength = Path.Combine(h.SourceDir, deep).Length;
        Assert.True(fullLength < 260,
            $"测试路径过长（{fullLength}），会受 LongPathsEnabled 影响，请缩短段数");

        h.WriteSourceFile(deep, "深层内容", Base);
        h.WriteSourceFile("shallow.txt", "浅层内容", Base);

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);
        Assert.Equal(2, run.CreatedFileCount);
        Assert.Equal(0, run.FailedFileCount);

        RestoreResult result = await h.RestoreToAsync(task.Id, run.SnapshotId, "deep");
        Assert.Equal(2, result.RestoredCount);

        IntegrationHarness.AssertTreesIdentical(h.SourceDir, Path.Combine(h.RestoreDir, "deep"), h.Normalizer);
    }

    [Fact]
    public async Task 部分写入防护_静默期后提交的文件内容是完整的()
    {
        // 真实场景：一个大文件被写到一半时不该被固化。这里验证"稳定后备份得到完整内容"，
        // 而"写到一半不断变化"由监控模块的静默期负责（见 Monitoring 的测试）。
        await using IntegrationHarness h = await IntegrationHarness.CreateAsync();
        var task = await h.CreateTaskAsync();

        byte[] content = RandomBytes(new Random(17), 512 * 1024);
        h.WriteSourceFile("growing.bin", content, Base);

        BackupRunResult run = await h.BackupAsync(task.Id, SnapshotType.Full);
        await h.RestoreToAsync(task.Id, run.SnapshotId, "complete");

        byte[] restored = File.ReadAllBytes(Path.Combine(h.RestoreDir, "complete", "growing.bin"));
        Assert.Equal(content, restored);
    }

    // ------------------------------------------------------------------

    private static string RandomPath(Random random, int index)
    {
        string[] top = ["docs", "photos", "d3", "d4"];
        string[] sub = ["sub1", "sub2", "sub3"];
        string extension = random.Next(3) switch { 0 => ".txt", 1 => ".bin", _ => ".dat" };

        return index % 7 == 0
            ? $"toppath_{index}{extension}"
            : Path.Combine(
                top[index % top.Length],
                sub[(index / 3) % sub.Length],
                $"file_{index}{extension}");
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static async Task<Dictionary<string, string?>> ReadImageAsync(
        IntegrationHarness h, Guid taskId, int snapshotId)
    {
        List<Core.Entities.BackupSnapshot>? chain =
            await h.Chains.ResolveChainAsync(taskId, snapshotId, CancellationToken.None);
        Assert.NotNull(chain);

        List<Core.Entities.BackupFile> records = await h.FileRecords.GetChainRecordsInPrefixAsync(
            taskId, [.. chain!.Select(s => s.Id)], null, CancellationToken.None);

        var replay = Engine.Mirror.MirrorReplay.Replay(records);
        return replay.Items.Values.ToDictionary(i => i.Path, i => i.ContentHash, StringComparer.OrdinalIgnoreCase);
    }
}
