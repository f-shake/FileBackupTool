namespace FileBackupTool.Core.Abstractions;

/// <summary>
/// 时钟抽象。设计文档 06 §2：**所有 <c>UtcNow</c> 必须经过它**，否则测试无法构造时间场景。
/// </summary>
public interface IClock
{
    /// <summary>UTC 当前时间。全库时间戳一律 UTC 存储，显示时再转本地。</summary>
    DateTime UtcNow { get; }

    /// <summary>异步等待（可被测试时钟立即推进，避免真实 sleep）。</summary>
    Task Delay(TimeSpan delay, CancellationToken ct);
}

/// <summary>生产实现。</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTime UtcNow => DateTime.UtcNow;

    public Task Delay(TimeSpan delay, CancellationToken ct) => Task.Delay(delay, ct);
}
