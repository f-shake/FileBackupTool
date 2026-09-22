using FileBackupTool.Api.Contracts;
using Microsoft.AspNetCore.Antiforgery;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 写操作上的 antiforgery 强制校验。设计文档 05 §3：**全部写操作启用 antiforgery**。
/// </summary>
/// <remarks>
/// <para>
/// <b>令牌只有一个签发点</b>：<c>GET /api/auth/csrf</c>（<c>AuthController.Csrf</c>），
/// 它在认证之后执行。**不要**再在任何中间件里"顺手签发"——
/// ASP.NET Core 的 antiforgery 是配对令牌（cookie 一份、请求头一份），
/// 且请求令牌与**签发时的用户身份**绑定；出现第二个签发点就会让 cookie 与请求令牌
/// 来自不同身份，校验必然失败，症状是"登录之后所有写操作 400、读操作全正常"。
/// 详见 <c>docs/plans/09-实现期修订.md</c>。
/// </para>
/// <para>
/// <b>为什么用 MVC 的 action filter，而不是 <c>[AutoValidateAntiforgeryToken]</c></b>：
/// 后者的校验器是由 MVC 的 <c>AntiforgeryApplicationModelProvider</c> 注入的，
/// 而那个 provider 只在 <c>AddControllersWithViews()</c> / <c>AddMvc()</c> 这类
/// 带视图的注册里才会被装上。本宿主用的是 <c>AddControllers()</c>（纯 API，没有视图），
/// 一旦 provider 缺席，那个特性会**静默失效**——写操作看起来有防护，实际一点都没校验。
/// 这种"配了但没生效"的安全措施比没配还危险，所以自己实现，行为完全确定。
/// </para>
/// <para>
/// <b>为什么放在 action filter 而不是更早的中间件</b>：授权过滤器（<c>[Authorize]</c>）
/// 先跑，未登录的请求得到标准的 401；只有已认证的写请求才会走到这里被 CSRF 校验。
/// 反过来的话，未登录请求会得到 400（缺令牌），前端会把它当成"参数错误"而不是"请重新登录"。
/// </para>
/// <para>
/// 安全方法（GET/HEAD/OPTIONS/TRACE）不校验——它们按 HTTP 语义不得有副作用。
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireAntiforgeryAttribute : Attribute, Microsoft.AspNetCore.Mvc.Filters.IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutingContext context,
        Microsoft.AspNetCore.Mvc.Filters.ActionExecutionDelegate next)
    {
        string method = context.HttpContext.Request.Method;

        if (!IsSafeMethod(method))
        {
            IAntiforgery antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();

            if (!await antiforgery.IsRequestValidAsync(context.HttpContext).ConfigureAwait(false))
            {
                // 走统一异常通道，保证错误体形状与其它错误一致（{ code, message, detail? }）。
                throw ApiException.BadRequest(
                    ApiErrorCodes.AntiforgeryFailed,
                    "请求缺少或携带了无效的防跨站令牌，已被拒绝。请刷新页面后重试。",
                    "写操作的 X-XSRF-TOKEN 头必须取自 GET /api/auth/csrf 返回的 token "
                    + "（**不是** XSRF-TOKEN cookie 的值——cookie 里是配对的另一半）。");
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
        HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
