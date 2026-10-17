using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using FileBackupTool.Api.Configuration;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Hubs;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Api.Security;
using FileBackupTool.Api.Services;
using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using FileBackupTool.Core.IO;
using FileBackupTool.Core.Monitoring;
using FileBackupTool.Core.Paths;
using FileBackupTool.Data;
using FileBackupTool.Engine;
using FileBackupTool.Monitoring;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;

// =============================================================================
// FileBackupTool 宿主（设计文档 06）。
//
// 这个文件负责三件事，且只负责这三件事：
//   ① 组合：把所有层（Core / Data / Engine / Monitoring / Api）接成一个进程；
//   ② 顺序：中间件顺序、启动期初始化顺序——顺序错了安全措施会静默失效；
//   ③ 拒绝：任何"配置不完整"都必须让宿主**起不来**并说清原因，绝不默默用默认值跑。
//
// 设计上刻意保持"一个文件从上往下读一遍就懂启动过程"，所以业务逻辑一律不在
// 这里，而是各自在 Services/ 与 Controllers/ 下。
// =============================================================================

// -----------------------------------------------------------------------------
// 0. 路径与日志：必须最先做
//    Serilog 的目录、单实例锁文件、数据库路径都依赖 AppPaths，而 AppPaths 一旦
//    解析错（例如把相对路径解析到别的地方），后面所有东西都会写错位置。
//    所以它排在 DI 之前，并且只有一个事实来源。
// -----------------------------------------------------------------------------
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
IConfiguration config = builder.Configuration;

AppPaths paths = AppPaths.Resolve(config, builder.Environment.ContentRootPath);

// 目录必须先存在：Serilog 的文件 sink 在构造时就打开文件，目录不存在会静默失败，
// 于是"日志文件不见了"成为第一个也是最难查的故障。
paths.EnsureCreated();

