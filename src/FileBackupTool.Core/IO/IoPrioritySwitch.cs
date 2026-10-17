namespace FileBackupTool.Core.IO;

/// <summary>
/// 运行时开关：备份读源文件时是否申请低 IO 优先级。默认开启。
/// </summary>
/// <remarks>
/// 取值以数据库设置为准（<c>Io.LowPriorityRead</c>），配置里的
/// <c>Jobs:LowPriorityIo</c> 只做首次播种 —— 与本项目保留策略的约定一致。
/// <para>
/// 用 <c>volatile</c> 字段而不是锁：读写都是一次 bool，唯一的用途是"改了立刻生效"，
/// 不需要跨字段一致性。设置在设置页改动后由 Api 直接赋值。
/// </para>
/// </remarks>
public sealed class IoPrioritySwitch(bool enabled = true)
{
    private volatile bool _enabled = enabled;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}
