using System.Net;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Paths;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 全局异常 → <c>{ code, message, detail? }</c> 的统一映射。
/// </summary>
/// <remarks>
/// <para>
/// 设计文档 05 §8：**生产环境错误页不泄露堆栈**。
/// 因此只有 <c>IsDevelopment()</c> 才把 <c>exception.ToString()</c> 放进 <c>detail</c>，
/// 生产环境里 <c>detail</c> 只承载"可安全外泄"的补充信息（例如路径白名单被拒的具体原因），
/// 而异常堆栈只进 Serilog 文件。
/// </para>
/// <para>
/// 顺序很重要：先处理"有明确业务语义"的异常，最后才是兜底的 500。
/// </para>
/// </remarks>
public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    IHostEnvironment environment,
    ILogger<ApiExceptionMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly IHostEnvironment _environment = environment;
    private readonly ILogger<ApiExceptionMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端自己断开了（浏览器关页面、SignalR 降级、用户取消）。
            // 这不是服务端错误，不该记 Error，也不该尝试写响应体。
            _logger.LogDebug("请求被客户端取消：{Path}", context.Request.Path);
        }
        catch (ApiException ex)
        {
            await ApiErrorWriter.WriteAsync(
                context, (int)ex.Status, ex.Code, ex.Message, ex.Detail, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            // 引擎在"还原目标不在白名单内"时抛这个（RestoreService §1.3 的最后一道校验）。
            // 它是一个**安全事件**，用独立的稳定 code，前端据此给出针对性提示。
            _logger.LogWarning(ex, "访问被拒绝：{Path}", context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context, (int)HttpStatusCode.Forbidden,
                ApiErrorCodes.RestoreTargetNotAllowed, "操作被拒绝。",
                _environment.IsDevelopment() ? ex.Message : "目标路径不在白名单还原根目录之内。",
                context.RequestAborted).ConfigureAwait(false);
        }
        catch (KeyNotFoundException ex)
        {
            await ApiErrorWriter.WriteAsync(
                context, (int)HttpStatusCode.NotFound, ApiErrorCodes.NotFound, ex.Message,
                null, context.RequestAborted).ConfigureAwait(false);
        }
        catch (DirectoryValidationFailedException ex)
        {
            await ApiErrorWriter.WriteAsync(
                context, (int)HttpStatusCode.BadRequest, ApiErrorCodes.TaskDirConflict,
                ex.Message, ex.DetailJson, context.RequestAborted).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await ApiErrorWriter.WriteAsync(
                context, (int)HttpStatusCode.BadRequest, ApiErrorCodes.InvalidRequest, ex.Message,
                null, context.RequestAborted).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // 引擎用 InvalidOperationException 表达"前置条件不满足"（如快照不属于该任务）。
            // 一律 409 比一律 500 更贴近事实，也不会让前端误以为服务端崩了。
            _logger.LogWarning(ex, "业务前置条件不满足：{Path}", context.Request.Path);
            await ApiErrorWriter.WriteAsync(
                context, (int)HttpStatusCode.Conflict, ApiErrorCodes.Conflict, ex.Message,
                null, context.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "未处理异常：{Method} {Path}", context.Request.Method, context.Request.Path);

            await ApiErrorWriter.WriteAsync(
                context,
                (int)HttpStatusCode.InternalServerError,
                ApiErrorCodes.ServerError,
                "服务端发生未预期的错误，详情见服务器日志。",
                // 生产环境绝不外泄堆栈（05 §8）；开发环境给全量便于定位。
                _environment.IsDevelopment() ? ex.ToString() : null,
                context.RequestAborted).ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 目录关系校验失败。带上结构化的冲突清单，前端可直接高亮是哪两个路径打架。
/// </summary>
public sealed class DirectoryValidationFailedException(string message, string detailJson)
    : Exception(message)
{
    public string DetailJson { get; } = detailJson;

    public static DirectoryValidationFailedException From(IReadOnlyList<Core.Models.DirectoryValidationIssue> issues)
    {
        // code 取第一条的稳定码；清单放在 detail 里（前端契约的 detail 是字符串）。
        string message = issues.Count == 1
            ? issues[0].Message
            : $"目录关系校验未通过（{issues.Count} 项冲突）：" +
              string.Join("；", issues.Select(i => i.Message));

        string detail = System.Text.Json.JsonSerializer.Serialize(
            issues.Select(i => new DirValidationIssueDto(
                i.Code, i.Message, i.PathA, i.PathB, ValidationIssueMapper.KindOf(i.Code))),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        return new DirectoryValidationFailedException(message, detail);
    }
}
