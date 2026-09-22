using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Monitoring;

/// <summary>待定表里的一条记录。</summary>
internal sealed class PendingChangeEntry
{
    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    /// <summary>是否已经取过一次 (Length, LastWriteTimeUtc) 样本。</summary>
    public bool HasSample { get; set; }

    /// <summary>采样时文件是否不存在（删除也是一种"状态"，同样要观察它是否稳定）。</summary>
    public bool Missing { get; set; }

    public long Length { get; set; }

    public DateTime LastWriteUtc { get; set; }

    /// <summary>最近一次观察到 (Length, LastWriteTimeUtc) 变化的时刻。静默期从它开始算。</summary>
    public DateTime ChangedAtUtc { get; set; }

    public int RetryCount { get; set; }

    /// <summary>下次允许重试的时刻（指数退避）。</summary>
    public DateTime? NextAttemptUtc { get; set; }
}

/// <summary>一条已经稳定、可以放行到"待备份"的变更。</summary>
internal readonly record struct SettledChange(string FullPath, bool IsDirectory);

/// <summary>
/// **部分写入防护**（设计文档 03 §5.2）。备份场景的硬要求。
/// </summary>
/// <remarks>
/// <b>为什么必须有它</b>：GalleryCloud 的 5 秒防抖是"从第一个事件起的**固定窗口**"，
/// 不是静默期——大文件拷到一半就会被读。在相册里最多少一行记录；在备份里严重得多：
/// 一份**截断的内容**会被固化成 blob 并算出哈希，而 dedup 键把它当作合法内容，
/// **之后会被反复复用，污染所有后续快照**（设计文档 03 §3.3 的 Length 预筛正是按长度命中的）。
/// <para>
/// 规则：
/// </para>
/// <list type="number">
/// <item>变更先进待定表，**不直接触发备份**；</item>
/// <item>只有当 <c>(Length, LastWriteTimeUtc)</c> 在 <c>SettleWindowSeconds</c> 内**不再变化**，
/// 才放行；</item>
/// <item>窗口内又变了 → 重置计时；</item>
/// <item>对 <c>IOException</c> 做有限次指数退避重试，覆盖"文件被独占锁定"——这种状态
/// **静默期检测不出来**（长度和时间戳都不再变化，但内容根本读不出来）。</item>
/// </list>
/// <para>
/// <b>为什么用"采样对比"而不是"睡 10 秒再看"</b>：睡眠会把单消费者挂在原地，
/// 一个正在写 10 GB 的文件会让整台机器的监控停 10 秒。采样对比把成本摊进既有的 tick，
/// 而且天然实现了"窗口内又变了就重置"——不需要额外的定时器。
/// </para>
/// <para>
/// <b>额外收益</b>：编辑一个文档保存 10 次，会被折叠成一次生效。
/// </para>
/// </remarks>
internal sealed class PendingChangeTable
{
    private readonly IFileSystem _fs;
    private readonly IPathNormalizer _normalizer;
    private readonly IClock _clock;
    private readonly TimeSpan _settleWindow;
    private readonly int _retryCount;
    private readonly TimeSpan _retryBaseDelay;
    private readonly ILogger _logger;

    /// <summary>
    /// key 必须是**平台相关的大小写归一化之后**的路径（设计文档 03 §5.6 的强制要求）。
    /// Windows 上 <c>IMG.JPG</c> 与 <c>img.jpg</c> 是同一个文件，若用原始字符串当 key，
    /// 它会变成两条待定记录，各自独立计时——两边都会被反复重置，谁都settle不下来，
    /// 表现为"这个文件永远不进备份"。
    /// </summary>
    private readonly Dictionary<string, PendingChangeEntry> _entries;

    public PendingChangeTable(
        IFileSystem fs,
        IPathNormalizer normalizer,
        IClock clock,
        JobOptions jobOptions,
        TimeSpan settleWindow,
        ILogger logger)
    {
        _fs = fs;
        _normalizer = normalizer;
        _clock = clock;
        _settleWindow = settleWindow;
        _retryCount = Math.Max(0, jobOptions.IoRetryCount);
        _retryBaseDelay = TimeSpan.FromSeconds(Math.Max(0, jobOptions.IoRetryBaseDelaySeconds));
        _logger = logger;
        _entries = new Dictionary<string, PendingChangeEntry>(normalizer.Comparer);
    }

