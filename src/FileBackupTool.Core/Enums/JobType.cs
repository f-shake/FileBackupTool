namespace FileBackupTool.Core.Enums;

/// <summary>后台 IO 作业类型。</summary>
public enum JobType
{
    Backup = 0,
    Restore = 1,
    Verify = 2,
    Collect = 3,
}

/// <summary>作业状态。作业队列**不跨重启恢复**（设计文档 01 §4.1），因此这些状态只存在于进程内。</summary>
public enum JobStatus
{
    Queued = 0,
    Running = 1,
    Completed = 2,
    Failed = 3,
    Aborted = 4,
}
