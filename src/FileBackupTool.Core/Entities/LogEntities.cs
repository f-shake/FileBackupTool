using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Core.Entities;

/// <summary>业务日志（按任务/快照关联）。设计文档 02 §2.4。同时输出到 Serilog 文件 sink。</summary>
public class BackupLog
{
    public long Id { get; set; }

    public Guid TaskId { get; set; }

    public int? SnapshotId { get; set; }

    public LogLevel Level { get; set; }

    /// <summary>UTC。</summary>
    public DateTime Time { get; set; }

    [MaxLength(4000)]
    public string Message { get; set; } = string.Empty;

    public string? Detail { get; set; }
}

/// <summary>审计日志。设计文档 02 §2.5 / 05 §5。**不参与快照删除的级联，且不自动清理**。</summary>
public class AuditLog
{
    public long Id { get; set; }

    /// <summary>UTC。</summary>
    public DateTime Time { get; set; }

    public Enums.AuditEventType EventType { get; set; }

    /// <summary>操作者（单用户系统里就是用户名；系统自动行为记 "system"）。</summary>
    [MaxLength(256)]
    public string? Actor { get; set; }

    [MaxLength(64)]
    public string? SourceIp { get; set; }

    [MaxLength(512)]
    public string? UserAgent { get; set; }

    [MaxLength(512)]
    public string? Target { get; set; }

    public string? Detail { get; set; }
}

/// <summary>全局设置的键值存储（非基础设施部分：还原根白名单、保留策略等）。设计文档 06 §4.9。</summary>
public class AppSetting
{
    [MaxLength(200)]
    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}
