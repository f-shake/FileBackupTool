using System.Text.Json;
using FileBackupTool.Api.Contracts;
using FileBackupTool.Api.Infrastructure;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Api.Security;

/// <summary>一个登录会话。</summary>
public sealed record SessionRecord(
    string Id,
    Guid UserId,
    DateTime CreatedAtUtc,
    DateTime LastSeenUtc,
    string? Ip,
    string? UserAgent);

/// <summary>
/// 会话登记表（设计文档 05 §3：允许**多**会话，但必须能列出并单独踢出）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它</b>：Cookie 认证的票据是**自包含**的（Data Protection 加密），
/// 服务端默认没有任何"当前有哪些会话"的概念，因此既列不出来、也踢不掉单个会话。
/// 文档明确要求"会话列表 + 单独踢出，以便发现异常登录"，所以必须自己维护一份登记表。
/// </para>
/// <para>
/// <b>为什么落库而不是纯内存</b>：纯内存的实现重启即失忆——用户看到的会话列表会突然变空，
/// 而浏览器里的 Cookie 还是有效的（能访问）→ 列表与事实不符，"踢出"也无从下手。
/// 存在 <c>AppSetting</c> 表里（单个 JSON），进程重启后立刻恢复一致。
/// 它是**单用户系统**，会话条数是个位数，不存在写放大问题。
/// </para>
/// <para>
/// <b>性能取舍</b>：读路径（每个请求都要判"这个会话还在不在"）走内存缓存，不查库；
/// 写路径（登录 / 登出 / 踢出 / 心跳）才落库，且心跳按 5 分钟节流——
/// 否则每个静态资源请求都会往 SQLite 写一次。
/// </para>
/// <para>
/// <b>绝对过期</b>：Cookie 认证的 <c>ExpireTimeSpan</c> 只有滑动过期，
/// 没有"绝对上限"，不额外做的话只要用户一直在用，会话可以无限延长。
/// 设计文档 05 §3 要求绝对 30 天，判据就用这里的 <see cref="SessionRecord.CreatedAtUtc"/>。
/// </para>
/// </remarks>
public sealed class SessionRegistry(
    SettingsService settings,
    IClock clock,
    ILogger<SessionRegistry> logger)
{
    /// <summary>存进 <c>AppSetting</c> 的键。</summary>
    public const string StorageKey = "Security.Sessions";

    /// <summary>心跳落库的最小间隔。见类注释的性能取舍。</summary>
    private static readonly TimeSpan PersistInterval = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly SettingsService _settings = settings;
    private readonly IClock _clock = clock;
    private readonly ILogger<SessionRegistry> _logger = logger;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Dictionary<string, SessionRecord> _sessions = new(StringComparer.Ordinal);
    private DateTime _lastPersistUtc = DateTime.MinValue;

    /// <summary>是否已完成初次加载。<c>false</c> 时不做任何判定，避免启动瞬间误踢。</summary>
    public bool Loaded { get; private set; }

    /// <summary>
    /// 启动时调用一次（在 <c>app.Run()</c> 之前）。**必须在开始接受请求前完成**，
    /// 否则 <see cref="IsValid"/> 会因未加载而返回 false，把所有人的会话都拒掉。
    /// </summary>
    public async Task LoadAsync(int absoluteLifetimeDays, CancellationToken ct)
    {
        Dictionary<string, SessionRecord> parsed = [];

        try
        {
            string? raw = await _settings.GetRawAsync(StorageKey, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                List<SessionRecord>? list = JsonSerializer.Deserialize<List<SessionRecord>>(raw, Json);
                DateTime cutoff = _clock.UtcNow.AddDays(-absoluteLifetimeDays);

                foreach (SessionRecord record in list ?? [])
                {
                    // 超过绝对过期时间的记录直接丢弃：它对应的 Cookie 也不该再被接受。
                    if (record.CreatedAtUtc >= cutoff)
                    {
                        parsed[record.Id] = record;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // 登记表损坏不应该让整个站点起不来。**但要显式告警**：
            // 静默清空等于"所有已有会话失效且用户不知道为什么"。
            _logger.LogWarning(ex, "会话登记表无法解析，已按空表处理；所有已有会话将被要求重新登录。");
            parsed.Clear();
        }

        lock (_sync)
        {
            _sessions = parsed;
            Loaded = true;
        }

        if (parsed.Count > 0)
        {
            _logger.LogInformation("已恢复 {Count} 个登录会话。", parsed.Count);
        }
    }

    public SessionRecord? Get(string? sessionId)
    {
        if (sessionId is null)
        {
            return null;
        }

        // **读路径也必须持锁。** `_sessions` 是普通 `Dictionary`，而写路径在 `lock (_sync)` 里
        // Add / Remove（容量增长时会替换整个桶数组）。一边 `TryGetValue` 一边扩容是
        // **未定义行为**：轻则读到过期视图（刚被踢出的会话仍判有效、刚创建的会话判无效），
        // 重则抛 `IndexOutOfRangeException`，而这句代码在每个受认证请求上都会执行。
        lock (_sync)
        {
            return _sessions.TryGetValue(sessionId, out SessionRecord? record) ? record : null;
        }
    }

    /// <summary>该会话是否仍然有效。未加载完成时返回 true（见 <see cref="Loaded"/> 的说明）。</summary>
    public bool IsValid(string? sessionId)
    {
        // `Loaded` 的读取也一并放进锁里：它在 LoadAsync 里与 `_sessions` 一同赋值（同为锁内），
        // 分开读会出现"Loaded 已为 true 而表还是旧的"这种撕裂读。
        lock (_sync)
        {
            if (!Loaded)
            {
                return true;
            }

            return sessionId is not null && _sessions.ContainsKey(sessionId);
        }
    }

    public async Task<SessionRecord> CreateAsync(
        Guid userId,
        string? ip,
        string? userAgent,
        CancellationToken ct)
    {
        DateTime now = _clock.UtcNow;
        var record = new SessionRecord(
            Guid.NewGuid().ToString("N"),
            userId,
            now,
            now,
            ip,
            Truncate(userAgent, 512));

        lock (_sync)
        {
            _sessions[record.Id] = record;
        }

        await PersistAsync(ct).ConfigureAwait(false);
        return record;
    }

    /// <summary>更新"最近活动时间"。落库按 <see cref="PersistInterval"/> 节流。</summary>
    public async Task TouchAsync(string sessionId, CancellationToken ct)
    {
        SessionRecord? updated = null;

        lock (_sync)
        {
            if (_sessions.TryGetValue(sessionId, out SessionRecord? existing))
            {
                updated = existing with { LastSeenUtc = _clock.UtcNow };
                _sessions[sessionId] = updated;
            }
        }

        if (updated is null)
        {
            return;
        }

        if (_clock.UtcNow - _lastPersistUtc >= PersistInterval)
        {
            await PersistAsync(ct).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<SessionRecord> List(Guid userId)
    {
        lock (_sync)
        {
            return
            [
                .. _sessions.Values
                    .Where(s => s.UserId == userId)
                    .OrderByDescending(s => s.LastSeenUtc)
            ];
        }
    }

    /// <summary>踢出单个会话。返回该会话是否存在且属于该用户。</summary>
    public async Task<bool> RevokeAsync(Guid userId, string sessionId, CancellationToken ct)
    {
        bool removed;

        lock (_sync)
        {
            removed = _sessions.TryGetValue(sessionId, out SessionRecord? existing)
                      && existing.UserId == userId
                      && _sessions.Remove(sessionId);
        }

        if (removed)
        {
            // 管理员踢出单个会话：安全相关，落库失败必须让接口报错而不是假装成功。
            await PersistAsync(ct, required: true).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// 失效某个用户的全部会话。改密码时调用。
    /// </summary>
    /// <remarks>
    /// 设计文档 05 §3 说"改密码靠 Identity 安全戳一次性失效全部会话"——那是对的，
    /// 但安全戳的校验默认有 30 分钟间隔。这里额外把登记表清掉，
    /// 让"改完密码，其他设备立刻掉线"变成**立即**成立，而不是"最多 30 分钟后"。
    /// </remarks>
    public async Task<int> RevokeAllForUserAsync(Guid userId, CancellationToken ct)
    {
        int count;

        lock (_sync)
        {
            string[] ids = [.. _sessions.Values.Where(s => s.UserId == userId).Select(s => s.Id)];
            count = ids.Length;
            foreach (string id in ids)
            {
                _sessions.Remove(id);
            }
        }

        if (count > 0)
        {
            // 踢出该用户的**全部**会话（改密码、重绑后的兜底）：同样是安全相关。
            await PersistAsync(ct, required: true).ConfigureAwait(false);
        }

        return count;
    }

    /// <summary>登出：只移除当前会话。</summary>
    public Task<bool> RemoveAsync(string sessionId, CancellationToken ct) =>
        RevokeAsyncInternal(sessionId, ct);

    private async Task<bool> RevokeAsyncInternal(string sessionId, CancellationToken ct)
    {
        bool removed;
        lock (_sync)
        {
            removed = _sessions.Remove(sessionId);
        }

        if (removed)
        {
            await PersistAsync(ct).ConfigureAwait(false);
        }

        return removed;
    }

    /// <summary>
    /// 把内存里的登记表落库。
    /// </summary>
    /// <param name="required">
    /// <c>true</c> = 这是**安全相关**的变更（管理员踢出会话），落库失败必须让调用方知道；
    /// <c>false</c> = 心跳 / 创建这类变更，落库失败只记一条告警（下一次写入会补上）。
    /// </param>
    /// <remarks>
    /// **为什么踢出必须区分出来**：内存里删掉、库里还在时，接口返回 204、审计写下"已踢出"，
    /// 管理员认为处置完了；而服务**重启**（升级、崩溃、迁移）后 <c>LoadAsync</c> 会把库里那条记录
    /// 原样读回来——**被踢掉的会话直接复活**，界面上还显示为"尚未被踢"，没有任何信号提示要再踢一次。
    /// 一次已经处置过的入侵静默恢复，比一次明确的报错严重得多。
    /// </remarks>
    private async Task PersistAsync(CancellationToken ct, bool required = false)
    {
        SessionRecord[] snapshot;
        lock (_sync)
        {
            snapshot = [.. _sessions.Values];
        }

        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _lastPersistUtc = _clock.UtcNow;
            await _settings.SetRawAsync(StorageKey, JsonSerializer.Serialize(snapshot, Json), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (required)
            {
                _logger.LogError(ex, "会话登记表写入失败（安全相关变更），本次踢出无法持久化。");

                // 用**专门的 code**：调用方必须能区分"**已经生效**但没持久化"与"没生效"。
                // 踢出本身确实已经生效——内存里删掉了，那个 Cookie 立刻失效；
                // 失败的只是"它活不过重启"。含糊地报一句 409 会让用户以为没踢掉而反复重试，
                // 或者以为处理完了而不再复核。
                throw ApiException.Conflict(
                    ApiErrorCodes.SessionPersistFailed,
                    "会话已在内存中撤销（该会话立即失效），但会话登记表落库失败——"
                    + "服务重启后它可能恢复。请排查数据库后到会话列表里复核。",
                    "SessionRegistry.PersistAsync 写入会话登记表失败。");
            }

            // 落库失败不能让登录失败；内存里仍然是准的，下一次写入会补上。
            _logger.LogWarning(ex, "会话登记表写入失败，本次变更仅存在于内存中。");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
