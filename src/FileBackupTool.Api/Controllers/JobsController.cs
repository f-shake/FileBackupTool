using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Mvc;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 全局作业队列（设计文档 06 §4.5）。
/// </summary>
/// <remarks>
/// 队列**不跨进程重启恢复**（01 §4.1），所以这里看到的就是本进程的作业。
/// 前端靠它显示"正在跑什么、还排着几个"，并作为 SignalR 掉线时的轮询兜底。
/// </remarks>
[Route("api/jobs")]
public sealed class JobsController(
    IJobQueue queue,
    TaskStore tasks) : ApiControllerBase
{
    private readonly IJobQueue _queue = queue;
    private readonly TaskStore _tasks = tasks;

    /// <summary>队列与运行中作业。</summary>
    [HttpGet]
    public Task<JobQueueSnapshotDto> ListAsync() =>
        JobQueueQuery.BuildSnapshotAsync(_queue, _tasks, Ct);

    /// <summary>作业详情与进度快照。</summary>
    [HttpGet("{jobId}")]
    public async Task<QueuedJobDto> GetAsync([FromRoute] string jobId)
    {
        JobView? view = await _queue.GetAsync(jobId, Ct).ConfigureAwait(false);

        if (view is null)
        {
            throw ApiException.NotFound(
                ApiErrorCodes.JobNotFound,
                $"作业 {jobId} 不存在。作业队列不跨进程重启恢复，重启前的作业记录已全部消失。");
        }

        Dictionary<Guid, string> names =
            await JobQueueQuery.GetTaskNamesAsync(_tasks, [view], Ct).ConfigureAwait(false);

        return JobViewMapper.Map(view, JobQueueQuery.NameOf(names, view));
    }
}