LogEventLevel minimumLevel = ParseLevel(config["Serilog:MinimumLevel"], LogEventLevel.Information);

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(minimumLevel)
    // ASP.NET 自己的 Information 级日志（每次请求的路由匹配等）会把业务日志淹掉；
    // 而 EF 的命令级日志在批量写入时能刷出几十万行。两者都压到 Warning。
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    // 按天滚动（06 §7）。保留 31 天：滚动日志一旦无限增长，最终会把数据盘吃满，
    // 而"盘满"在本程序里意味着备份与数据库同时失败。
    .WriteTo.File(
        Path.Combine(paths.LogDir, "filebackuptool-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31,
        fileSizeLimitBytes: 64L * 1024 * 1024,
        rollOnFileSizeLimit: true,
        outputTemplate:
            "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// 让 Serilog 自己的错误（例如文件被占用）出现在控制台上，而不是凭空消失。
Serilog.Debugging.SelfLog.Enable(message => Console.Error.WriteLine($"[Serilog] {message}"));

builder.Host.UseSerilog(Log.Logger, dispose: false);

// -----------------------------------------------------------------------------
// 1. 强制单实例（设计文档 01 §6）
//    必须在任何"写数据目录"的动作之前拿到锁：两个进程同时开同一个 SQLite 库、
//    同时往同一个 blob 池写，会直接损坏备份数据。第二个实例**必须立刻退出**，
//    而不是"起不来但也不说为什么"。
// -----------------------------------------------------------------------------
using SingleInstanceGuard guard = SingleInstanceGuard.Acquire(
    paths.DataDir, out string? guardWarning, out string? guardConflict);

if (guardConflict is not null)
{
    // 双写：控制台（用户大概率是双击/命令行启动，看不到日志文件）+ Serilog。
    Console.Error.WriteLine(guardConflict);
    Log.Fatal("拒绝启动：{Reason}", guardConflict);
    Log.CloseAndFlush();
    return 1;
}

if (guardWarning is not null)
{
    // 只依赖数据目录锁文件也能保证正确性，所以这是 Warning 而不是致命错误。
    Log.Warning("单实例保护降级：{Warning}", guardWarning);
}

Log.Information(
    "FileBackupTool 启动中：ContentRoot={ContentRoot}，DataDir={DataDir}，数据库={Database}。",
    paths.ContentRoot, paths.DataDir, paths.DatabasePath);

// -----------------------------------------------------------------------------
// 2. 配置绑定
// -----------------------------------------------------------------------------
// 这里刻意创建一份**本地**实例：有些决定必须在 Build() 之前做出（Cookie 寿命、
// 锁定阈值、HSTS 有效期），那时 DI 容器还不能解析 IOptions。两份实例绑定的是
// **同一个配置节**，所以取值必然一致。
var securityOptions = new SecurityOptions();
config.GetSection(SecurityOptions.SectionName).Bind(securityOptions);

var reverseProxyOptions = new ReverseProxyOptions();
config.GetSection(ReverseProxyOptions.SectionName).Bind(reverseProxyOptions);

var lockoutOptions = new AccountLockoutOptions();
config.GetSection(AccountLockoutOptions.SectionName).Bind(lockoutOptions);

var loginRateLimitOptions = new LoginRateLimitOptions();
config.GetSection(LoginRateLimitOptions.SectionName).Bind(loginRateLimitOptions);

var apiRateLimitOptions = new ApiRateLimitOptions();
config.GetSection(ApiRateLimitOptions.SectionName).Bind(apiRateLimitOptions);

var writeRateLimitOptions = new WriteRateLimitOptions();
config.GetSection(WriteRateLimitOptions.SectionName).Bind(writeRateLimitOptions);

var hstsOptions = new HstsApiOptions();
config.GetSection(HstsApiOptions.SectionName).Bind(hstsOptions);

var retentionOptions = new RetentionOptions();
config.GetSection(RetentionOptions.SectionName).Bind(retentionOptions);

var jobOptions = new JobOptions();
config.GetSection(JobOptions.SectionName).Bind(jobOptions);

// -----------------------------------------------------------------------------
// 3. 服务注册
// -----------------------------------------------------------------------------

// ---- 3.1 宿主自己的基础设施 ----
RuntimeInfo runtimeInfo = new();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(runtimeInfo);
builder.Services.AddHttpContextAccessor();

// 数据保护密钥环放在数据目录里（设计文档 05 §7）。
// Cookie 票据的加解密依赖它。因此：
//   · 目录必须在 DataDir 内——HardExcludeFileFilter 会把 DataDir 整个排除在备份之外，
//     密钥环进了备份就等于把"伪造会话"的能力一起备份了；
//   · 应用名必须显式固定。默认值是 ContentRoot 的路径，那样一旦把整个目录挪个位置
//     （便携部署最常见的操作），密钥环换名 → **所有会话失效**。
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir))
    .SetApplicationName("FileBackupTool");

// ---- 3.2 选项 ----
builder.Services.AddOptions<SecurityOptions>()
    .Bind(config.GetSection(SecurityOptions.SectionName));
builder.Services.AddOptions<ReverseProxyOptions>()
    .Bind(config.GetSection(ReverseProxyOptions.SectionName));
builder.Services.AddOptions<AccountLockoutOptions>()
    .Bind(config.GetSection(AccountLockoutOptions.SectionName));
builder.Services.AddOptions<LoginRateLimitOptions>()
    .Bind(config.GetSection(LoginRateLimitOptions.SectionName));
builder.Services.AddOptions<ApiRateLimitOptions>()
    .Bind(config.GetSection(ApiRateLimitOptions.SectionName));
builder.Services.AddOptions<WriteRateLimitOptions>()
    .Bind(config.GetSection(WriteRateLimitOptions.SectionName));
builder.Services.AddOptions<HstsApiOptions>()
    .Bind(config.GetSection(HstsApiOptions.SectionName));
builder.Services.AddOptions<SchedulerOptions>()
    .Bind(config.GetSection(SchedulerOptions.SectionName));
builder.Services.AddOptions<RetentionTickOptions>()
    .Bind(config.GetSection(RetentionTickOptions.SectionName));
builder.Services.AddOptions<RetentionOptions>()
    .Bind(config.GetSection(RetentionOptions.SectionName));
builder.Services.AddOptions<MonitoringOptions>()
    .Bind(config.GetSection(MonitoringOptions.SectionName));
builder.Services.AddOptions<JobOptions>()
    .Bind(config.GetSection(JobOptions.SectionName));

