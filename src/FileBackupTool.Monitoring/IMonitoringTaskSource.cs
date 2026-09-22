using FileBackupTool.Core.Monitoring;

namespace FileBackupTool.Monitoring;

/// <summary>
/// 一个需要被监控的源根（对应一个备份任务的源目录）。
/// </summary>
/// <param name="TaskId">任务 id。</param>
/// <param name="TaskName">任务名（只用于日志与状态展示）。</param>
/// <param name="SourceDir">源目录绝对路径。</param>
/// <param name="WatchEnabled">任务是否启用了实时监控（<c>BackupTask.ByWatching</c>）。</param>
/// <param name="BackupDir">
/// 该任务的备份目录。必须排除在监控范围之外——它就在源目录里时，备份自己写出的 blob
/// 会变成源源不断的变更事件（自激）。允许为 null（拿不到时只排除数据目录）。
/// </param>
public sealed record MonitoredRoot(
    Guid TaskId,
    string TaskName,
    string SourceDir,
    bool WatchEnabled,
    string? BackupDir = null);

/// <summary>
/// 监控器**读取任务配置**的唯一入口。
/// </summary>
/// <remarks>
/// 为什么要有这层：任务定义在数据库里（设计文档 01 §5.2），而本模块只引用
/// <c>FileBackupTool.Core</c>（见 Monitoring.csproj）——它**不能**直接依赖 <c>Data</c>。
/// 若让监控器自己开 <c>DbContext</c>，监控模块就被绑死在 EF 上了，而它其实一行 SQL 都不需要。
/// 因此由宿主（Api 层）用一个薄实现把 <c>BackupTask</c> 投影成 <see cref="MonitoredRoot"/>：
/// <code>
/// sealed class DbMonitoringTaskSource(IDbContextFactory&lt;AppDbContext&gt; factory) : IMonitoringTaskSource
/// {
///     public async Task&lt;IReadOnlyList&lt;MonitoredRoot&gt;&gt; GetMonitoredRootsAsync(CancellationToken ct)
///     {
///         await using AppDbContext db = await factory.CreateDbContextAsync(ct);
///         return await db.Tasks
///             .Where(t =&gt; !t.IsDeleted &amp;&amp; t.IsEnabled &amp;&amp; t.ByWatching)
///             .Select(t =&gt; new MonitoredRoot(t.Id, t.Name, t.SourceDir, true, t.BackupDir))
///             .ToListAsync(ct);
///     }
/// }
/// </code>
/// <para>
/// <b>是否要把未启用监控的任务也返回？</b>要——<see cref="RootMonitorMode.Disabled"/> 是给
/// UI 看的状态，任务列表里"这个任务不监控"本身就是信息。若不返回，UI 只能自己再查一遍任务表
/// 才能把两种来源拼起来。
/// </para>
/// </remarks>
public interface IMonitoringTaskSource
{
    /// <summary>取出当前所有（未删除的）任务。任务增删改后由宿主调用 <c>IChangeMonitor.ReloadAsync</c>。</summary>
    Task<IReadOnlyList<MonitoredRoot>> GetMonitoredRootsAsync(CancellationToken ct);
}
