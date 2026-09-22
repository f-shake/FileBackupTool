using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Monitoring;

/// <summary>一次要排给备份引擎的扫描。</summary>
internal enum ScanKind
{
    /// <summary>全量扫描：整个源目录重新枚举。</summary>
    Full = 0,

    /// <summary>局部扫描：只重扫这些目录子树。</summary>
    Local = 1,
}

/// <summary>一次扫描请求。<paramref name="Scopes"/> 是**源目录相对路径**的目录集合。</summary>
internal sealed record ScanRequest(ScanKind Kind, IReadOnlyList<string> Scopes, string Reason);

/// <summary>
/// 触发语义：**变更 → 标记脏 → 静默期（无新变更）→ 合并跑一次增量备份**。设计文档 03 §5.3。
/// </summary>
/// <remarks>
/// 一次增量备份要开事务、扫源目录、写日志；单文件高频触发会把 IO 打满并产生大量微小快照。
/// 用较长的静默期 + 合并，把海量碎事件折叠成一次备份。
/// <para>
/// 本类被拆成**两级**，因为它们解决的是两个不同的问题（设计文档的配置里也是两个参数）：
/// </para>
/// <list type="number">
/// <item><b>局部扫描防抖</b>（<c>LocalScanDebounceSeconds</c>，默认 5 秒）：
/// 把"同一个目录里连来的几十个文件变更"先攒成一坨，避免每个文件都去碰合并窗口；</item>
/// <item><b>合并窗口</b>（<c>CoalesceWindowSeconds</c>，默认 45 秒）：静默期，
/// 期间没有新变更才真正派发。</item>
/// </list>
/// <para>
/// <b>关键约束</b>：实时触发只是**提前**跑，不改变全量/增量的选取逻辑——
/// 那是引擎的职责（设计文档 03 §2），监控器只提供"脏目录"这一个输入。
/// </para>
/// <para>
/// <b>线程说明</b>：<c>MarkDirty</c> / <c>TakeReady</c> / <c>Rollback</c> 只由单消费者调用；
/// <c>RequestFullScan</c> 例外——它可能来自 API 请求线程（手动扫描）与启动自检。
/// 因此那个标志位是 <c>volatile</c>，且它的语义是幂等的（"需要一次全量"），
/// 并发写不会互相破坏。
/// </para>
/// </remarks>
internal sealed class ScanCoalescer(IPathNormalizer normalizer, TimeSpan debounce, TimeSpan coalesceWindow)
{
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly TimeSpan _debounce = debounce;
    private readonly TimeSpan _coalesceWindow = coalesceWindow;

    /// <summary>还没过防抖期的脏目录（key = 归一化后的相对路径，value = 原始相对路径）。</summary>
    private readonly Dictionary<string, string> _pending = new(normalizer.Comparer);

    /// <summary>已过防抖、正在等静默期的脏目录。</summary>
    private readonly Dictionary<string, string> _scheduled = new(normalizer.Comparer);

    private DateTime _pendingActivityUtc = DateTime.MinValue;

    private DateTime _scheduledActivityUtc = DateTime.MinValue;

    private volatile bool _fullRequested;

    private string? _fullReason;

    /// <summary>派发中（尚未确认成功）的请求。失败时靠它回滚，保证脏目录不会丢。</summary>
    private ScanRequest? _inFlight;

    /// <summary>是否有待办（含未派发的全量请求）。</summary>
    public bool HasPending => _fullRequested || _pending.Count > 0 || _scheduled.Count > 0;