// StorageOptions 的 DataDirectory 不在 appsettings.json 里，而是从 DataDir 推导后注入：
// 让"数据目录"只有一个事实来源，否则会出现 appsettings 说 A、日志写到 B、
// 而扫描排除规则按 C 判断的经典事故。
builder.Services.AddOptions<StorageOptions>()
    .Bind(config.GetSection(StorageOptions.SectionName))
    .Configure(o => o.DataDirectory = paths.DataDir);

// ---- 3.3 数据库 ----
// 连接串由 DataDir 推导（同上的单一事实来源）。
DbContextOptions<AppDbContext> dbContextOptions = DatabaseBootstrapper.BuildOptions(paths.DatabasePath);

// 先注册已经建好的选项，再 AddDbContextFactory：后者内部对 DbContextOptions<T> 用的是
// TryAdd，所以先注册的这份胜出，不必把 BuildOptions 的内容在这里抄一遍。
builder.Services.AddSingleton(dbContextOptions);
builder.Services.AddDbContextFactory<AppDbContext>();

// Identity 的 EF 存储是按"每个请求一个 DbContext"的假设写的（构造函数注入 AppDbContext），
// 而引擎与宿主其余部分用的是工厂（单例，方便在后台服务里短命地用）。
// 两者并存：这里的 scoped 上下文由工厂创建，随作用域一起释放。
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ---- 3.4 Identity ----
builder.Services
    .AddIdentity<IdentityUser<Guid>, IdentityRole<Guid>>(options =>
    {
        // 密码策略（设计文档 05 §2.3）。
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        // RequiredUniqueChars 保持 Identity 的默认 1：文档没有要求"N 个不同字符"，
        // 悄悄加一条更严的规则只会让用户对着"密码不合规"却看不出哪里不合规。
        options.Password.RequiredUniqueChars = 1;

        // 账户锁定（设计文档 05 §4：连续 5 次失败锁 15 分钟）。
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = lockoutOptions.MaxFailedAttempts;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(lockoutOptions.Minutes);

        // 单用户、无邮箱验证：唯一账号由 IdentitySeeder 建，RequireConfirmedAccount
        // 若为 true 会让登录永远失败。
        options.User.RequireUniqueEmail = false;
        options.SignIn.RequireConfirmedAccount = false;
        options.SignIn.RequireConfirmedEmail = false;
    })
    .AddEntityFrameworkStores<AppDbContext>()
    // 这里原先有 .AddDefaultTokenProviders()（TOTP 验证器与恢复码的令牌提供者），
    // 随 TOTP 一起删除——全仓已无任何 token provider 的解析点，删掉不影响任何现存路径。
    // ⚠️ 但缺失是**静默的**：DI 只在**解析**时失败，不在启动期。将来若加"邮箱确认"或
    // "密码重置"，UserManager.GenerateXxxTokenAsync 会在运行期抛 NotSupportedException
    // （正是被删那段注释警告过的失败方式）。届时必须把这一行加回来。
    // "密码不得与用户名相同"（Identity 内置的 PasswordValidator 不比较用户名）。
    // 注册成校验器即覆盖播种建号、改密码等所有入口。
    .AddPasswordValidator<UserNamePasswordValidator>();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "fbt.auth";
    options.Cookie.HttpOnly = true;
    // SameSite=Strict（05 §3）：浏览器不会在跨站请求里带上它，这是 CSRF 的第一层防护。
    options.Cookie.SameSite = SameSiteMode.Strict;
    // Secure 策略取 SameAsRequest 而不是 Always：本程序在生产里由 nginx 终止 TLS
    //（01 §7.2），而 ForwardedHeaders 采信 X-Forwarded-Proto 之后请求在应用内就是 https，
    // 于是等价于 Always。唯一"不设 Secure"的场合是直接 http://localhost 本机调试——
    // 那里若强制 Secure，浏览器会直接丢弃 Cookie，结果是**根本登录不了**，比降级这一项更难排查。
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromDays(Math.Max(1, securityOptions.CookieSlidingDays));
    // 滑动 7 天；绝对 30 天由 SessionValidationEvents 负责（Cookie 中间件只有滑动）。
    options.SlidingExpiration = true;
    // EventsType（而不是 options.Events）：它会**整体替换** AddIdentity 设的
    // OnValidatePrincipal，所以 SessionValidationEvents 内部必须显式把安全戳校验调回来。
    options.EventsType = typeof(SessionValidationEvents);
    // 未登录 / 无权限时不要 302 到登录页：SPA 需要的是 401/403 + 稳定 code，
    // 由 SessionValidationEvents 覆写。
    options.LoginPath = "/api/auth/login";
    options.AccessDeniedPath = "/api/auth/login";
});

