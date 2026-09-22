using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Core.Backup;

/// <summary>增量比对中单个文件相对于上一个镜像的归类。</summary>
public enum FileChangeKind
{
    /// <summary>上一个镜像里没有这个路径。</summary>
    Created = 0,

    /// <summary>有，但 <c>(FileTimeUtc, Length)</c> 变了。</summary>
    Modified = 1,

    /// <summary>有，且 <c>(FileTimeUtc, Length)</c> 一致 → 未变，**不写记录**。</summary>
    Unchanged = 2,
}

/// <summary>
/// 变更检测。设计文档 03 §3.2。
/// </summary>
/// <remarks>
/// 变更检测用的是 <c>(FileTimeUtc, Length)</c>，**不重算哈希**。这是有意的性能取舍，
/// 代价是"同秒、同长度的修改"会被漏掉（经典的时间戳检测盲区）。
/// </remarks>
public static class FileChangeClassifier
{
    public static FileChangeKind Classify(
        bool existsInMirror,
        long mirrorLength,
        DateTime mirrorFileTimeUtc,
        long diskLength,
        DateTime diskFileTimeUtc)
    {
        if (!existsInMirror)
        {
            return FileChangeKind.Created;
        }

        bool same = mirrorLength == diskLength && mirrorFileTimeUtc == diskFileTimeUtc;
        return same ? FileChangeKind.Unchanged : FileChangeKind.Modified;
    }

    /// <summary>镜像记录类型 → 记录类型（Created / Modified / Deleted）。</summary>
    public static FileRecordType ToRecordType(FileChangeKind kind) => kind switch
    {
        FileChangeKind.Created => FileRecordType.Created,
        FileChangeKind.Modified => FileRecordType.Modified,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unchanged 不产生记录"),
    };
}

/// <summary>
/// 去重判据。设计文档 03 §3.3。
/// </summary>
/// <remarks>
/// 这是引擎里最需要权衡的一段。
/// <para>
/// <b>背景</b>：内容寻址要求"必须先知道内容的哈希，才能知道 blob 该叫什么名字"——
/// 所以**算哈希是结构性的、无法绕过**。
/// </para>
/// <para>
/// <b>旧的流程</b>：读源文件一遍算哈希 → 查重 → 没有再读一遍拷贝。浪费在新文件被读了两遍。
/// </para>
/// <para>
/// <b>"边拷边算"的陷阱</b>：拷完才知道内容已存在，则**白写一整份文件**（10GB 的文件就是白写 10GB）。
/// 命中率越高越亏，而全量备份里绝大多数文件是没变过的——**命中率恰恰很高**。
/// </para>
/// <para>
/// <b>正确方案：<c>Length</c> 预筛。</b>
/// <i>内容相同的文件，长度必然相同。</i>所以在算哈希之前先按 Length 查一次索引：
/// </para>
/// <code>
/// 查：是否存在 ContentHash 非空、且 Length == file.Length 的记录？
///
///   否  → 内容必定全新 → 直接「边拷边算」：一次读 + 一次写，省掉预读
///
///   是  → 无法证明唯一，老老实实先算哈希（读 1）
///         → File.Exists(blobPath)?
///              存在   → 复用，0 次写   ✅ 不白写
///              不存在 → 拷贝（读 2）
/// </code>
/// <para>
/// <b>这个判据可证明安全</b>——绝不会错误去重。且它恰好在最在意的地方生效：
/// 大文件的长度几乎不可能撞车，所以 10GB 文件走的是一次读 + 一次写；
/// 而长度容易撞车的小文件，本来读两遍也不贵。
/// </para>
/// </remarks>
public static class DeduplicationPolicy
{
    /// <summary>
    /// 决定走哪条路径。
    /// </summary>
    /// <param name="anyRecordWithSameLength">
    /// 是否存在"<c>ContentHash</c> 非空且 <c>Length</c> 等于本文件长度"的记录。
    /// 这个查询走 <c>(TaskId, Length)</c> 索引。
    /// </param>
    public static BlobWriteStrategy Decide(bool anyRecordWithSameLength) =>
        anyRecordWithSameLength ? BlobWriteStrategy.HashFirstThenMaybeCopy : BlobWriteStrategy.CopyWhileHashing;
}

/// <summary>为文件获取 blob 的策略。</summary>
public enum BlobWriteStrategy
{
    /// <summary>
    /// 长度全新：边读边写边算哈希（1 次读 + 1 次写），写完按哈希就位。
    /// 因为不存在同长度记录，这个写入**不可能**白费。
    /// </summary>
    CopyWhileHashing = 0,

    /// <summary>无法排除同长度：先算哈希（读 1），命中就 0 次写，未命中再拷一遍（读 2）。</summary>
    HashFirstThenMaybeCopy = 1,
}

/// <summary>
/// 局部扫描的脏目录集合。设计文档 03 §5.4。
/// </summary>
public static class DirtyDirectorySet
{
    /// <summary>
    /// 把脏目录集合**压缩**掉冗余项：若 A 是 B 的祖先，则只需扫描 B（B 的递归枚举已覆盖 A 的子树）。
    /// 不压缩会导致重复枚举与重复记录。
    /// </summary>
    /// <remarks>用最长的路径做代表，比较次数是 O(n²)，但脏目录通常只有几十个。</remarks>
    public static List<string> Compact(IEnumerable<string> dirtyDirectories, IPathNormalizer normalizer)
    {
        List<string> input = [.. dirtyDirectories
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(normalizer.TrimTrailingSeparator)
            .Distinct(normalizer.Comparer)
            .OrderBy(d => d.Length)];

        var result = new List<string>();
        foreach (string candidate in input)
        {
            bool covered = result.Any(kept => normalizer.IsSameOrUnder(candidate, kept));
            if (!covered)
            {
                result.Add(candidate);
            }
        }

        // 去掉此刻已被更短路径覆盖的项（按长度升序处理时不会发生，但保持稳健）
        var final = new List<string>();
        foreach (string candidate in result)
        {
            bool covered = final.Any(kept => normalizer.IsSameOrUnder(candidate, kept) && !normalizer.AreEqual(kept, candidate));
            if (!covered)
            {
                final.Add(candidate);
            }
        }

        return final;
    }

    /// <summary>
    /// 是否应退化为全量扫描。脏目录占比超过阈值时，局部扫描的开销已接近全量，
    /// 而全量能顺带修复所有被漏掉的事件。
    /// </summary>
    public static bool ShouldDegradeToFullScan(int dirtyDirectoryCount, int totalDirectoryCount, double degradeRatio)
    {
        if (dirtyDirectoryCount <= 0)
        {
            return false;
        }

        if (totalDirectoryCount <= 0)
        {
            // 目录数未知（例如首次扫描），保守按全量处理
            return true;
        }

        return (double)dirtyDirectoryCount / totalDirectoryCount > degradeRatio;
    }
}