    /// <summary>
    /// 状态上报用：当前待办的扫描范围。
    /// <c>null</c> = 全量；空 = 无待办（与 <c>RootMonitorStatus.ScheduledScopes</c> 的约定一致）。
    /// </summary>
    public IReadOnlyList<string>? ScheduledScopes
    {
        get
        {
            if (_fullRequested)
            {
                return null;
            }

            if (_pending.Count == 0 && _scheduled.Count == 0)
            {
                return [];
            }

            var scopes = new List<string>(_pending.Count + _scheduled.Count);
            scopes.AddRange(_pending.Values);
            scopes.AddRange(_scheduled.Values);
            return scopes.Distinct(_normalizer.Comparer).OrderBy(s => s, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>待办条目数（状态上报用）。</summary>
    public int PendingCount => _pending.Count + _scheduled.Count;

    /// <summary>标记一个目录脏了（源目录**相对**路径，空串表示源目录根）。</summary>
    public void MarkDirty(string relativeDir, DateTime nowUtc)
    {
        string key = _normalizer.Normalize(relativeDir);
        _pending[key] = relativeDir;

        // 静默期从"最后一个变更"开始算 —— 每来一次变更就重置。
        _pendingActivityUtc = nowUtc;
    }

    /// <summary>排一次**全量**扫描。任何来源（钩子 ①②③ / 手动 / 轮询 / 兜底）都走这里。</summary>
    public void RequestFullScan(string reason, DateTime nowUtc)
    {
        _fullRequested = true;
        _fullReason = reason;

        // 全量扫描覆盖一切，所以顺手把局部待办折叠掉——否则会先跑一次全量、再跑一次
        // 已经被它覆盖的局部扫描。
        _pending.Clear();
        _scheduled.Clear();
        _scheduledActivityUtc = nowUtc;
    }

    /// <summary>
    /// 跳过两个窗口，让当前待办立刻可派发（用户手动点"扫描"时用）。
    /// </summary>
    /// <remarks>
    /// 这里跳过的是**合并与防抖**，不是部分写入防护——进入本方法的脏目录早已过了静默期
    /// （它们是 <see cref="PendingChangeTable"/> 放行出来的）。所以这不是在开一个后门。
    /// </remarks>
    public void FlushNow(DateTime nowUtc)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach ((string key, string value) in _pending)
        {
            _scheduled[key] = value;
        }

        _pending.Clear();
        _scheduledActivityUtc = nowUtc - _coalesceWindow;
    }

    /// <summary>取出一个可以派发的请求；没有就返回 null。</summary>
    public ScanRequest? TakeReady(DateTime nowUtc)
    {
        if (_fullRequested)
        {
            _fullRequested = false;
            _inFlight = new ScanRequest(ScanKind.Full, [], _fullReason ?? "全量扫描");
            return _inFlight;
        }

        // 一级：防抖。攒够 LocalScanDebounceSeconds 没有新变更才进入下一级。
        if (_pending.Count > 0 && nowUtc - _pendingActivityUtc >= _debounce)
        {
            foreach ((string key, string value) in _pending)
            {
                _scheduled[key] = value;
            }

            _pending.Clear();
            _scheduledActivityUtc = nowUtc;
        }

        // 二级：静默期。期间又脏了会经由上面这一级的提升动作把 _scheduledActivityUtc 推后。
        if (_scheduled.Count > 0 && nowUtc - _scheduledActivityUtc >= _coalesceWindow)
        {
            List<string> scopes = [.. _scheduled.Values.OrderBy(s => s, StringComparer.Ordinal)];
            _scheduled.Clear();
            _inFlight = new ScanRequest(ScanKind.Local, scopes, $"变更合并（{scopes.Count} 个目录）");
            return _inFlight;
        }

        return null;
    }

    /// <summary>派发成功，确认。</summary>
    public void Commit() => _inFlight = null;

    /// <summary>
    /// 派发失败（作业队列拒绝、宿主异常）→ 把范围放回去重试。
    /// </summary>
    /// <remarks>
    /// 脏目录**绝不能因为一次投递失败而消失**：它代表的是"磁盘上确实变了"这个事实，
    /// 而权威扫描是唯一能修正它的机制。放回时把计时器也重置，避免失败后立刻重投（热循环）。
    /// </remarks>
    public void Rollback(DateTime nowUtc)
    {
        if (_inFlight is { } request)
        {
            if (request.Kind == ScanKind.Full)
            {
                RequestFullScan(request.Reason, nowUtc);
            }
            else
            {
                foreach (string scope in request.Scopes)
                {
                    _scheduled[_normalizer.Normalize(scope)] = scope;
                }

                _scheduledActivityUtc = nowUtc;
            }
        }

        _inFlight = null;
    }
}