// ---- 3.5 antiforgery（设计文档 05 §8：**全部写操作**启用）----
builder.Services.AddAntiforgery(options =>
{
    // 名字是**前端契约**（web/src/api/http.ts），改这里必须同步改前端。
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "XSRF-TOKEN";

    // **HttpOnly = true。** 这里原来的注释说"必须允许 JS 读取：前端要靠 document.cookie 取它"，
    // 那句话已经过时了：前端读的是 `GET /api/auth/csrf` 的**响应体**（request token），
    // 而 axios 内置的读 cookie 行为是用 `withXSRFToken: false` **明确关掉**的
    //（不关会覆盖请求头，见 09 H3c）。ASP.NET Core 的配对令牌模型下，
    // cookie 里那个 *cookie token* 本来就不需要被 JS 看见——校验时由服务端自己读。
    //
    // 所以这个 cookie 没有理由对脚本可见：XSS 读到它也拿不到 request token，
    // 而关掉读取面是纯收益（纵深防御）。它仍然会随请求自动发送，HttpOnly 不影响这一点。
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
});

// ---- 3.6 限流（设计文档 05 §4）----
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.OnRejected = async (context, ct) =>
    {
        // 明确告诉用户"等多久"，否则前端只能显示一个无从下手的"失败"。
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        await ApiErrorWriter.WriteAsync(
            context.HttpContext,
            StatusCodes.Status429TooManyRequests,
            ApiErrorCodes.RateLimited,
            "请求过于频繁，已被限流。请稍后重试。",
            null,
            ct);
    };

    // 全局配额：已认证 / 匿名两档（appsettings 的 Security:ApiRateLimit）。
    // 只作用于 /api 与 /hubs——静态资源（index.html、前端 bundle）不该被计入，
    // 否则光是打开登录页就能把一个匿名配额用掉一大半，看起来像"网站挂了"。
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
    {
        PathString path = context.Request.Path;

        if (!path.StartsWithSegments("/api") && !path.StartsWithSegments("/hubs"))
        {
            return RateLimitPartition.GetNoLimiter("static");
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            // 单用户系统：所有设备共用一份配额。故意不按 IP 分——用户从多个网络
            //（家里、手机）访问时，按 IP 分只会让配置与排查更难。
            return RateLimitPartition.GetFixedWindowLimiter("authenticated", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, apiRateLimitOptions.AuthenticatedPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
        }

        return RateLimitPartition.GetFixedWindowLimiter(
            $"anonymous:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, apiRateLimitOptions.AnonymousPerMinute),
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            });
    });

    // 登录：每 IP 每 5 分钟 5 次（05 §4）。按 **IP** 而不是全局，
    // 这样暴力破解的成本与攻击者换 IP 的成本绑在一起。
    options.AddPolicy(RateLimitPolicies.Login, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"login:{context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, loginRateLimitOptions.PermitLimit),
                Window = TimeSpan.FromMinutes(Math.Max(1, loginRateLimitOptions.WindowMinutes)),
                QueueLimit = 0,
            }));

    // 写 / 重型操作：防止误连点与前端死循环造出大量备份作业。
    options.AddPolicy(RateLimitPolicies.Write, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"write:{context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, writeRateLimitOptions.PermitLimit),
                Window = TimeSpan.FromSeconds(Math.Max(1, writeRateLimitOptions.WindowSeconds)),
                QueueLimit = 0,
            }));
});

