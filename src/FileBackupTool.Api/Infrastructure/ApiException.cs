using System.Net;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 带稳定 <c>code</c> 与 HTTP 状态码的业务异常。统一异常中间件把它映射成
/// <c>{ code, message, detail? }</c>（设计文档 06 §4）。
/// </summary>
/// <remarks>
/// 用异常而不是"到处 return BadRequest(...)"，是为了让控制器里的业务逻辑保持线性可读。
/// 由于**只有**中间件负责把异常转成 HTTP 响应，业务代码不需要知道状态码。
/// </remarks>
public class ApiException(string code, string message, HttpStatusCode status = HttpStatusCode.BadRequest, string? detail = null)
    : Exception(message)
{
    public string Code { get; } = code;

    public HttpStatusCode Status { get; } = status;

    public string? Detail { get; } = detail;

    public static ApiException NotFound(string code, string message, string? detail = null) =>
        new(code, message, HttpStatusCode.NotFound, detail);

    public static ApiException Conflict(string code, string message, string? detail = null) =>
        new(code, message, HttpStatusCode.Conflict, detail);

    public static ApiException Forbidden(string code, string message, string? detail = null) =>
        new(code, message, HttpStatusCode.Forbidden, detail);

    public static ApiException Unauthorized(string code, string message, string? detail = null) =>
        new(code, message, HttpStatusCode.Unauthorized, detail);

    public static ApiException BadRequest(string code, string message, string? detail = null) =>
        new(code, message, HttpStatusCode.BadRequest, detail);
}
