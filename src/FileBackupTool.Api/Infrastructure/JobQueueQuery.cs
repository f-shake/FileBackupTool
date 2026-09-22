using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;
using FileBackupTool.Data;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>对 <see cref="IJobQueue"/> 的只读查询辅助。</summary>
/// <remarks>
/// <c>IJobQueue</c> 只提供 <c>ListAsync</c>（引擎的接口契约），没有"某任务是否有在跑的作业"。
/// 调度器与监控触发都需要这个判断——**没有它就会出现同一任务被重复入队**：
/// 60 秒的调度节拍与监控的合并窗口完全可能在同一时刻各自认为"该备份了"。
/// 所以在此集中一次，避免两处各写一遍（且写法不一致）。
/// </remarks>
public static class JobQueueQuery
{
    /// <summary>该任务是否已有排队或运行中的作业。</summary>
    public static async Task<bool> HasActiveJobForTaskAsync(
        IJobQueue queue,
        Guid taskId,
        CancellationToken ct)
    {
        IReadOnlyList<JobView> jobs = await queue.ListAsync(ct).ConfigureAwait(false);

        return jobs.Any(j =>
            j.TaskId == taskId && j.Status is JobStatus.Queued or JobStatus.Running);
    }

    /// <summary>
    /// 队列快照（运行中 + 排队中），供 <c>GET /api/jobs</c> 与 <c>GET /api/monitoring</c> 共用。
    /// </summary>
    /// <remarks>
    /// 两处必须给出**完全一致**的队列视图：前端在监控页和作业页显示的是同一件事，
    /// 两套实现迟早会出现"这里说在跑、那里说排队"的分歧。所以只有这一个实现。
    /// </remarks>
    public static async Task<JobQueueSnapshotDto> BuildSnapshotAsync(
        IJobQueue queue,
        TaskStore tasks,
        CancellationToken ct)
    {
        IReadOnlyList<JobView> jobs = await queue.ListAsync(ct).ConfigureAwait(false);
        Dictionary<Guid, string> names = await GetTaskNamesAsync(tasks, jobs, ct).ConfigureAwait(false);

        JobView? running = jobs.FirstOrDefault(j => j.Status == JobStatus.Running);

        return new JobQueueSnapshotDto(
            running is null ? null : JobViewMapper.Map(running, NameOf(names, running)),
            [
                .. jobs
                    .Where(j => j.Status == JobStatus.Queued)

                    // 按排队位置升序：Position 为 null 的（不该出现）排在最后。
                    .OrderBy(j => j.QueuePosition ?? int.MaxValue)
                    .Select(j => JobViewMapper.Map(j, NameOf(names, j))),
            ]);
    }

    /// <summary>批量取任务名（避免每个作业查一次库）。已软删除的任务仍然返回它的名字。</summary>
    public static async Task<Dictionary<Guid, string>> GetTaskNamesAsync(
        TaskStore tasks,
        IReadOnlyList<JobView> jobs,
        CancellationToken ct)
    {
        Guid[] ids = [.. jobs.Where(j => j.TaskId.HasValue).Select(j => j.TaskId!.Value).Distinct()];

        if (ids.Length == 0)
        {
            return [];
        }

        // 含已软删除的任务：作业的 TaskId 可能指向一个刚被删掉的任务，
        // 此时显示"(已删除)"比显示空白更有信息量。
        List<BackupTask> all = await tasks.GetAllAsync(includeDeleted: true, ct).ConfigureAwait(false);

        return all
            .Where(t => ids.Contains(t.Id))
            .ToDictionary(t => t.Id, t => t.IsDeleted ? $"{t.Name}（已删除）" : t.Name);
    }

    /// <summary>作业所属任务名（取不到则 null）。</summary>
    public static string? NameOf(Dictionary<Guid, string> names, JobView view) =>
        view.TaskId is { } id && names.TryGetValue(id, out string? name) ? name : null;
}
