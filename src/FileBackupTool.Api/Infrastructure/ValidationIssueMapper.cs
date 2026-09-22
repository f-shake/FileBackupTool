using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Models;
using FileBackupTool.Core.Paths;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// <see cref="DirectoryValidationIssue"/> → <c>DirValidationIssueDto</c> 的**唯一**映射。
/// </summary>
/// <remarks>
/// <para>
/// <c>POST /api/tasks/validate-dirs</c> 与 <c>POST /api/fs/validate</c> 返回的是同一种东西
/// （都来自 <see cref="DirectoryRelationshipValidator"/>），前端也用同一个组件渲染。
/// 两处各写一份字段翻译，迟早会出现"任务表单能标红、目录选择器标不出来"的分歧。
/// </para>
/// <para>
/// <c>Kind</c> 是给前端分组用的粗分类（"路径本身不合法" / "备份目录在源目录里" …），
/// 它由 <c>Code</c> 派生而非独立定义——**稳定契约是 <c>Code</c>**，
/// <c>Kind</c> 只是让界面不必自己维护一张 code 表。
/// </para>
/// </remarks>
public static class ValidationIssueMapper
{
    public static DirValidationIssueDto ToDto(DirectoryValidationIssue issue) => new(
        issue.Code,
        issue.Message,
        issue.PathA,
        issue.PathB,
        KindOf(issue.Code));

    public static DirValidationResultDto ToResult(IReadOnlyList<DirectoryValidationIssue> issues) => new(
        issues.Count == 0,
        [.. issues.Select(ToDto)]);

    public static string KindOf(string code) => code switch
    {
        DirectoryRelationshipValidator.CodePathEmpty => "path",
        DirectoryRelationshipValidator.CodePathNotAbsolute => "path",
        DirectoryRelationshipValidator.CodePathContainsRelativeSegment => "path",
        DirectoryRelationshipValidator.CodeSameSourceAndBackup => "source_backup",
        DirectoryRelationshipValidator.CodeBackupInsideSource => "backup_inside_source",
        DirectoryRelationshipValidator.CodeSourceInsideBackup => "source_inside_backup",
        DirectoryRelationshipValidator.CodeBackupDirShared => "backup_shared",
        DirectoryRelationshipValidator.CodeBackupDirsOverlap => "backup_overlap",
        DirectoryRelationshipValidator.CodeRestoreRootInsideBackup => "restore_root",
        DirectoryRelationshipValidator.CodeBackupInsideRestoreRoot => "restore_root",
        _ => "other",
    };
}
