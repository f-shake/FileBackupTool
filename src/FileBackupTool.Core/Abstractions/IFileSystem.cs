namespace FileBackupTool.Core.Abstractions;

/// <summary>文件枚举选项。</summary>
public sealed record FileEnumerationOptions
{
    /// <summary>是否递归子目录。</summary>
    public bool RecurseSubdirectories { get; init; } = true;

    /// <summary>无权限的目录/文件是否静默跳过（默认 true，与旧设计一致）。</summary>
    public bool IgnoreInaccessible { get; init; } = true;

    /// <summary>
    /// 要跳过的属性。设计文档 03 §3：**必须为 0**——.NET 默认为 <c>Hidden | System</c>，
    /// 会让隐藏/系统文件被静默排除在备份之外。
    /// </summary>
    public FileAttributes AttributesToSkip { get; init; } = 0;

    public static FileEnumerationOptions Recursive { get; } = new();

    public static FileEnumerationOptions TopOnly { get; } = new() { RecurseSubdirectories = false };
}

/// <summary>一个文件条目的快照信息（不持有打开的句柄）。</summary>
public readonly record struct FileEntry(
    string FullPath,
    long Length,
    DateTime FileTimeUtc,
    FileAttributes Attributes);

/// <summary>一个目录条目的快照信息。</summary>
public readonly record struct DirectoryEntry(
    string FullPath,
    DateTime LastWriteTimeUtc,
    FileAttributes Attributes);

/// <summary>驱动器（用于 <c>/api/fs/roots</c>，以及判定"映射网络盘"）。</summary>
public readonly record struct DriveEntry(string RootPath, DriveType DriveType)
{
    /// <summary>设计文档 03 §5.5：UNC / NAS / **映射盘**一律放弃 watcher，只跑轮询。</summary>
    public bool IsNetwork => DriveType == DriveType.Network;
}

/// <summary>
/// 文件系统抽象。设计文档 06 §2。
/// **不允许**在引擎/监控里直接调用 <c>System.IO.File</c> / <c>Directory</c>——除了本接口的实现内部。
/// </summary>
/// <remarks>
/// 两点契约，实现必须遵守：
/// <list type="number">
/// <item>枚举是**真惰性**的：十万～百万文件必须全程流式，不得物化成 <c>List&lt;FileInfo&gt;</c>
/// （设计文档 01 §4.2）。</item>
/// <item><paramref name="descendInto"/> 为目录剪枝钩子——返回 false 表示整棵子树不进入。
/// 必须由**自己的递归**实现，因为 .NET 的 <c>RecurseSubdirectories</c> 不提供剪枝回调，
/// 而设计文档要求按 exclude 规则剪枝（03 §3）与局部扫描（03 §5.4）。</item>
/// </list>
/// </remarks>
public interface IFileSystem
{
    bool FileExists(string fullPath);

    bool DirectoryExists(string fullPath);

    /// <summary>文件不存在时返回 null（而不是抛异常）。</summary>
    FileEntry? GetFile(string fullPath);

    DirectoryEntry? GetDirectory(string fullPath);

    /// <summary>
    /// 该路径**是否是一个重解析点**（符号链接 / junction），**不跟随**它。
    /// </summary>
    /// <remarks>
    /// 必须与"它是否存在"分开，这是实测出来的（.NET 10 / Windows 10）：
    /// <list type="bullet">
    /// <item>指向不存在目标的**文件**链接：<c>File.Exists</c> / <c>FileInfo.Exists</c> 返回 <c>true</c>，
    /// 且属性里带 <c>ReparsePoint</c> —— 用 <see cref="GetFile"/> 判得到；</item>
    /// <item>指向不存在目标的**目录**链接：<c>Directory.Exists</c> / <c>DirectoryInfo.Exists</c> 返回
    /// <c>false</c>（所以 <see cref="GetDirectory"/> 返回 null），但它**仍然**是重解析点，
    /// <c>Directory.CreateDirectory</c> 与 <c>FileMode.Create</c> 都会跟随它。</item>
    /// </list>
    /// 于是"在白名单还原根里预先摆一个悬空的**目录**链接"就能把写入引到白名单之外，
    /// 而任何基于存在性的检查都看不见它。调用方需要一个不依赖存在性的探针，所以它是独立成员。
    /// </remarks>
    bool IsReparsePoint(string fullPath);

    void CreateDirectory(string fullPath);

    /// <summary>惰性枚举文件。</summary>
    /// <param name="descendInto">目录剪枝回调；null 表示不剪枝。</param>
    IEnumerable<FileEntry> EnumerateFiles(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null);

    /// <summary>惰性枚举子目录（不含 <paramref name="rootFullPath"/> 自身）。</summary>
    IEnumerable<DirectoryEntry> EnumerateDirectories(
        string rootFullPath,
        FileEnumerationOptions? options = null,
        Func<string, bool>? descendInto = null);

    Stream OpenRead(string fullPath);

    /// <summary>创建（或截断）文件用于写入。</summary>
    Stream Create(string fullPath);

    /// <summary>同卷上应为元数据操作（rename）。</summary>
    void MoveFile(string sourceFullPath, string destinationFullPath, bool overwrite);

    void DeleteFile(string fullPath);

    /// <summary>删除目录。目录不存在时静默返回。</summary>
    void DeleteDirectory(string fullPath, bool recursive);

    void SetLastWriteTimeUtc(string fullPath, DateTime lastWriteTimeUtc);

    /// <summary>可浏览的根（驱动器或配置的根）。</summary>
    IReadOnlyList<DriveEntry> GetLogicalDrives();
}

/// <summary>
/// 可选的旁路报告接口：实现方在枚举过程中跳过了某些目录（重解析点 / 无权限）时，
/// 通过它把清单交出来，供引擎写入 <c>BackupLog</c>。
/// </summary>
/// <remarks>
/// 设计文档 03 §7 的立场是"不静默"。重解析点（目录符号链接 / junction）如果不跳过就会**无限递归**，
/// 必须跳过；但跳过这件事本身要让用户看得见，否则就是"显示成功但实际不完整"。
/// </remarks>
public interface IDirectorySkipReporter
{
    /// <summary>取出并清空自上次调用以来被跳过的目录清单。</summary>
    IReadOnlyList<SkippedDirectory> DrainSkippedDirectories();
}

/// <summary>一个被跳过的目录。</summary>
/// <param name="FullPath">被跳过的目录的完整路径。</param>
/// <param name="Reason">为什么跳过（给人看的）。</param>
/// <param name="Deliberate">
/// <c>true</c> = **有意**跳过（目录重解析点：进入它可能无限递归）；
/// <c>false</c> = **失败**跳过（无法枚举：权限、路径过长等）。
/// <para>
/// 这个区分不是为了好看：有意跳过不该把自己算进失败数，否则源目录里放几个 junction
/// 就会让每次备份都显示成"成功（N 个失败）"；而无法枚举是真失败，必须计数。
/// 早先靠 <paramref name="Reason"/> 的字符串前缀来分辨，那等于把一句人话当协议用。
/// </para>
/// </param>
public readonly record struct SkippedDirectory(string FullPath, string Reason, bool Deliberate = false);
