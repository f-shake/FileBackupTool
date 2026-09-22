using FileBackupTool.Api.Contracts;
using Microsoft.AspNetCore.Antiforgery;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 在**安全方法**（GET/HEAD/OPTIONS）上主动下发 antiforgery 令牌。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不能只依赖框架自动下发</b>：双提交 Cookie 模式要求浏览器先拿到 Cookie
/// （<c>XSRF-TOKEN</c>），之后的写请求才能把它回填到 <c>X-XSRF-TOKEN</c> 头上。
/// 而框架只在"该端点需要校验"时才顺带下发令牌——对本 API 来说需要校验的都是写方法，
/// 于是**第一个**写请求必然没有可用令牌（鸡生蛋问题），前端会看到 400，
/// 而用户看到的是"保存不了，刷新一下又好了"这种最难受的间歇性故障。
/// 所以由宿主显式下发：只要方法安全，就把令牌摆好。
/// </para>
/// <para>
/// <b>它不下发任何秘密</b>：令牌本身由 Data Protection 密钥环派生，
/// 这里的 Cookie 只是"同一浏览器的第二个请求能证明它读过第一个响应"，
/// 攻击者站点**跨站读不到它**（同源策略 + <c>SameSite=Strict</c>）。
/// 这正是 CSRF 防护的全部原理，见设计文档 05 §8。
/// </para>
/// </remarks>
public sealed class AntiforgeryTokenMiddleware(
    RequestDelegate next,
    IAntiforgery antiforgery,
    ILogger<AntiforgeryTokenMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly IAntiforgery _antiforgery = antiforgery;
    private readonly ILogger<AntiforgeryTokenMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context)
    {
        if (IsSafeMethod(context.Request.Method))
        {
            try
            {
                // 内部会复用请求里已有的合法 Cookie，只在缺失/非法时新生成一个。
                _antiforgery.GetAndStoreTokens(context);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 下发令牌失败绝不能把一次 GET 变成 500：那只影响"接下来能不能写"，
                // 而写操作自己会重新触发一次下发（用户刷新页面即可）。
                _logger.LogWarning(ex, "下发 antiforgery 令牌失败：{Path}", context.Request.Path);
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);
}

/// <summary>
/// 写操作上的 antiforgery 强制校验。
/// </summary>
/// <remarks>
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
                    "写操作必须带 X-XSRF-TOKEN 请求头（值取自 XSRF-TOKEN Cookie）。");
            }
        }

        await next().ConfigureAwait(false);
    }

    private static bool IsSafeMethod(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
        HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method);
}
