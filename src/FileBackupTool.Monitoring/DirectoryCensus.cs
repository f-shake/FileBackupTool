using FileBackupTool.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace FileBackupTool.Monitoring;

/// <summary>
/// 一个根目录下**目录总数**的缓存。只在"要不要退化为全量扫描"这一个判断里用。
/// </summary>
/// <remarks>
/// 退化阈值是"脏目录数**占比**"，所以分母必须知道。分母只能数出来
/// （<c>IFileSystem</c> 没有"目录数"这种现成元数据），而数一遍十万级目录是有成本的，
/// 因此**缓存 + TTL**：
/// <list type="bullet">
/// <item>只在真的要派发一次局部扫描时才去数（不是每 tick 都数）；</item>
/// <item>TTL 取兜底全量扫描的周期——反正每个周期都会重新枚举一遍整棵树，
/// 数一遍目录是其中的零头。</item>
/// </list>
/// <para>
/// 数不出来时保守返回 0，而 <c>DirtyDirectorySet.ShouldDegradeToFullScan</c> 对 0 的处理是
/// "按全量"——这是**故意**的：拿不到分母时，局部扫描的范围就无法被证明是小的。
/// </para>
/// </remarks>
internal sealed class DirectoryCensus(IFileSystem fs, TimeSpan ttl, ILogger logger)
{
    private int _count = -1;
    private DateTime _computedAtUtc = DateTime.MinValue;

    public int GetCount(string rootFullPath, DateTime nowUtc)
    {
        if (_count >= 0 && nowUtc - _computedAtUtc < ttl)
        {
            return _count;
        }

        try
        {
            // 惰性枚举 + 直接 Count()：不物化，百万目录也只是走一遍。
            _count = fs.EnumerateDirectories(rootFullPath, FileEnumerationOptions.Recursive).Count();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "统计目录数失败（本次按全量处理）：{Root}", rootFullPath);
            _count = 0;
        }

        _computedAtUtc = nowUtc;
        return _count;
    }
}