// ---- 3.7 反向代理（设计文档 05 §6.1 ②）----
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    // 只采信 X-Forwarded-For / X-Forwarded-Proto。X-Forwarded-Host 不采信：
    // 我们没有基于 Host 的逻辑，多采信一个头只多一个可被伪造的输入。
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    // Clear 是必须的：默认值里包含回环地址，而我们要的是"只信配置里那几个"。
    options.KnownProxies.Clear();

    foreach (string proxy in reverseProxyOptions.TrustedProxies)
    {
        if (IPAddress.TryParse(proxy, out IPAddress? address))
        {
            options.KnownProxies.Add(address);
        }
        else
        {
            // 写主机名是常见误解（KnownProxies 只认 IP）。静默忽略会让"限流按 IP"
            // 悄悄退化成"所有请求都算同一个 IP"，必须说出来。
            Log.Warning("ReverseProxy:TrustedProxies 里的 {Proxy} 不是合法 IP，已忽略。", proxy);
        }
    }

    // 只信任一跳：nginx 直连本进程。设成更大的值等于允许客户端自己往里塞伪造的跳。
    options.ForwardLimit = 1;
});

// ---- 3.8 HSTS（05 §6.1 ④：由应用设置，nginx 透传）----
if (reverseProxyOptions.UseHsts)
{
    builder.Services.AddHsts(options =>
    {
        options.MaxAge = TimeSpan.FromDays(Math.Max(1, hstsOptions.HstsMaxAgeDays));
        options.IncludeSubDomains = true;
        // Preload 需要域名所有者主动向浏览器厂商提交，不在这里替用户决定。
        options.Preload = false;
    });
}

// ---- 3.9 MVC ----
builder.Services.AddControllers().AddJsonOptions(options =>
{
    // 数据库里只存 UTC，但 SQLite 读回来的是 Unspecified，不转换就会输出
    // 不带 Z 的时间串，浏览器会按**本地时区**解释它（显示时间凭空偏移）。
    options.JsonSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
    options.JsonSerializerOptions.Converters.Add(new NullableUtcDateTimeJsonConverter());
});

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    // [ApiController] 的模型校验默认产出 ProblemDetails，与本项目的
    // { code, message, detail? } 契约不一致——前端只能对一个形状写解析。
    // 这里把它拉回统一形状，code 用稳定的 VALIDATION_FAILED。
    options.InvalidModelStateResponseFactory = context =>
    {
        Dictionary<string, string[]> errors = context.ModelState
            .Where(pair => pair.Value?.Errors.Count > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value!.Errors
                    .Select(error => string.IsNullOrWhiteSpace(error.ErrorMessage)
                        ? "参数格式不正确。"
                        : error.ErrorMessage)
                    .ToArray());

        return new ObjectResult(new ApiErrorBody(
            ApiErrorCodes.ValidationFailed,
            "请求参数不合法。",
            JsonSerializer.Serialize(errors, new JsonSerializerOptions(JsonSerializerDefaults.Web))))
        {
            StatusCode = StatusCodes.Status400BadRequest,
        };
    };
});

// 刻意**不配置 CORS**（05 §8）：前端由本宿主同源带起，不开放跨域比"开放但配得严"
// 更安全——开放之后任何一个漏配的响应头都会变成实际可达的攻击面。
// 如果将来真的要独立部署前端，必须回到设计文档重新评审，而不是在这里加一行 AddCors。

// ---- 3.10 SignalR（设计文档 06 §5）----
builder.Services.AddSignalR();

// ---- 3.11 引擎 / 数据层 / 监控 ----
// 顺序说明：AddFileBackupEngine 内部用 TryAdd 注册了一堆默认实现
//（包括"不发事件"的 IJobEventPublisher），所以它必须在**被覆盖的注册之前**调用。

// 低 IO 优先级开关：先用配置里的值做出厂默认；启动后由 SettingsService 用数据库里的值覆盖
//（数据库是事实来源，配置只负责首次播种 —— 与保留策略同一套约定）。
builder.Services.AddSingleton(new IoPrioritySwitch(jobOptions.LowPriorityIo));

builder.Services.AddFileBackupEngine();

// 作业事件 → SignalR。先替换掉引擎的 NullJobEventPublisher，
// 再包一层补发 snapshot.committed（引擎只发 job.completed，见该装饰器的说明）。
builder.Services.RemoveAll<IJobEventPublisher>();
builder.Services.AddSingleton<SignalRJobEventPublisher>();
builder.Services.AddSingleton<IJobEventPublisher>(sp => new SnapshotCommittedPublisher(
    sp.GetRequiredService<SignalRJobEventPublisher>(),
    sp,
    // 全名写出：本文件 using 了 Serilog，而 Serilog 也导出 ILogger/ILogger<T>。
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SnapshotCommittedPublisher>>()));

