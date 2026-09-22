using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.UnitTests.TestSupport;

/// <summary>
/// 可推进的假时钟。测试里**不允许真的 sleep**。
/// </summary>
public sealed class FakeClock : IClock
{
    private DateTime _utcNow;

    public FakeClock(DateTime? startUtc = null) =>
        _utcNow = startUtc ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow => _utcNow;

    /// <summary><see cref="Delay"/> 被调用的次数（MonitoringOptions 那套退避逻辑靠它断言）。</summary>
    public int DelayCount { get; private set; }

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

    public void AdvanceSeconds(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    /// <summary>不真的等待——时间由测试自己推进。</summary>
    public Task Delay(TimeSpan delay, CancellationToken ct)
    {
        DelayCount++;
        _utcNow = _utcNow.Add(delay);
        return Task.CompletedTask;
    }
}
