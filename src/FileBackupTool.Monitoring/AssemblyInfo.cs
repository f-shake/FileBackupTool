using System.Runtime.CompilerServices;

// 待定表 / 合并窗口 / 事件队列都是内部类型：它们不是对外契约（对外只有 IChangeMonitor），
// 但单元测试需要直接驱动它们——尤其是"静默期"这种必须用可推进的假时钟精确构造的场景，
// 只从 ChangeMonitor 的公开 API 是测不出来的。
[assembly: InternalsVisibleTo("FileBackupTool.UnitTests")]