// 业务日志 → 先落库、再推实时通道。装饰器保证"凡是进表的日志一定会推"，
// 不存在"某条路径忘了发事件"。Engine 只注册了接口，所以这里显式注册具体实现。
builder.Services.RemoveAll<IBackupLogSink>();
builder.Services.AddSingleton<BackupLogSink>();
builder.Services.AddSingleton<IBackupLogSink>(sp => new RealtimeLogSink(
    sp.GetRequiredService<BackupLogSink>(),
    sp.GetRequiredService<SignalRJobEventPublisher>(),
    sp.GetRequiredService<IClock>(),
    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RealtimeLogSink>>()));

// StartupSelfHealService：把上次运行遗留的 InProgress 快照标为 Aborted。
//
// **必须注册在监控与作业队列之前。** IHostedService 的 StartAsync 是按注册顺序依次 await 的，
// 而"本进程创建出一条 InProgress 快照"只有一条途径：作业队列的消费循环真的开始执行备份。
// 所以只要自愈在 JobQueueService 之前跑完，本进程就不可能创建出 InProgress 行，
// 自愈也就绝不会把一次**正在运行的合法备份**标成 Aborted。
//
// 反过来的失败模式很隐蔽：监控服务的钩子③在启动时无条件排一次全量扫描，而它注册在自愈之前、
// 作业队列的消费循环又先于自愈开跑 —— 那次备份的 InProgress 行正好落在自愈的
// SELECT 与 UPDATE 之间，于是一个合法快照被写成"上次进程未正常退出"，
// 并被永久排除在镜像链之外（查询一律只看 Completed）。
builder.Services.AddHostedService<StartupSelfHealService>();

// 监控模块。它是**可选**的：MonitoringAccess 在它缺席时给出明确的
// "功能不可用"答复，而不是让整个 API 500。
builder.Services.AddFileBackupMonitoring();

// 这是 06 §8 提到的"唯一一处接线"：监控模块只引用 Core，所以
// ①"从哪儿读任务"与②"触发备份"必须由宿主提供。
builder.Services.AddSingleton<IMonitoringTaskSource, DbMonitoringTaskSource>();
builder.Services.AddSingleton<IBackupTrigger, BackupTrigger>();

// ---- 3.12 Api 层自己的服务 ----
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<SessionRegistry>();

// SessionValidationEvents 是 Cookie 认证的 EventsType（见上面 options.EventsType）。
// 认证处理器会从**请求容器**里 GetRequiredService(EventsType) 来实例化它，
// 所以它**必须**在 DI 里注册。漏注册的失败模式很迷惑：应用能正常启动、日志一切正常，
// 但**每一个**受认证保护的请求都会以 409 + "No service for type …" 结束。
builder.Services.AddScoped<SessionValidationEvents>();

builder.Services.AddSingleton<AuditLogger>();
builder.Services.AddSingleton<DirectoryRelationshipValidator>();
builder.Services.AddSingleton<TaskStatsQuery>();
builder.Services.AddSingleton<ApiMapper>();
builder.Services.AddSingleton<TaskProjection>();
builder.Services.AddSingleton<MonitoringAccess>();
builder.Services.AddSingleton<ServerInfoService>();
builder.Services.AddSingleton<IdentitySeeder>();

// ---- 3.13 后台服务（设计文档 06 §8）----
// JobQueueService：把引擎的串行作业队列接到宿主生命周期上。
// （StartupSelfHealService 已挪到监控模块之前注册，原因见那里的注释：
//   自愈必须在消费循环开始执行任何备份之前完成。）
builder.Services.AddHostedService<JobQueueService>();
// SchedulerService：每 60 秒检查一次任务是否到期。
builder.Services.AddHostedService<SchedulerService>();
// RetentionService：默认关闭，只有数据库里的显式开关为真才会淘汰。
builder.Services.AddHostedService<RetentionService>();

