namespace FileBackupTool.Core.Enums;

/// <summary>
/// 文件记录类型。设计文档 02 §2.3。
/// </summary>
public enum FileRecordType
{
    /// <summary>新增。</summary>
    Created = 0,

    /// <summary>修改。</summary>
    Modified = 1,

    /// <summary>删除墓碑。此类型的 <c>ContentHash</c> 恒为 null。</summary>
    Deleted = 2,
}
