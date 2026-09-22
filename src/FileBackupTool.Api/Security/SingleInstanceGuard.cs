using System.Diagnostics;
using System.Text;

namespace FileBackupTool.Api.Security;

/// <summary>
/// 强制单实例（设计文档 01 §6：**必须**，因为数据目录不能被两个进程同时写）。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么两套机制都用</b>：
/// <list type="number">
/// <item><b>数据目录独占锁文件</b>（<c>&lt;DataDir&gt;\.lock</c>，<c>FileShare.None</c>）——
/// 这是**真正重要**的那一层。两个进程同时开同一个 SQLite 库、同时往同一个 blob 池写，
/// 会直接损坏备份数据。锁文件与数据目录同生共死，天然精确。</item>
/// <item><b>全局命名 Mutex</b>——设计文档明确点名的机制，覆盖"两个实例指向不同数据目录"
/// 的情形（仍然危险：会争抢同一批源目录的 IO，并且两份日志谁也说不清发生了什么）。</item>
/// </list>
/// </para>
/// <para>
/// <b>为什么用 <c>WaitOne(0)</c> 而不是 <c>createdNew</c></b>：命名 Mutex 只要**存在**
/// （哪怕当前无人持有），<c>new Mutex(...)</c> 的 <c>createdNew</c> 就是 false。
/// 换句话说 <c>createdNew == false</c> 并不代表"别人正在运行"，
/// 只代表"这个名字被创建过"。唯一可靠的判据是"能不能立刻拿到它"。
/// </para>
/// <para>
/// <b><c>Global\</c> 拿不到权限时不能因此拒绝启动</b>：创建全局内核对象需要
/// <c>SeCreateGlobalPrivilege</c>，普通用户在某些策略下没有。那种情况下数据目录锁文件
/// 仍然有效，功能不受影响——不能因为一个"尽力而为"的第二道防线而让服务起不来。
/// </para>
/// </remarks>
public sealed class SingleInstanceGuard : IDisposable
{
    /// <summary>与 <c>AppPaths.MutexName</c> 一致的全局名字。</summary>
    private const string GlobalMutexName = @"Global\FileBackupTool.Api.SingleInstance";

    private const string LocalMutexName = @"FileBackupTool.Api.SingleInstance";

    private Mutex? _mutex;
    private FileStream? _lockFile;

    /// <summary>数据的目录锁是否真的拿到了（拿不到就不能启动）。</summary>
    public bool HoldsDataDirLock => _lockFile is not null;

    /// <summary>
    /// 尝试成为唯一实例。
    /// </summary>
    /// <param name="dataDir">数据目录（锁文件放在这里）。</param>
    /// <param name="warning">非致命问题（例如全局 Mutex 没权限创建）。</param>
    /// <param name="conflict">致命冲突的人话描述；为 null 表示拿到了。</param>
    public static SingleInstanceGuard Acquire(
        string dataDir,
        out string? warning,
        out string? conflict)
    {
        var guard = new SingleInstanceGuard();
        warning = null;
        conflict = null;

        // ---- ① 全局命名 Mutex（尽力而为）----
        foreach (string name in new[] { GlobalMutexName, LocalMutexName })
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: false, name);
                if (mutex.WaitOne(0))
                {
                    guard._mutex = mutex;
                    break;
                }

                // 拿不到 → 已经有实例持有它。
                mutex.Dispose();
                conflict =
                    $"已有另一个 FileBackupTool 实例正在运行（未能获得全局互斥体 {name}）。\n" +
                    "本工具**强制单实例**：并发写同一个数据目录会损坏备份数据。\n" +
                    "如果确认没有实例在运行（例如上次被强杀），请重新启动；" +
                    "若一直失败，请重启系统以释放内核对象。";
                return guard;
            }
            catch (UnauthorizedAccessException ex)
            {
                // 权限不足：Global\ 在当前会话下不可用。退到会话内名字，仍失败就只靠锁文件。
                warning = $"无法创建命名互斥体 {name}：{ex.Message}（将只依赖数据目录锁文件）";
            }
        }

        // ---- ② 数据目录独占锁文件（关键防线）----
        string lockPath = Path.Combine(dataDir, ".lock");

        try
        {
            guard._lockFile = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 512,
                FileOptions.WriteThrough);

            // 写下 pid 与启动时间，让"到底是谁占着"可查。
            byte[] payload = Encoding.UTF8.GetBytes(
                $"pid={Environment.ProcessId}{Environment.NewLine}" +
                $"started={DateTime.UtcNow:u}{Environment.NewLine}");

            guard._lockFile.SetLength(0);
            guard._lockFile.Write(payload, 0, payload.Length);
            guard._lockFile.Flush();

            return guard;
        }
        catch (IOException ex)
        {
            // 被占用 = 另一个实例正在用同一个数据目录。
            conflict =
                $"数据目录已被另一个 FileBackupTool 实例占用：{dataDir}\n" +
                $"（无法独占打开 {lockPath}：{ex.Message}）\n" +
                "本工具**强制单实例**。请先关闭另一个实例（或在任务管理器里结束它的进程）再启动。";
            return guard;
        }
        catch (UnauthorizedAccessException ex)
        {
            conflict =
                $"没有权限在数据目录里创建锁文件：{lockPath}\n（{ex.Message}）\n" +
                "请确认运行账户对该目录有写权限。";
            return guard;
        }
    }

    public void Dispose()
    {
        if (_lockFile is not null)
        {
            string? path = null;

            try
            {
                path = _lockFile.Name;
                _lockFile.Dispose();
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"释放锁文件失败：{ex.Message}");
            }
            finally
            {
                _lockFile = null;
            }

            try
            {
                if (path is not null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 删不掉无所谓：下次启动仍会以 FileShare.None 打开并检查可独占性。
                Debug.WriteLine($"删除锁文件失败：{ex.Message}");
            }
        }

        if (_mutex is not null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex 是**线程亲和**的：top-level 语句里的 await 会让续体跑在别的线程池线程上，
                // 于是"释放"可能不在持有它的那个线程上发生。这不影响正确性——
                // 进程退出时内核会自动释放，锁文件的释放才是我们能控制的那一层。
            }

            _mutex.Dispose();
            _mutex = null;
        }
    }
}
