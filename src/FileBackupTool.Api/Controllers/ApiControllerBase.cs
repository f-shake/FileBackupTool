using System.Security.Claims;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Api.Security;
using FileBackupTool.Core.Entities;
using FileBackupTool.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FileBackupTool.Api.Controllers;

/// <summary>
/// 全部业务控制器的基类：统一鉴权、统一 CSRF 校验、统一当前用户。
/// </summary>
/// <remarks>
/// <para>
/// <b>默认 <c>[Authorize]</c>，而不是每个动作各写一遍</b>：这是"默认安全"的写法——
/// 新增端点时忘记加鉴权会立刻得到 401（容易被发现），而不是悄悄变成匿名可访问
/// （很难被发现，而且往往是在被访问之后才知道）。需要匿名的端点必须**显式**标
/// <c>[AllowAnonymous]</c>，全仓库只有登录接口是那样。
/// </para>
/// <para>
/// <b>默认 <c>[RequireAntiforgery]</c></b>：写操作必须带防跨站令牌（设计文档 05 §8）。
/// 同样地，漏掉一个写端点会立刻在开发时暴露（前端不带令牌就 400），
/// 而不是留下一个"能被第三方站点构造的表单打穿"的入口。
/// </para>
/// </remarks>
[ApiController]
[Authorize]
[RequireAntiforgery]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    /// <summary>当前请求的取消令牌（客户端断开时触发）。</summary>
    protected CancellationToken Ct => HttpContext.RequestAborted;

    /// <summary>当前用户 id。未登录时抛异常——所有动作都在 <c>[Authorize]</c> 之后，不该发生。</summary>
    protected Guid CurrentUserId
    {
        get
        {
            string? raw = User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out Guid id)
                ? id
                : throw ApiException.Unauthorized(ApiErrorCodes.Unauthorized, "登录状态已失效，请重新登录。");
        }
    }

    protected string CurrentUserName => User.Identity?.Name ?? "(未知)";

    /// <summary>当前会话 id（用于"哪个会话是我"与踢出之后的自处理）。</summary>
    protected string? CurrentSessionId => User.FindFirst(SecurityClaims.SessionId)?.Value;

    /// <summary>取任务；不存在（含已软删除）时抛 404。</summary>
    protected async Task<BackupTask> RequireTaskAsync(TaskStore tasks, Guid id)
    {
        BackupTask? task = await tasks.GetAsync(id, Ct).ConfigureAwait(false);

        if (task is null || task.IsDeleted)
        {
            // 已软删除的任务对外等同不存在：它还在库里只是为了保留快照，
            // 让 API 继续按 id 访问它会暴露"曾经有过这个任务"以及它的路径配置。
            throw ApiException.NotFound(ApiErrorCodes.TaskNotFound, $"任务 {id} 不存在。");
        }

        return task;
    }

    /// <summary>取快照；不存在（含已软删除）时抛 404。</summary>
    /// <remarks>
    /// 已软删除的快照对外等同不存在：它的文件记录还在库里只是为了给 GC 判定引用，
    /// 让 API 继续按 id 访问它会暴露"曾经有过这个还原点"以及它的全部路径。
    /// </remarks>
    protected async Task<BackupSnapshot> RequireSnapshotAsync(SnapshotChainService chains, int snapshotId)
    {
        BackupSnapshot? snapshot = await chains.GetAsync(snapshotId, Ct).ConfigureAwait(false);

        if (snapshot is null || snapshot.IsDeleted)
        {
            throw ApiException.NotFound(ApiErrorCodes.SnapshotNotFound, $"快照 {snapshotId} 不存在。");
        }

        return snapshot;
    }

    /// <summary>取当前登录用户实体（改密码等认证流程要用）。</summary>
    protected async Task<IdentityUser<Guid>> RequireUserAsync(UserManager<IdentityUser<Guid>> users)
    {
        IdentityUser<Guid>? user = await users.GetUserAsync(User).ConfigureAwait(false);

        if (user is null)
        {
            throw ApiException.Unauthorized(ApiErrorCodes.Unauthorized, "登录状态已失效，请重新登录。");
        }

        return user;
    }
}
