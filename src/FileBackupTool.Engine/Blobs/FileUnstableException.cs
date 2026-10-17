namespace FileBackupTool.Engine.Blobs;

/// <summary>
/// 源文件在读取过程中发生了变化（长度或最后写入时间），本次取得的 blob 不可信。
/// </summary>
/// <remarks>
/// <b>为什么必须作废，而不是"尽力而为"</b>：blob 的名字就是内容的哈希，
/// 而哈希是按**实际读到的那些字节**算出来的 —— 一个只读到一半的 mp4 会得到一个
/// 与自身完全自洽的 blob。内容寻址的全部完整性校验（重算哈希、比对文件名）
/// 都会说它"没问题"，只有回头看源文件的长度才看得见问题。
/// <para>
/// 一旦固化，这个截断内容会被 as-if 合法地写进文件记录，还原出来就是一个坏文件，
/// 而界面上显示"可恢复"。所以宁可这一轮不备份，也不能存一个自洽的谎言。
/// </para>
/// <para>
/// 它不是失败：文件本身没坏，只是"还没写完"。调用方应按**跳过**处理，
/// 不计入失败率、不触发中止，下一轮重试。
/// </para>
/// </remarks>
public sealed class FileUnstableException(string path, string detail)
    : Exception($"文件在读取期间仍在变化，本轮跳过：{path}（{detail}）")
{
    public string Path { get; } = path;

    public string Detail { get; } = detail;
}
