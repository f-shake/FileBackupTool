using Xunit;

namespace FileBackupTool.UnitTests.Monitoring.Fakes;

/// <summary>有界等待。</summary>
/// <remarks>
/// 只用于"等另一个线程刚刚做完某件事"这种**调度**等待（例如被反压阻塞的投递方）。
/// 它**不**用来构造场景时间——静默期、合并窗口、退避一律由
/// <see cref="FakeClock"/> 推进，那是本模块测试的硬要求。
/// </remarks>
public static class TestWait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        int attempts = Math.Max(1, timeoutMs / 5);
        for (int i = 0; i < attempts && !condition(); i++)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.True(condition(), $"等待条件超时（{timeoutMs} ms）。");
    }

    /// <summary>
    /// 让出一小段真实时间，给后台循环（<c>ChangeMonitor.RunAsync</c>）跑一步的机会。
    /// </summary>
    /// <remarks>
    /// 这**不是**在构造场景时间：静默期、防抖、合并窗口仍然全部由
    /// <see cref="FakeClock"/> 推进。这是纯粹的**调度**等待——后台循环的续体要走线程池，
    /// 测试线程自己不让出时间片，它就一直没机会跑（<see cref="UntilAsync"/> 的条件若一开始
    /// 就成立，它的循环体一次都不会 await，于是整整几十轮推进可以在 1 毫秒内跑完、
    /// 循环一步都没动）。
    /// </remarks>
    public static Task SchedulingYieldAsync(int milliseconds = 10) => Task.Delay(milliseconds);
}
