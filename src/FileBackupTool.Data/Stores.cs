using System.Text.Json;
using FileBackupTool.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Data;

/// <summary>任务仓储。任务定义存数据库（设计文档 01 §5.2）。</summary>
public sealed class TaskStore(IDbContextFactory<AppDbContext> factory, Core.Abstractions.IClock clock)
{
    private readonly IDbContextFactory<AppDbContext> _factory = factory;
    private readonly Core.Abstractions.IClock _clock = clock;

    public async Task<List<BackupTask>> GetAllAsync(bool includeDeleted, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tasks
            .Where(t => includeDeleted || !t.IsDeleted)
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<BackupTask?> GetAsync(Guid id, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
    }

    /// <summary>需要参与目录关系校验的其余任务（排除自身、含已软删除的——它们占用的目录还没释放）。</summary>
    public async Task<List<BackupTask>> GetForValidationAsync(Guid? excludeId, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Tasks
            .Where(t => excludeId == null || t.Id != excludeId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task AddAsync(BackupTask task, CancellationToken ct)
    {
        task.CreatedAt = _clock.UtcNow;
        task.UpdatedAt = task.CreatedAt;

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Tasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task UpdateAsync(BackupTask task, CancellationToken ct)
    {
        task.UpdatedAt = _clock.UtcNow;

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Tasks.Update(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 软删除任务。设计文档 08 §4.2：**快照保留；blob 交由 GC；不物理删除备份目录**。
    /// </summary>
    public async Task SoftDeleteAsync(Guid id, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        BackupTask? task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        if (task is null)
        {
            return;
        }

        task.IsDeleted = true;
        task.IsEnabled = false;
        task.ByWatching = false;
        task.UpdatedAt = _clock.UtcNow;
        db.Tasks.Update(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<(int SnapshotCount, DateTime? LastBackupUtc)> GetStatsAsync(Guid id, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var query = db.Snapshots.Where(s => s.TaskId == id && !s.IsDeleted);
        int count = await query.CountAsync(ct).ConfigureAwait(false);
        DateTime? last = await query
            .Where(s => s.Status == Core.Enums.SnapshotStatus.Completed)
            .OrderByDescending(s => s.BeginTime)
            .Select(s => (DateTime?)s.BeginTime)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return (count, last);
    }

    /// <summary>上次备份时间（用于定时调度）。以**数据库中的快照状态**为准，而不是进程内字段。</summary>
    public async Task<Dictionary<Guid, DateTime>> GetLastBackupTimesAsync(CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Snapshots
            .Where(s => !s.IsDeleted && s.Status == Core.Enums.SnapshotStatus.Completed)
            .GroupBy(s => s.TaskId)
            .Select(g => new { TaskId = g.Key, Last = g.Max(s => s.BeginTime) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.ToDictionary(r => r.TaskId, r => r.Last);
    }
}

/// <summary>业务日志 + 审计日志。</summary>
public sealed class LogStore(IDbContextFactory<AppDbContext> factory)
{
    private readonly IDbContextFactory<AppDbContext> _factory = factory;

    public async Task WriteAsync(BackupLog log, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Logs.Add(log);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteManyAsync(IReadOnlyList<BackupLog> logs, CancellationToken ct)
    {
        if (logs.Count == 0)
        {
            return;
        }

        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Logs.AddRangeAsync(logs, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteAuditAsync(AuditLog audit, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.AuditLogs.Add(audit);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>
/// 全局设置。设计文档 06 §4.9：基础设施配置留在 appsettings.json，
/// 这些东西（还原根白名单、保留策略）需要能即时改，所以放数据库。
/// </summary>
public sealed class SettingsStore(IDbContextFactory<AppDbContext> factory, Core.Abstractions.IClock clock)
{
    public const string KeyAllowedRestoreRoots = "Security.AllowedRestoreRoots";
    public const string KeyRetentionAutoEviction = "Retention.AutoEvictionEnabled";
    public const string KeyRetentionMaxGroups = "Retention.MaxSnapshotGroups";
    public const string KeyRetentionMaxAgeDays = "Retention.MaxAgeDays";
    public const string KeyRetentionMaxBytesGb = "Retention.MaxTotalBytesGb";

    private readonly IDbContextFactory<AppDbContext> _factory = factory;
    private readonly Core.Abstractions.IClock _clock = clock;

    public async Task<List<string>> GetAllowedRestoreRootsAsync(CancellationToken ct)
    {
        string? json = await GetStringAsync(KeyAllowedRestoreRoots, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async Task SetAllowedRestoreRootsAsync(IReadOnlyList<string> roots, CancellationToken ct)
        => await SetStringAsync(KeyAllowedRestoreRoots, JsonSerializer.Serialize(roots), ct).ConfigureAwait(false);

    public async Task<string?> GetStringAsync(string key, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Settings
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task SetStringAsync(string key, string value, CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        AppSetting? existing = await db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct).ConfigureAwait(false);
        if (existing is null)
        {
            db.Settings.Add(new AppSetting { Key = key, Value = value, UpdatedAt = _clock.UtcNow });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = _clock.UtcNow;
            db.Settings.Update(existing);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>> GetAllAsync(CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value, ct).ConfigureAwait(false);
    }
}