    /// <summary>待定条目数（状态上报用）。</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// 记录一次变更。**不触发备份**——这是与"防抖后直接触发"最本质的区别。
    /// </summary>
    public void Track(string fullPath, bool isDirectory)
    {
        string key = _normalizer.Normalize(fullPath);
        DateTime now = _clock.UtcNow;

        if (_entries.TryGetValue(key, out PendingChangeEntry? existing))
        {
            // 又变了 → 重置计时。这是 §5.2 规则 3，也是"持续变化的文件永远不进备份"的实现。
            existing.ChangedAtUtc = now;
            existing.NextAttemptUtc = null;
            return;
        }

        _entries[key] = new PendingChangeEntry
        {
            FullPath = fullPath,
            IsDirectory = isDirectory,
            ChangedAtUtc = now,
        };
    }

    /// <summary>清空（任务配置变更时用）。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// 结算一轮：找出已经稳定的条目并移出待定表。
    /// </summary>
    /// <remarks>
    /// 只由单消费者调用，因此不需要任何同步。
    /// </remarks>
    public async Task<IReadOnlyList<SettledChange>> EvaluateAsync(CancellationToken ct)
    {
        List<SettledChange>? released = null;
        List<string>? finished = null;

        foreach ((string key, PendingChangeEntry entry) in _entries)
        {
            ct.ThrowIfCancellationRequested();

            DateTime now = _clock.UtcNow;
            if (entry.NextAttemptUtc is { } next && now < next)
            {
                continue;
            }

            Sample sample;
            try
            {
                sample = ReadSample(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (entry.RetryCount >= _retryCount)
                {
                    // 重试用尽：**保持待定**，不猜、不放过。宁可晚一点备份，也不能把一个
                    // 读不出来的文件（可能正被独占锁定、可能内容是截断的）当成合法内容固化。
                    entry.NextAttemptUtc = null;
                    entry.RetryCount = 0;
                    _logger.LogWarning(ex, "读取变更文件失败且重试已用尽，保持待定：{Path}", entry.FullPath);
                    continue;
                }

                entry.RetryCount++;

                // 指数退避：0.5s → 1s → 2s（参数在 JobOptions）。
                // **不在这里 await 等待**——退避只推迟"下一次尝试"，不阻塞单消费者。
                // 用 await 等待会把整台机器的监控停住几秒，而它只是为了一个被锁住的文件。
                entry.NextAttemptUtc = now + RetryDelay(entry.RetryCount);
                _logger.LogDebug(ex, "读取变更文件失败（第 {N} 次），将重试：{Path}", entry.RetryCount, entry.FullPath);
                continue;
            }
            catch (Exception ex)
            {
                // 其它异常（权限、路径过长）：同样保持待定，但要留下痕迹。
                entry.RetryCount = 0;
                entry.NextAttemptUtc = null;
                _logger.LogWarning(ex, "检查变更文件状态失败，保持待定：{Path}", entry.FullPath);
                continue;
            }

            entry.RetryCount = 0;
            entry.NextAttemptUtc = null;

            bool changed = !entry.HasSample
                           || entry.Missing != !sample.Exists
                           || (sample.Exists && (entry.Length != sample.Length || entry.LastWriteUtc != sample.LastWriteTimeUtc));

            if (changed)
            {
                entry.HasSample = true;
                entry.Missing = !sample.Exists;
                entry.Length = sample.Length;
                entry.LastWriteUtc = sample.LastWriteTimeUtc;
                entry.ChangedAtUtc = _clock.UtcNow;
                continue;
            }

            if (_clock.UtcNow - entry.ChangedAtUtc < _settleWindow)
            {
                continue;
            }

            // 稳定（或"稳定地不存在"）→ 放行。
            (released ??= []).Add(new SettledChange(entry.FullPath, entry.IsDirectory));
            (finished ??= []).Add(key);
        }

        // 遍历中不能改字典，统一在最后移除。
        if (finished is not null)
        {
            foreach (string key in finished)
            {
                _entries.Remove(key);
            }
        }

        return released ?? (IReadOnlyList<SettledChange>)[];
    }

    private TimeSpan RetryDelay(int attempt) => _retryBaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));

    private Sample ReadSample(PendingChangeEntry entry)
    {
        if (entry.IsDirectory)
        {
            DirectoryEntry? dir = _fs.GetDirectory(entry.FullPath);
            return dir is null
                ? new Sample(false, 0, default)
                : new Sample(true, 0, dir.Value.LastWriteTimeUtc);
        }

        FileEntry? file = _fs.GetFile(entry.FullPath);
        return file is null
            ? new Sample(false, 0, default)
            : new Sample(true, file.Value.Length, file.Value.FileTimeUtc);
    }

    private readonly record struct Sample(bool Exists, long Length, DateTime LastWriteTimeUtc);
}
