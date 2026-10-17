using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FileBackupTool.Core.IO;

/// <summary>
/// 给文件句柄设置 Windows 的 IO 优先级提示（<c>IoPriorityHintVeryLow</c>）。
/// </summary>
/// <remarks>
/// <b>要解决什么</b>：备份读源盘、用户同时往同一块盘里写（合成视频、拷照片）时，
/// 两股 IO 在同一个队列里排队，用户的写入被备份的读取拖慢。
/// <para>
/// 这里选的是**句柄级**的 IO 优先级，而不是 <c>SetPriorityClass(PROCESS_MODE_BACKGROUND_BEGIN)</c>：
/// 后者会把整个进程（含 Web API 与日志）一并降成后台，界面跟着变卡。句柄级只影响这一条流，
/// 而且语义正好是"只在争用时让路"——盘空闲时备份照样跑满，不需要用户调任何速率参数。
/// </para>
/// <para>
/// **失败必须是静默的**：非 Windows、文件系统不支持、句柄不允许 —— 都只是"没有加速让路"，
/// 与备份正确性无关。第一次失败后记住结论，之后不再尝试，避免每个文件都调一次系统调用。
/// </para>
/// </remarks>
public static class IoPriority
{
    /// <summary>0 = 尚未失败过；1 = 已确认不可用。用 Interlocked 维护，避免重复尝试。</summary>
    private static int _unavailable;

    private static int _applied;

    /// <summary>本进程是否还能尝试设置 IO 优先级。</summary>
    public static bool IsAvailable =>
        OperatingSystem.IsWindows() && Volatile.Read(ref _unavailable) == 0;

    /// <summary>
    /// 尝试把一个已打开的句柄降为最低 IO 优先级。
    /// </summary>
    /// <returns>是否真的设置成功。失败不影响调用方功能。</returns>
    /// <remarks>
    /// **这个成员永不抛异常**，包括非 Windows 平台（`kernel32.dll` 根本不存在）、
    /// 文件系统不支持、句柄权限不足。理由：它只是"让路"这一层优化，
    /// 备份的正确性不依赖它——若它能把一次备份搞崩，那才是真的错了。
    /// </remarks>
    public static bool TryApplyBackground(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            MarkUnavailable();
            return false;
        }

        if (Volatile.Read(ref _unavailable) != 0)
        {
            return false;
        }

        try
        {
            return ApplyCore(handle);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 入口点缺失、封送失败、DllImport 解析不到…… 都归为"这台机器没有这个能力"。
            // 故意不往上抛：见 remarks。
            MarkUnavailable();
            return false;
        }
    }

    private static bool ApplyCore(SafeFileHandle handle)
    {
        // `FILE_IO_PRIORITY_HINT_INFO` 只有 4 字节，而这个信息类要求缓冲区**8 字节对齐**。
        // 托管栈上的 `ref` 局部是否 8 字节对齐取决于 JIT 的布局，语言层面不保证，
        // 不对齐时会得到 ERROR_NOACCESS(998)。所以显式要一块必然对齐的非托管内存。
        IntPtr buffer = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(buffer, 0L);
            Marshal.WriteInt32(buffer, 0, (int)FileIoPriorityHint.VeryLow);

            bool ok = SetFileInformationByHandle(
                handle,
                FileInfoByHandleClass.FileIoPriorityHintInfo,
                buffer,
                (uint)Marshal.SizeOf<FileIoPriorityHintInfo>());

            if (ok)
            {
                Interlocked.Exchange(ref _applied, 1);
                return true;
            }

            // 任何失败都记住：这代表"这台机器/这个句柄拿不到这个能力"，
            // 不是可以逐文件重试的偶发错误（998 / 5 这类尤其多）。
            // 不记住的话，每个文件都会白调一次系统调用。
            MarkUnavailable();
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>是否至少成功设置过一次（供自检/诊断使用）。</summary>
    public static bool HasEverApplied => Volatile.Read(ref _applied) != 0;

    private static void MarkUnavailable() =>
        Interlocked.CompareExchange(ref _unavailable, 1, 0);

    private enum FileInfoByHandleClass
    {
        FileIoPriorityHintInfo = 12,
    }

    private enum FileIoPriorityHint
    {
        VeryLow = 0,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIoPriorityHintInfo
    {
        public FileIoPriorityHint PriorityHint;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        FileInfoByHandleClass fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);
}
