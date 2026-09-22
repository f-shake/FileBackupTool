using FileBackupTool.Core.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.Data;

/// <summary>
/// 集中式 SQLite 上下文。设计文档 02 §1。
/// </summary>
/// <remarks>
/// 与旧设计（每任务一个 <c>db.sqlite</c> + <c>EnsureCreated()</c>）的差别：
/// 单一迁移点、能建全局索引、能跨任务查询、无需为每个任务维护连接。
/// 代价是备份目录不再自包含——由"抢救清单"补偿。
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<IdentityUser<Guid>, IdentityRole<Guid>, Guid>(options)
{
    public DbSet<BackupTask> Tasks => Set<BackupTask>();

    public DbSet<BackupSnapshot> Snapshots => Set<BackupSnapshot>();

    public DbSet<BackupFile> Files => Set<BackupFile>();

    public DbSet<BackupLog> Logs => Set<BackupLog>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<AppSetting> Settings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // SQLite 上的外键默认行为是 Cascade——这里全部改成 Restrict。
        // 这个库里 NEVER 硬删除业务记录（决策 #12：一律软删除），
        // 设置成 Restrict 是为了让"某处误写了 Remove(task)"直接失败，
        // 而不是静默 cascade 掉一百万个 BackupFile 行。
        b.Entity<BackupSnapshot>()
            .HasOne(s => s.Task)
            .WithMany(t => t.Snapshots)
            .HasForeignKey(s => s.TaskId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<BackupFile>()
            .HasOne(f => f.Snapshot)
            .WithMany(s => s.Files)
            .HasForeignKey(f => f.SnapshotId)
            .OnDelete(DeleteBehavior.Restrict);

        b.Entity<BackupTask>().HasIndex(t => t.BackupDir);
        b.Entity<BackupTask>().HasIndex(t => t.IsDeleted);

        // 索引是这个量级（十万～百万文件）下**性能的生死线**。旧设计一个索引都没有。
        b.Entity<BackupSnapshot>().HasIndex(s => new { s.TaskId, s.Status, s.BeginTime });

        // 路径前缀查询：镜像浏览、文件历史。
        // SnapshotId 刻意建成 **DESC**：物化"上一个镜像"时按
        // (RelativePathKey ASC, SnapshotId DESC) 扫描，同一路径的记录连续且最新的排在最前，
        // 索引方向一致才不会让 SQLite 为百万行做一次外部排序。
        b.Entity<BackupFile>()
            .HasIndex(f => new { f.TaskId, f.RelativePathKey, f.SnapshotId })
            .IsDescending(false, false, true);

        // 按快照取记录（镜像重建时按 SnapshotId IN (…) 取一批）
        b.Entity<BackupFile>().HasIndex(f => f.SnapshotId);

        // blob 引用计数（GC）
        b.Entity<BackupFile>().HasIndex(f => new { f.TaskId, f.ContentHash });

        // Length 预筛（设计文档 03 §3.3）
        b.Entity<BackupFile>().HasIndex(f => new { f.TaskId, f.Length });

        b.Entity<BackupLog>().HasIndex(l => new { l.TaskId, l.Time });
        b.Entity<BackupLog>().HasIndex(l => l.SnapshotId);

        b.Entity<AuditLog>().HasIndex(a => a.Time);

        b.Entity<AppSetting>().HasKey(s => s.Key);
    }
}
