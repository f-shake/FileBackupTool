using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Backup;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Core.Paths;
using FileBackupTool.Core.Snapshots;
using FileBackupTool.UnitTests.TestSupport;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 全量/增量选取、镜像链、级联删除、去重判据、变更分类、脏目录集合。
/// 设计文档 08 §1.4 的单元测试清单。
/// </summary>
public class PolicyTests
{
    private static BackupSnapshot Snap(int id, SnapshotType type, int minute) => new()
    {
        Id = id,
        TaskId = Guid.Empty,
        Type = type,
        Status = SnapshotStatus.Completed,
        BeginTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(minute),
    };

    // ------------------------------------------------------------------
    // 全量 / 增量选取（设计文档 03 §2，语义沿用旧设计，"不改"）
    // ------------------------------------------------------------------

    [Fact]
    public void 没有任何快照时跑全量()
    {
        SnapshotType type = SnapshotChainPolicy.DecideNextType([], isDefaultVirtual: false, 30);
        Assert.Equal(SnapshotType.Full, type);
    }

    [Fact]
    public void 没有快照且配置了默认虚拟全量时跑虚拟全量()
    {
        SnapshotType type = SnapshotChainPolicy.DecideNextType([], isDefaultVirtual: true, 30);
        Assert.Equal(SnapshotType.VirtualFull, type);
    }

    [Fact]
    public void 全量之后跑增量()
    {
        BackupSnapshot[] snapshots = [Snap(1, SnapshotType.Full, 0)];
        Assert.Equal(SnapshotType.Increment, SnapshotChainPolicy.DecideNextType(snapshots, false, 30));
    }

    [Fact]
    public void 增量数超过上限时改跑全量()
    {
        // n > Max 才触发（文档的伪代码是严格的 >）
        List<BackupSnapshot> snapshots = [Snap(1, SnapshotType.Full, 0)];
        for (int i = 0; i < 30; i++)
        {
            snapshots.Add(Snap(2 + i, SnapshotType.Increment, 1 + i));
        }

        Assert.Equal(30, snapshots.Count - 1);
        Assert.Equal(SnapshotType.Increment, SnapshotChainPolicy.DecideNextType(snapshots, false, 30));

        snapshots.Add(Snap(100, SnapshotType.Increment, 40));
        Assert.Equal(SnapshotType.Full, SnapshotChainPolicy.DecideNextType(snapshots, false, 30));
    }

    [Fact]
    public void 虚拟全量同样算作链首全量()
    {
        BackupSnapshot[] snapshots = [Snap(1, SnapshotType.VirtualFull, 0)];
        Assert.Equal(SnapshotType.Increment, SnapshotChainPolicy.DecideNextType(snapshots, false, 30));
    }

    [Fact]
    public void 超过上限时改用虚拟全量若任务如此配置()
    {
        List<BackupSnapshot> snapshots = [Snap(1, SnapshotType.Full, 0)];
        for (int i = 0; i < 31; i++)
        {
            snapshots.Add(Snap(2 + i, SnapshotType.Increment, 1 + i));
        }

        Assert.Equal(SnapshotType.VirtualFull, SnapshotChainPolicy.DecideNextType(snapshots, true, 30));
    }

    // ------------------------------------------------------------------
    // 镜像链解析（设计文档 03 §4）
    // ------------------------------------------------------------------