WebApplication app = builder.Build();

// -----------------------------------------------------------------------------
// 4. 中间件顺序
//    这里的每一步顺序都有理由，改动前请先读注释——顺序错了通常不会报错，
//    而是某项安全措施静默失效。
// -----------------------------------------------------------------------------

// ① 反向代理头必须最靠前：限流按 IP 分档、审计记来源 IP，都依赖 Connection.RemoteIpAddress。
//    放在后面的话，nginx 之后所有请求的来源都是 127.0.0.1，限流形同虚设、审计彻底无用。
app.UseForwardedHeaders();

// ② 请求日志（06 §7）。放在异常中间件之前，这样 500 也会出现在访问日志里。
app.UseSerilogRequestLogging();

// ③ 统一异常 → { code, message, detail? }。必须包住后面所有东西。
app.UseMiddleware<ApiExceptionMiddleware>();

if (reverseProxyOptions.UseHsts)
{
    // 只在 https 响应上加 Strict-Transport-Security 头（中间件自己判断）。
    app.UseHsts();
}

// ④ 静态资源 + SPA。放在路由之前，命中文件时直接短路，不进入限流与认证管道。
app.UseDefaultFiles();
app.UseStaticFiles();

// ⑤ 路由：后面的端点级特性（[EnableRateLimiting]、[Authorize]）都依赖它先解析出端点。
app.UseRouting();

// 注意：这里**没有**"在安全方法上下发 antiforgery 令牌"的中间件，这是有意的。
// 早先版本在这里挂过一个，它破坏了配对令牌的前提：这个位置在 UseAuthentication **之前**，
// 于是每个 GET 都会在**用户还是匿名时**签发/复用 cookie token，
// 而写请求的校验发生在认证之后（身份是 admin）→ 报
// "The provided antiforgery token was meant for a different claims-based user than the current user"，
// 症状是"登录之后所有写操作 400、读操作全正常"。
// 现在令牌**只**由 GET /api/auth/csrf 在认证之后签发，见下方 ⑨。

// ⑦ 认证：必须在限流之前，因为全局限流要按"已认证 / 匿名"分档。
app.UseAuthentication();

// ⑧ 限流：放在认证之后（分区键要用 User）、授权之前（未登录的重请求也该被挡住）。
app.UseRateLimiter();

app.UseAuthorization();

// ⑨ antiforgery 令牌**只由** GET /api/auth/csrf 签发（见 AuthController.Csrf），
// 所以这里没有"在安全方法上顺手签发"的中间件。
//
// 为什么不能有第二个签发点：ASP.NET Core 的 antiforgery 是**配对令牌**——
// cookie 一份、请求头一份，而且请求令牌与"签发时的用户身份"绑定。
// 一旦存在两个签发点（尤其是挂在认证之前的那个），cookie 与请求令牌就可能来自
// 不同的身份，校验必然失败，报
// "The provided antiforgery token was meant for a different claims-based user than the current user"，
// 症状是**登录之后所有写操作 400、读操作全正常**。
// 保持"唯一签发点 + 它在认证之后"，cookie 就永远与最后一次签发的请求令牌同身份。

// ⑩ 端点。
app.MapControllers();
app.MapHub<EventsHub>("/hubs/events");

// ⑪ 未匹配路由的兜底。**不能**简单地对所有路径回 index.html：
//    那样 /api/xxx（打错的接口名）会得到 200 + 一坨 HTML，前端会把
//    "接口不存在"当成"接口返回了奇怪的数据"，排查方向直接跑偏。
app.MapFallback(async context =>
{
    if (context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/hubs"))
    {
        await ApiErrorWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "接口不存在。",
            null,
            context.RequestAborted);
        return;
    }

    IWebHostEnvironment environment = context.RequestServices.GetRequiredService<IWebHostEnvironment>();
    Microsoft.Extensions.FileProviders.IFileInfo index = environment.WebRootFileProvider.GetFileInfo("index.html");

    if (!index.Exists)
    {
        // 明确说出来，而不是回一个空白页：前端没构建是最常见的"界面打不开"。
        await ApiErrorWriter.WriteAsync(
            context,
            StatusCodes.Status404NotFound,
            ApiErrorCodes.NotFound,
            "前端资源不存在。",
            "wwwroot/index.html 不存在。开发时请先在 web/ 目录执行构建，或直接访问 /api 下的接口。",
            context.RequestAborted);
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index, context.RequestAborted);
});

