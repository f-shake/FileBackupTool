using System.ComponentModel.DataAnnotations;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Entities;

/// <summary>
/// 备份任务定义。设计文档 02 §2.1。业务数据存数据库（而非 configs.json），
/// 以便在 UI 中即时增删改、与快照记录建立外键关联、并留下变更审计。
/// </summary>
public class BackupTask
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>需要备份的目录（绝对路径）。</summary>
    [MaxLength(1024)]
    public string SourceDir { get; set; } = string.Empty;

    /// <summary>备份文件存放目录（绝对路径）。**全局唯一，禁止共享**（设计文档 02 §7）。</summary>
    [MaxLength(1024)]
    public string BackupDir { get; set; } = string.Empty;

    /// <summary>序列化的筛选规则，见 <see cref="Filtering.FilterRuleSet"/>。</summary>
    public string FilterJson { get; set; } = "{}";

    /// <summary>是否启用定时备份。</summary>
    public bool ByTimeInterval { get; set; }

    /// <summary>备份间隔（**分钟**，非"时刻"）。旧实现把它做成了 TimePicker，语义是错的。</summary>
    public int TimeIntervalMinutes { get; set; } = 1440;

    /// <summary>增量链长度上限，超过则跑全量。</summary>
    public int MaxAutoIncrementBackupCount { get; set; } = 30;

    /// <summary>自动全量时使用虚拟全量（不写 blob，**不可恢复**）。</summary>
    public bool IsDefaultVirtualBackup { get; set; }

    /// <summary>是否启用变更监控（实时备份）。旧设计里这是**死配置**，全仓库没有 FileSystemWatcher。</summary>
    public bool ByWatching { get; set; }

    /// <summary>任务级启用开关。旧设计缺失。</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>软删除。设计文档决策 #12：所有记录删除一律软删除。</summary>
    public bool IsDeleted { get; set; }

    // ---- 导航 ----

    public List<BackupSnapshot> Snapshots { get; set; } = [];

    /// <summary>该任务是否启用了筛选规则。</summary>
    public bool HasFilter => !string.IsNullOrWhiteSpace(FilterJson) && FilterJson != "{}";

    public SnapshotType DefaultFullType => IsDefaultVirtualBackup ? SnapshotType.VirtualFull : SnapshotType.Full;
}
