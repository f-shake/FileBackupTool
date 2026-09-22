using FileBackupTool.Core.Entities;
using FileBackupTool.Data;
using FileBackupTool.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace FileBackupTool.Api.Services;

/// <summary>
/// 把数据库里的任务投影成监控器要的 <see cref="MonitoredRoot"/>（监控模块读取任务配置的唯一入口）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么由宿主实现而不是监控模块自己查库</b>：监控模块只引用 Core
/// （见 Monitoring 项目的 csproj），它不能依赖 EF/Data——它一行 SQL 都不需要。
/// 这层薄壳属于宿主，见 <see cref="IMonitoringTaskSource"/> 的类注释。
/// </para>
/// <para>
/// <b>刻意**不**按 <c>ByWatching</c> 过滤</b>：过滤掉未开监控的任务，监控器就永远无法对外
/// 表达"这个任务处于 <c>Disabled</c>"（它只能靠"查不到这个根"来推断，而"查不到"同时也意味着
/// "任务不存在"，两种完全不同的情况被压成一种）。所以把未开监控的任务也交给它，
/// 由它决定是不建 watcher 还是扫都不扫。
/// </para>
/// <para>
/// <b>必须带上 <c>BackupDir</c></b>：备份目录要排除在监控范围之外。虽然目录关系校验已经禁止
/// "备份目录位于源目录之内"，但那条校验只作用于**同一个实例自己的任务**——
/// 用户完全可以让 A 任务的源目录正好包含 B 任务的备份目录。不排除就是自激循环。
/// </para>
/// <para>
/// 过滤条件用 <c>!IsDeleted &amp;&amp; IsEnabled</c>：软删除的任务与已禁用的任务都不该再有监控行为。
/// 已禁用的任务在 <c>TasksController.SetEnabledAsync</c> 里同时被关掉了 <c>ByWatching</c>，
/// 两处一致。
/// </para>
/// </remarks>
public sealed class DbMonitoringTaskSource(
    IDbContextFactory<AppDbContext> factory,
    ILogger<DbMonitoringTaskSource> logger) : IMonitoringTaskSource
{
    private readonly IDbContextFactory<AppDbContext> _factory = factory;
    private readonly ILogger<DbMonitoringTaskSource> _logger = logger;

    public async Task<IReadOnlyList<MonitoredRoot>> GetMonitoredRootsAsync(CancellationToken ct)
    {
        await using AppDbContext db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);

        List<BackupTask> tasks = await db.Tasks
            .Where(t => !t.IsDeleted && t.IsEnabled)
            .OrderBy(t => t.Name)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        List<MonitoredRoot> roots =
        [
            .. tasks
                .Where(t => !string.IsNullOrWhiteSpace(t.SourceDir))
                .Select(t => new MonitoredRoot(
                    t.Id,
                    t.Name,
                    t.SourceDir,
                    WatchEnabled: t.ByWatching,
                    BackupDir: t.BackupDir)),
        ];

        _logger.LogDebug(
            "已向变更监控提供 {Total} 个根，其中 {Watched} 个启用了实时监控。",
            roots.Count, roots.Count(r => r.WatchEnabled));

        return roots;
    }
}
