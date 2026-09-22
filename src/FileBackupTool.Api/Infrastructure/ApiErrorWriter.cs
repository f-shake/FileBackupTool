using System.Text.Json;
using FileBackupTool.Api.Contracts;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 把错误体写成统一形状 <c>{ code, message, detail? }</c>。
/// </summary>
/// <remarks>
/// 单独抽出来是因为有**四条**互不相干的路径要产生错误体：
/// 异常中间件、认证挑战/拒绝回调、限流拒绝回调、未匹配路由的兜底。
/// 这四处各写一遍 JSON 一定会漂移。
/// </remarks>
public static class ApiErrorWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task WriteAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message,
        string? detail = null,
        CancellationToken ct = default)
    {
        if (context.Response.HasStarted)
        {
            // 响应已经开始写出，再改状态码只会抛异常。让连接自然结束。
            return;
        }

        // Response.Clear() 会把**全部**响应头一起清掉，包括 Set-Cookie。
        // 而本 API 有一条必须存活的响应头：AntiforgeryTokenMiddleware 在安全方法上下发的
        // antiforgery 令牌（XSRF-TOKEN）。丢掉它 → 客户端永远拿不到令牌 →
        // 之后**所有**写操作（包括登录）都会 400，表现为"界面能用但什么都存不了"。
        // 所以在清之前把它取出来，清完再放回去。
        string[] setCookie = [.. context.Response.Headers.SetCookie
            .Where(v => v is not null)
            .Select(v => v!)];

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        if (setCookie.Length > 0)
        {
            context.Response.Headers.SetCookie = setCookie;
        }

        await context.Response
            .WriteAsync(JsonSerializer.Serialize(new ApiErrorBody(code, message, detail), Options), ct)
            .ConfigureAwait(false);
    }
}
