using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Models;

namespace FileBackupTool.Core.Paths;

/// <summary>
/// 目录关系校验。设计文档 02 §7。
/// </summary>
/// <remarks>
/// 旧实现的 <c>ConfigBase.CheckDir</c> 只在"目录不存在"时报错，这些关系**一条都不查**。
/// 其中"备份目录位于源目录之内"是最容易犯、后果最严重的配置错误——
/// 它会与实时监控叠加成**自我触发的死循环**：备份写入的文件立刻被 watcher 当成新变更。
/// </remarks>
public sealed class DirectoryRelationshipValidator(IPathNormalizer normalizer)
{
    public const string CodePathEmpty = "PATH_EMPTY";
    public const string CodePathNotAbsolute = "PATH_NOT_ABSOLUTE";
    public const string CodePathContainsRelativeSegment = "PATH_CONTAINS_RELATIVE_SEGMENT";
    public const string CodeSameSourceAndBackup = "SAME_SOURCE_AND_BACKUP";
    public const string CodeBackupInsideSource = "BACKUP_INSIDE_SOURCE";
    public const string CodeSourceInsideBackup = "SOURCE_INSIDE_BACKUP";
    public const string CodeBackupDirShared = "BACKUP_DIR_SHARED";
    public const string CodeBackupDirsOverlap = "BACKUP_DIRS_OVERLAP";
    public const string CodeRestoreRootInsideBackup = "RESTORE_ROOT_INSIDE_BACKUP";
    public const string CodeBackupInsideRestoreRoot = "BACKUP_INSIDE_RESTORE_ROOT";

    private readonly IPathNormalizer _normalizer = normalizer;

    /// <summary>校验一个任务定义（新建或修改）与其余任务的关系。<paramref name="otherTasks"/> 应排除自身。</summary>
    public IReadOnlyList<DirectoryValidationIssue> ValidateTask(
        string sourceDir,
        string backupDir,
        IEnumerable<BackupTask> otherTasks,
        IEnumerable<string> restoreRoots)
    {
        var issues = new List<DirectoryValidationIssue>();

        ValidateSinglePath(sourceDir, "源目录", issues);
        ValidateSinglePath(backupDir, "备份目录", issues);

        if (issues.Count > 0)
        {
            // 路径本身就不合法，后面的关系比较没有意义
            return issues;
        }

        // 规则 1：SourceDir 不得等于 BackupDir
        if (_normalizer.AreEqual(sourceDir, backupDir))
        {
            issues.Add(new DirectoryValidationIssue(
                CodeSameSourceAndBackup,
                $"源目录与备份目录不能相同（都是 {sourceDir}），否则备份产物会被当成新文件反复备份。",
                sourceDir,
                backupDir));
        }
        // 规则 2：BackupDir 不得位于 SourceDir 之内（最严重）
        else if (_normalizer.IsSameOrUnder(backupDir, sourceDir))
        {
            issues.Add(new DirectoryValidationIssue(
                CodeBackupInsideSource,
                $"备份目录（{backupDir}）位于源目录（{sourceDir}）之内。这会让备份写入的文件立刻被监控当成新变更，形成自我触发的死循环。",
                sourceDir,
                backupDir));
        }
        // 规则 3：SourceDir 不得位于 BackupDir 之内
        else if (_normalizer.IsSameOrUnder(sourceDir, backupDir))
        {
            issues.Add(new DirectoryValidationIssue(
                CodeSourceInsideBackup,
                $"源目录（{sourceDir}）位于备份目录（{backupDir}）之内，会把已有的备份数据再次纳入备份。",
                sourceDir,
                backupDir));
        }

        foreach (BackupTask other in otherTasks)
        {
            // 规则 4a：两个任务的 BackupDir 不得相同
            if (_normalizer.AreEqual(backupDir, other.BackupDir))
            {
                issues.Add(new DirectoryValidationIssue(
                    CodeBackupDirShared,
                    $"备份目录 {backupDir} 已被任务「{other.Name}」使用。每个任务的备份目录必须独立（禁止共享）。",
                    backupDir,
                    other.BackupDir));
                continue;
            }

            // 规则 4b：两个任务的 BackupDir 不得互相包含
            if (_normalizer.Overlaps(backupDir, other.BackupDir))
            {
                issues.Add(new DirectoryValidationIssue(
                    CodeBackupDirsOverlap,
                    $"备份目录 {backupDir} 与任务「{other.Name}」的备份目录 {other.BackupDir} 存在包含关系。",
                    backupDir,
                    other.BackupDir));
            }
        }

        // 规则 5（及其对称情形）：备份目录与还原根白名单的关系
        foreach (string root in restoreRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (_normalizer.IsSameOrUnder(root, backupDir))
            {
                issues.Add(new DirectoryValidationIssue(
                    CodeRestoreRootInsideBackup,
                    $"还原根目录（{root}）位于备份目录（{backupDir}）之内，还原操作可能覆盖 blob 池。",
                    root,
                    backupDir));
            }
            else if (_normalizer.IsSameOrUnder(backupDir, root))
            {
                // 文档 02 §7 的 5 条规则未包含此情形。这是刻意补的一条：
                // 还原根包含备份目录时，一次"整快照还原到该根"就可能覆盖 blob，
                // 属于静默数据丢失路径，成本极低不值得放过。
                issues.Add(new DirectoryValidationIssue(
                    CodeBackupInsideRestoreRoot,
                    $"备份目录（{backupDir}）位于还原根目录（{root}）之内，向该根还原快照可能覆盖 blob 池。",
                    backupDir,
                    root));
            }
        }

        return issues;
    }