    [Fact]
    public void 链等于最近全量及其后所有增量()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Increment, 10),
            Snap(3, SnapshotType.Increment, 20),
            Snap(4, SnapshotType.Full, 30),
            Snap(5, SnapshotType.Increment, 40),
        ];

        List<BackupSnapshot> chain = SnapshotChainPolicy.ResolveChain(snapshots, snapshots[2])!;
        Assert.Equal([1, 2, 3], chain.Select(s => s.Id));

        // 快照 5 的链是新的那个全量起头，不能跨过 4
        List<BackupSnapshot> chain2 = SnapshotChainPolicy.ResolveChain(snapshots, snapshots[4])!;
        Assert.Equal([4, 5], chain2.Select(s => s.Id));
    }

    [Fact]
    public void 全量快照的链就是它自己()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Increment, 10),
            Snap(3, SnapshotType.Full, 20),
        ];

        List<BackupSnapshot> chain = SnapshotChainPolicy.ResolveChain(snapshots, snapshots[2])!;
        Assert.Equal([3], chain.Select(s => s.Id));
    }

    [Fact]
    public void 首条就是增量时链断裂()
    {
        List<BackupSnapshot> snapshots = [Snap(1, SnapshotType.Increment, 0)];
        Assert.Null(SnapshotChainPolicy.ResolveChain(snapshots, snapshots[0]));
    }

    // ------------------------------------------------------------------
    // 级联删除（设计文档 04 §2.2）
    // ------------------------------------------------------------------

    [Fact]
    public void 删除中间增量时级联到下一个全量之前()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Increment, 10),
            Snap(3, SnapshotType.Increment, 20),
            Snap(4, SnapshotType.Full, 30),
            Snap(5, SnapshotType.Increment, 40),
        ];

        List<BackupSnapshot> affected = SnapshotChainPolicy.ComputeCascadeDelete(snapshots, snapshots[1]);
        Assert.Equal([2, 3], affected.Select(s => s.Id));
    }

    [Fact]
    public void 删除全量时级联到下一个全量之前且不含下一个全量()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Increment, 10),
            Snap(3, SnapshotType.Full, 20),
        ];

        List<BackupSnapshot> affected = SnapshotChainPolicy.ComputeCascadeDelete(snapshots, snapshots[0]);
        Assert.Equal([1, 2], affected.Select(s => s.Id));
    }

    [Fact]
    public void 删除最后一个全量时只影响它自己()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Full, 10),
        ];

        List<BackupSnapshot> affected = SnapshotChainPolicy.ComputeCascadeDelete(snapshots, snapshots[1]);
        Assert.Equal([2], affected.Select(s => s.Id));
    }

    [Fact]
    public void 删除最老的全量且后面没有全量时影响到链尾()
    {
        List<BackupSnapshot> snapshots =
        [
            Snap(1, SnapshotType.Full, 0),
            Snap(2, SnapshotType.Increment, 10),
            Snap(3, SnapshotType.Increment, 20),
        ];

        List<BackupSnapshot> affected = SnapshotChainPolicy.ComputeCascadeDelete(snapshots, snapshots[0]);
        Assert.Equal([1, 2, 3], affected.Select(s => s.Id));
    }

    // ------------------------------------------------------------------
    // 去重判据（设计文档 03 §3.3）
    // ------------------------------------------------------------------

    [Fact]
    public void 没有同长度记录时走边拷边算()
    {
        Assert.Equal(BlobWriteStrategy.CopyWhileHashing, DeduplicationPolicy.Decide(anyRecordWithSameLength: false));
    }

    [Fact]
    public void 存在同长度记录时先算哈希()
    {
        Assert.Equal(BlobWriteStrategy.HashFirstThenMaybeCopy, DeduplicationPolicy.Decide(anyRecordWithSameLength: true));
    }

    // ------------------------------------------------------------------
    // 变更分类（设计文档 03 §3.2）
    // ------------------------------------------------------------------

    [Fact]
    public void 镜像里没有该路径则算新增()
    {
        Assert.Equal(FileChangeKind.Created,
            FileChangeClassifier.Classify(false, 0, default, 100, new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void 长度与时间都一致则算未变()
    {
        DateTime t = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(FileChangeKind.Unchanged, FileChangeClassifier.Classify(true, 100, t, 100, t));
    }

    [Theory]
    [InlineData(101, 100, 100, 100)] // 长度变了
    [InlineData(100, 100, 100, 200)] // 时间变了
    public void 长度或时间任一变化则算修改(long ml, long dl, int mMin, int dMin)
    {
        DateTime mirrorTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(mMin);
        DateTime diskTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(dMin);
        Assert.Equal(FileChangeKind.Modified, FileChangeClassifier.Classify(true, ml, mirrorTime, dl, diskTime));
    }

    [Fact]
    public void 未变不产生记录()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FileChangeClassifier.ToRecordType(FileChangeKind.Unchanged));
        Assert.Equal(FileRecordType.Created, FileChangeClassifier.ToRecordType(FileChangeKind.Created));
        Assert.Equal(FileRecordType.Modified, FileChangeClassifier.ToRecordType(FileChangeKind.Modified));
    }

    // ------------------------------------------------------------------
    // 脏目录集合（设计文档 03 §5.4）
    // ------------------------------------------------------------------

    [Fact]
    public void 脏目录压实掉被祖先覆盖的项()
    {
        IPathNormalizer n = WindowsPathNormalizer.Instance;
        List<string> compacted = DirtyDirectorySet.Compact(
            [@"docs\2026", @"docs", @"photos", @"docs\2026\01"], n);

        Assert.Equal(2, compacted.Count);
        Assert.Contains(@"docs", compacted, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("photos", compacted, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void 脏目录压实去重且不区分大小写()
    {
        IPathNormalizer n = WindowsPathNormalizer.Instance;
        List<string> compacted = DirtyDirectorySet.Compact([@"Docs", @"docs\", @"DOCS"], n);
        Assert.Single(compacted);
    }

    [Fact]
    public void 脏目录占比超过阈值时退化为全量扫描()
    {
        // 30/100 = 30% 不超（文档说的是"超过"）
        Assert.False(DirtyDirectorySet.ShouldDegradeToFullScan(30, 100, 0.30));

        // 31/100 超过
        Assert.True(DirtyDirectorySet.ShouldDegradeToFullScan(31, 100, 0.30));
    }

    [Fact]
    public void 目录总数未知时保守按全量处理()
    {
        Assert.True(DirtyDirectorySet.ShouldDegradeToFullScan(1, 0, 0.30));
        Assert.False(DirtyDirectorySet.ShouldDegradeToFullScan(0, 100, 0.30));
    }
}