// -----------------------------------------------------------------------------
// 5. 启动期初始化
//    这些动作必须在 Kestrel 接受请求**之前**完成：
//    · 数据库迁移（没有表就没有任何接口可用）；
//    · 设置播种（否则第一次 GET /api/settings 会读到空值）；
//    · 初始账号（缺失时必须让宿主起不来，见下）；
//    · 会话登记表加载（未加载时 IsValid 会拒绝所有人）。
// -----------------------------------------------------------------------------
try
{
    using IServiceScope scope = app.Services.CreateScope();
    IServiceProvider services = scope.ServiceProvider;

    if (securityOptions.AutoMigrate)
    {
        await using AppDbContext db = await services
            .GetRequiredService<IDbContextFactory<AppDbContext>>()
            .CreateDbContextAsync();

        // Migrate（不是 EnsureCreated，设计文档决策 #7）：EnsureCreated 建出来的库
        // 没有 __EFMigrationsHistory，之后再也无法演进。
        await DatabaseBootstrapper.MigrateAsync(db);
        runtimeInfo.MigrationsApplied = true;
        Log.Information("数据库迁移已完成：{Database}", paths.DatabasePath);
    }
    else
    {
        Log.Information("Security:AutoMigrate 为 false，跳过迁移（请确认已用 dotnet ef database update 建库）。");
    }

    SettingsService settings = services.GetRequiredService<SettingsService>();
    await settings.SeedFromConfigurationAsync(securityOptions, retentionOptions, jobOptions, CancellationToken.None);

    // 初始账号：密码只能来自环境变量。环境变量缺失时 IdentitySeeder 抛异常，
    // 我们把它变成"拒绝启动 + 一段怎么配的说明"，**绝不**自己生成密码并打印到日志
    //（日志文件恰恰是最容易被顺手带走的文件，等于把管理员凭据贴在门上）。
    try
    {
        await services.GetRequiredService<IdentitySeeder>().SeedAsync(CancellationToken.None);
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine(ex.Message);
        Log.Fatal("拒绝启动：初始账号无法建立。");
        Log.CloseAndFlush();
        return 1;
    }

    await services.GetRequiredService<SessionRegistry>()
        .LoadAsync(securityOptions.CookieAbsoluteDays, CancellationToken.None);
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    // 迁移失败是**配置/环境错误**，不是运行环境噪声：没有表结构的话
    // 每个接口都会 500，让用户进了界面也只会看到一堆错误。
    // 所以这里拒绝启动，并留下明确的日志（而不是"起来了但什么都不工作"）。
    runtimeInfo.MigrationError = ex.Message;
    Log.Fatal(ex, "启动期初始化失败，拒绝启动（数据库不可用 / 迁移失败）。");
    Log.CloseAndFlush();
    return 1;
}

// -----------------------------------------------------------------------------
// 6. 运行
// -----------------------------------------------------------------------------
try
{
    Log.Information(
        "初始化完成，开始接受请求。监听地址见下方 Kestrel 日志（默认 http://localhost:5000，由 appsettings 的 Kestrel:Endpoints 配置）。");

    await app.RunAsync();
    return 0;
}
catch (Exception ex) when (ex is not OperationCanceledException)
{
    Log.Fatal(ex, "宿主异常终止。");
    return 1;
}
finally
{
    // 显式冲刷：finally 里的 CloseAndFlush 保证"崩溃前最后几行"也在文件里，
    // 而那几行往往正是唯一能解释崩溃的线索。
    Log.CloseAndFlush();
}

// -----------------------------------------------------------------------------
// 辅助
// -----------------------------------------------------------------------------

/// <summary>把 appsettings 里的日志级别字符串解析成 Serilog 级别；无法识别时回落并警告。</summary>
static LogEventLevel ParseLevel(string? value, LogEventLevel fallback)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return fallback;
    }

    return Enum.TryParse(value.Trim(), ignoreCase: true, out LogEventLevel level)
        ? level
        : fallback;
}