    /// <summary>校验一个还原根目录是否可用。设计文档 02 §7 规则 5。</summary>
    public IReadOnlyList<DirectoryValidationIssue> ValidateRestoreRoot(string restoreRoot, IEnumerable<BackupTask> tasks)
    {
        var issues = new List<DirectoryValidationIssue>();
        ValidateSinglePath(restoreRoot, "还原根目录", issues);
        if (issues.Count > 0)
        {
            return issues;
        }

        foreach (BackupTask task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.BackupDir))
            {
                continue;
            }

            if (_normalizer.IsSameOrUnder(restoreRoot, task.BackupDir))
            {
                issues.Add(new DirectoryValidationIssue(
                    CodeRestoreRootInsideBackup,
                    $"还原根目录（{restoreRoot}）位于任务「{task.Name}」的备份目录（{task.BackupDir}）之内。",
                    restoreRoot,
                    task.BackupDir));
            }
            else if (_normalizer.IsSameOrUnder(task.BackupDir, restoreRoot))
            {
                issues.Add(new DirectoryValidationIssue(
                    CodeBackupInsideRestoreRoot,
                    $"任务「{task.Name}」的备份目录（{task.BackupDir}）位于还原根目录（{restoreRoot}）之内，向该根还原快照可能覆盖 blob 池。",
                    task.BackupDir,
                    restoreRoot));
            }
        }

        return issues;
    }

    /// <summary>
    /// 校验"目标路径是否落在白名单还原根之内"。设计文档 04 §1.3：
    /// 还原是**写文件**操作，一个被攻破的会话就等于服务器沦陷，白名单是最重要的缓解。
    /// </summary>
    public static bool IsWithinAllowedRoots(
        IPathNormalizer normalizer,
        string targetPath,
        IEnumerable<string> allowedRoots,
        out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            reason = "目标路径为空。";
            return false;
        }

        if (ContainsRelativeSegment(targetPath))
        {
            reason = "目标路径包含 '.' 或 '..' 片段，已被拒绝。";
            return false;
        }

        if (!normalizer.IsAbsolute(targetPath))
        {
            reason = "目标路径必须是绝对路径。";
            return false;
        }

        foreach (string root in allowedRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            if (normalizer.IsSameOrUnder(targetPath, root))
            {
                return true;
            }
        }

        reason = allowedRoots.Any()
            ? "目标路径不在任何白名单还原根目录之内。"
            : "尚未配置任何还原根目录，还原功能不可用。";
        return false;
    }

    private void ValidateSinglePath(string path, string label, List<DirectoryValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            issues.Add(new DirectoryValidationIssue(CodePathEmpty, $"{label}不能为空。", path, path));
            return;
        }

        if (ContainsRelativeSegment(path))
        {
            issues.Add(new DirectoryValidationIssue(
                CodePathContainsRelativeSegment,
                $"{label}（{path}）包含 '.' 或 '..' 片段，请使用完全限定的路径。",
                path,
                path));
            return;
        }

        if (!_normalizer.IsAbsolute(path))
        {
            issues.Add(new DirectoryValidationIssue(
                CodePathNotAbsolute,
                $"{label}（{path}）必须是绝对路径。",
                path,
                path));
        }
    }

    /// <summary>路径穿越防护。设计文档 05 §8。</summary>
    public static bool ContainsRelativeSegment(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (string seg in path.Split(['\\', '/'], StringSplitOptions.None))
        {
            if (seg == "." || seg == "..")
            {
                return true;
            }
        }

        return false;
    }
}
