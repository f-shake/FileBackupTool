using System.Threading.Channels;

namespace FileBackupTool.Monitoring;

/// <summary>
/// 汇聚到队列里的一条内部事件。
/// </summary>
/// <param name="Detail"><see cref="WatcherChangeKind.Fault"/> 的错误说明；其余情况为 null。</param>
internal readonly record struct ChangeEvent(
    Guid TaskId,
    string FullPath,
    WatcherChangeKind Kind,
    bool IsDirectory,
    string? Detail = null);

/// <summary>
/// 所有监控器共享的**有界**事件队列。设计文档 03 §5.6。
/// </summary>
/// <remarks>
/// 形状来自参考实现：**有界 Channel 汇聚 + 单消费者**。它解决两个问题：
/// <list type="number">
/// <item>watcher 回调在任意线程上跑，且绝不能阻塞在 IO 上——队列是唯一的解耦点。</item>
/// <item>"逐批处理"要求消费者能一次拿到一批事件，而不是被回调的节奏牵着走。</item>
/// </list>
/// <para>
/// <b>本类修正的是 GalleryCloud 最危险的一处缺陷</b>：<c>TryWrite</c> 的返回值被丢弃。
/// 队列满时事件被**静默丢弃**，而那可能正是"这个文件被改过"的唯一信号——
/// 一次漏掉的变更就是永久少一个版本。所以这里：
/// </para>
/// <list type="number">
/// <item>快路径仍然用 <c>TryWrite</c>（无锁、无等待）；</item>
/// <item>快路径失败时**不丢弃**，而是退化为 <c>WriteAsync</c>——有界 Channel 的
/// <c>Wait</c> 语义会阻塞投递方直到消费者腾出空间，这就是**反压**；</item>
/// <item>同时计数：进入反压的次数被累计，超阈值由上层排一次全量扫描（钩子 ②）。
/// 这个计数与"被丢弃的事件数"在旧实现里是同一个数——因为那时它们真的被丢了。</item>
/// </list>
/// <para>
/// <b>为什么允许在回调里阻塞？</b>反压的定义就是"让生产慢下来"。回调线程是线程池线程，
/// 短暂阻塞的代价（一个线程）远小于丢事件的代价（永久少一个版本）。消费者的单次处理周期
/// 被限制在 tick 间隔（默认 2.5 秒）内，所以阻塞是有界的；停机时 <paramref name="lifetime"/>
/// 会把它解除。
/// </para>
/// </remarks>
internal sealed class ChangeEventQueue
{
    private readonly Channel<ChangeEvent> _channel;
    private readonly CancellationToken _lifetime;
    private readonly Action<Guid, long>? _onBackpressure;
    private long _backpressureCount;

    public ChangeEventQueue(
        int capacity,
        CancellationToken lifetime,
        Action<Guid, long>? onBackpressure = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

        _lifetime = lifetime;
        _onBackpressure = onBackpressure;
        _channel = Channel.CreateBounded<ChangeEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // Wait = 满了就等。若用 DropOldest/DropWrite，反压就变成了丢弃——
            // 那正是本类要修掉的行为。
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>进入反压（即"若用 TryWrite 就会被丢弃"）的累计次数。</summary>
    public long BackpressureCount => Interlocked.Read(ref _backpressureCount);

    /// <summary>
    /// 入队。可能阻塞（有界队列满时）。
    /// </summary>
    /// <remarks>
    /// 由 watcher 回调调用，因此**绝不做 IO、绝不分配大对象**。
    /// <paramref name="ct"/> 是"本次投递"的取消（调用方传监控器的生命周期令牌），
    /// 与 <see cref="_lifetime"/> 的区别是：前者只影响这一条事件，后者影响整个队列。
    /// </remarks>
    public void Enqueue(in ChangeEvent change, CancellationToken ct)
    {
        if (_channel.Writer.TryWrite(change))
        {
            return;
        }

        long count = Interlocked.Increment(ref _backpressureCount);
        _onBackpressure?.Invoke(change.TaskId, count);

        try
        {
            // 反压：等消费者腾出空间。用 AsTask().GetAwaiter().GetResult() 是因为回调
            // 是同步签名（EventHandler）——拦截在这里等，比丢掉事件正确得多。
            _channel.Writer.WriteAsync(change, ct).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // 停机中：此时丢弃是可以接受的（进程都不在了）。
        }
        catch (ChannelClosedException)
        {
            // 队列已关闭，同上。
        }
    }

    /// <summary>非阻塞取出一条（消费者用）。</summary>
    public bool TryRead(out ChangeEvent change) => _channel.Reader.TryRead(out change);

    /// <summary>关闭写入端（停机时唤醒所有被反压阻塞的投递方）。</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
