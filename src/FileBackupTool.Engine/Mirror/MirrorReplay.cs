using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Enums;

namespace FileBackupTool.Engine.Mirror;

/// <summary>镜像里一条存活条目的轻量形式（只保留浏览/还原需要的字段）。</summary>
public readonly record struct MirrorItem(
    string PathKey,
    string Path,
    long Length,
    DateTime FileTimeUtc,
    string? ContentHash,
    int SnapshotId);

/// <summary>replay 结果。</summary>
public readonly record struct ReplayResult(Dictionary<string, MirrorItem> Items, int Issues);

/// <summary>
/// 镜像重建：<c>当前状态 = 最近一次 Full/VirtualFull + 其后所有 Increment 依次重放</c>。
/// 设计文档 03 §4。
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>Created</c> → 加入</item>
/// <item><c>Modified</c> → 替换</item>
/// <item><c>Deleted</c> → 移除</item>
/// </list>
/// <para>
/// 重放遇到异常（<c>Created</c> 遇到已存在的路径、<c>Modified</c>/<c>Deleted</c> 遇到不存在的路径）
/// 时**不静默**：计数交回调用方记 Warning 并计入快照的健康状态。
/// 旧设计用 <c>Debug.Assert(false)</c> 当唯一守卫，Release 下被编译掉，损坏静默通过。
/// </para>
/// </remarks>
public static class MirrorReplay
{
    /// <summary>
    /// 重放。记录**必须按 SnapshotId 升序**——链上 SnapshotId 递增即时间递增。
    /// </summary>
    public static ReplayResult Replay(IEnumerable<BackupFile> recordsOrderedBySnapshotId)
    {
        var items = new Dictionary<string, MirrorItem>(StringComparer.Ordinal);
        int issues = 0;

        foreach (BackupFile record in recordsOrderedBySnapshotId)
        {
            var item = new MirrorItem(
                record.RelativePathKey,
                record.RelativePath,
                record.Length,
                record.FileTimeUtc,
                record.ContentHash,
                record.SnapshotId);

            switch (record.Type)
            {
                case FileRecordType.Created:
                    if (items.ContainsKey(record.RelativePathKey))
                    {
                        // 链上同一路径被新增两次：链本身不干净，但不影响"后来者覆盖前者"的正确性
                        issues++;
                    }

                    items[record.RelativePathKey] = item;
                    break;

                case FileRecordType.Modified:
                    if (!items.ContainsKey(record.RelativePathKey))
                    {
                        issues++;
                    }

                    items[record.RelativePathKey] = item;
                    break;

                case FileRecordType.Deleted:
                    if (!items.Remove(record.RelativePathKey))
                    {
                        issues++;
                    }

                    break;
            }
        }

        return new ReplayResult(items, issues);
    }

    /// <summary>某个目录下的直接子条目（返回这一层，不含更深层）。</summary>
    public static void GroupChildren(
        IReadOnlyDictionary<string, MirrorItem> items,
        string directoryKey,
        Dictionary<string, MirrorItem> files,
        HashSet<string> directories)
    {
        string prefix = directoryKey.Length == 0 ? string.Empty : directoryKey + "\\";

        foreach ((string key, MirrorItem item) in items)
        {
            if (key.Length <= prefix.Length)
            {
                continue;
            }

            if (prefix.Length > 0 && !key.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            string remainder = key[prefix.Length..];
            int separator = remainder.IndexOf('\\');
            if (separator < 0)
            {
                files[remainder] = item;
            }
            else
            {
                directories.Add(remainder[..separator]);
            }
        }
    }
}
