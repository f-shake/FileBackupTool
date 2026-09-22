using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.UnitTests.Monitoring.Fakes;

/// <summary>
/// 可推进的假时钟。
/// </summary>
/// <remarks>
/// 测"静默期"必须能精确控制时间，否则只能靠 <c>Thread.Sleep</c> 等 10 秒——
/// 那样的测试既慢又不可靠。这里的关键约定：
/// <list type="bullet">
/// <item><see cref="UtcNow"/> 只在 <see cref="Advance"/> 或 <see cref="Delay"/> 时前进；</item>
/// <item><see cref="AutoAdvance"/> 为 true（默认）时，<c>Delay</c> 立即返回并把时钟推进
/// 相应时长——用于直接驱动 <c>PumpAsync</c> 的测试（例如指数退避重试）；</item>
/// <item><see cref="AutoAdvance"/> 为 false 时，<c>Delay</c> 会一直等，直到测试调用
/// <see cref="Advance"/>——用于驱动 <c>RunAsync</c> 这种真实循环（否则循环会空转）。</item>
/// </list>
/// <para>
/// 用 <see cref="AutoAdvance"/> = false 驱动真实循环时，必须用
/// <see cref="WaitingForAdvance"/> 做握手：先等循环真的停在等待点上，再 <see cref="Advance"/>。
/// 否则会出现"循环还没进入等待、推进已经发生"的丢唤醒——被释放的是一次没人等的信号，
/// 循环要等到**下一次**推进才被唤醒，表现为"推进了八次却什么都没发生"。
/// </para>
/// </remarks>
public sealed class FakeClock : IClock
{
    private readonly object _gate = new();
    private DateTime _now;
    private int _waiting;
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public FakeClock(DateTime? startUtc = null) =>
        _now = startUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>见类型备注。</summary>
    public bool AutoAdvance { get; set; } = true;

    /// <summary>
    /// 是否已经有 <see cref="Delay"/> 停在等待点上（只对 <see cref="AutoAdvance"/> 为 false 有意义）。
    /// </summary>
    /// <remarks>
    /// "取了等待信号"与"登记为等待中"在同一个锁里完成，因此本属性为 true 时，
    /// 紧接着的 <see cref="Advance"/> 一定能把那个等待释放掉，不会丢唤醒。
    /// </remarks>
    public bool WaitingForAdvance
    {
        get
        {
            lock (_gate)
            {
                return _waiting > 0;
            }
        }
    }

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _now;
            }
        }
    }

    public async Task Delay(TimeSpan delay, CancellationToken ct)
    {
        if (!AutoAdvance)
        {
            Task wait;
            lock (_gate)
            {
                _waiting++;
                wait = _release.Task;
            }

            try
            {
                await wait.WaitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _waiting--;
                }
            }
        }

        Advance(delay);
    }

    /// <summary>推进时钟，并唤醒所有正在等待的 <see cref="Delay"/>。</summary>
    public void Advance(TimeSpan delta)
    {
        TaskCompletionSource previous;
        lock (_gate)
        {
            _now = _now.Add(delta);
            previous = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        previous.TrySetResult();
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));
}
