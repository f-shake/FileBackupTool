using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Monitoring;

/// <summary>
/// watcher 回调里的**路径预过滤**。设计文档 03 §5.6。
/// </summary>
/// <remarks>
/// 存在的理由是"回调里不能做 IO，但可以做纯字符串判断"：把明显不相干的事件挡在队列之外，
/// 队列就不会被噪声灌满（队列灌满 = 触发反压 = 阻塞回调）。
/// <para>
/// <b>过滤范围刻意做得极窄。</b>GalleryCloud 那一层的写法是"按扩展名白名单预筛"，
/// 但那在备份场景里是错的：预筛掉的**用户文件**不会被任何东西补救（权威扫描只扫
/// "脏目录"，而一个只有 .xyz 文件变过的目录根本不会变脏）。所以这里只排除三类
/// **确定不属于用户数据**的路径：
/// </para>
/// <list type="number">
/// <item>自己的数据目录（SQLite 的 <c>.db-wal</c> / <c>.db-shm</c> 边车文件在每次事务提交时
/// 都会写，是最高频的噪声源；落进源目录范围时会变成"备份触发备份"的自激）；</item>
/// <item>各任务自己的备份目录（blob 池在源目录里时同理，且它正是"自激"的第二来源）；</item>
/// <item>Windows 的 <c>$RECYCLE.BIN</c>（设计文档 03 §3 已硬编码跳过它）。</item>
/// </list>
/// <para>
/// 归一化在这里**不做**——过滤只需要"在不在子树内"的判断，而 <c>IsSameOrUnder</c> 内部
/// 已经做了平台相关的大小写归一化。防抖字典的 key 才必须显式归一化（见
/// <see cref="PendingChangeTable"/>）。
/// </para>
/// </remarks>
internal sealed class WatchPathFilter(IPathNormalizer normalizer, IReadOnlyList<string> excludedRoots)
{
    private readonly IPathNormalizer _normalizer = normalizer;
    private readonly IReadOnlyList<string> _excludedRoots = excludedRoots;

    /// <summary>该路径是否值得进入队列。</summary>
    public bool ShouldWatch(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return false;
        }

        foreach (string excluded in _excludedRoots)
        {
            if (string.IsNullOrEmpty(excluded))
            {
                continue;
            }

            if (_normalizer.IsSameOrUnder(fullPath, excluded))
            {
                return false;
            }
        }

        // $RECYCLE.BIN：设计文档 03 §3 的硬排除，和引擎侧保持一致（否则监控会把回收站里的
        // 变动当成用户变更，跑一次什么也没变的备份）。
        foreach (string segment in _normalizer.SplitSegments(fullPath))
        {
            if (string.Equals(segment, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
