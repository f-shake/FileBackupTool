using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Entities;
using FileBackupTool.Core.Paths;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 路径归一化、长路径、目录关系校验。设计文档 02 §3.2 / §7、01 §6.1。
/// </summary>
public class PathTests
{
    private static readonly IPathNormalizer N = WindowsPathNormalizer.Instance;
    private static readonly DirectoryRelationshipValidator Validator = new(N);

    // ------------------------------------------------------------------
    // 归一化
    // ------------------------------------------------------------------

    [Fact]
    public void Windows归一化统一分隔符并小写()
    {
        Assert.Equal(@"c:\src\img.jpg", N.Normalize(@"C:/SRC/IMG.JPG"));
    }

    [Fact]
    public void 归一化去掉尾部多余分隔符但保留盘根()
    {
        Assert.Equal(@"c:\src", N.Normalize(@"C:\src\"));
        Assert.Equal(@"c:\", N.Normalize(@"C:\"));
        Assert.Equal(@"c:\", N.Normalize(@"C:\\\"));
    }

    [Fact]
    public void Windows路径比较不区分大小写()
    {
        Assert.True(N.AreEqual(@"C:\Src\A.TXT", @"c:\src\a.txt"));
        Assert.False(N.AreEqual(@"C:\src", @"C:\src2"));
    }

    [Fact]
    public void 子树判定按段边界不会被前缀误匹配()
    {
        Assert.True(N.IsSameOrUnder(@"C:\src\sub\a.txt", @"C:\src"));
        Assert.True(N.IsSameOrUnder(@"C:\src", @"C:\src"));
        // "C:\src2" 不是 "C:\src" 的子树——纯字符串前缀比较会在这里出错
        Assert.False(N.IsSameOrUnder(@"C:\src2\a.txt", @"C:\src"));
    }

    [Fact]
    public void 相对路径计算保留原始大小写()
    {
        Assert.Equal(@"Sub\File.TXT", N.GetRelativePathOrNull(@"C:\Src", @"C:\src\Sub\File.TXT"));
        Assert.Equal(string.Empty, N.GetRelativePathOrNull(@"C:\src", @"C:\SRC\"));
        Assert.Null(N.GetRelativePathOrNull(@"C:\src", @"D:\other\a.txt"));
    }

    [Fact]
    public void 拼接路径处理盘根()
    {
        Assert.Equal(@"C:\src\a.txt", N.Combine(@"C:\src", "a.txt"));
        Assert.Equal(@"C:\a.txt", N.Combine(@"C:\", "a.txt"));
        Assert.Equal(@"C:\b", N.Combine(@"C:\a", @"C:\b"));
    }

    [Fact]
    public void 目录名与文件名()
    {
        Assert.Equal("a.txt", N.GetFileName(@"C:\src\a.txt"));
        Assert.Equal(@"C:\src", N.GetDirectoryName(@"C:\src\a.txt"));
        Assert.Equal(@"C:\", N.GetDirectoryName(@"C:\a.txt"));
    }

    // ------------------------------------------------------------------
    // 长路径（设计文档 01 §6.1）
    // ------------------------------------------------------------------

    [Fact]
    public void 短路径不加扩展前缀()
    {
        // \\?\ 会关闭路径规范化（.. 与正斜杠失效）且部分网络重定向器不认，
        // 所以只在确实需要时才加。
        Assert.Equal(@"C:\src\a.txt", N.ToExtendedPath(@"C:\src\a.txt"));
    }

    [Fact]
    public void 超长路径加扩展前缀()
    {
        string longPath = @"C:\" + new string('a', WindowsPathNormalizer.ExtendedPathThreshold + 10);
        string extended = N.ToExtendedPath(longPath);
        Assert.StartsWith(@"\\?\", extended, StringComparison.Ordinal);
        Assert.EndsWith(new string('a', 10), extended, StringComparison.Ordinal);
    }

    [Fact]
    public void 超长UNC路径用扩展UNC前缀()
    {
        string unc = @"\\server\share\" + new string('b', WindowsPathNormalizer.ExtendedPathThreshold + 10);
        string extended = N.ToExtendedPath(unc);
        Assert.StartsWith(@"\\?\UNC\", extended, StringComparison.Ordinal);
    }

    [Fact]
    public void 网络路径识别()
    {
        Assert.True(N.IsNetworkPath(@"\\nas\backup"));
        Assert.True(N.IsNetworkPath(@"\\?\UNC\nas\backup"));
        Assert.False(N.IsNetworkPath(@"C:\backup"));
        // \\?\C:\ 是本地扩展路径，不是网络路径
        Assert.False(N.IsNetworkPath(@"\\?\C:\backup"));
    }

    [Fact]
    public void 相对路径无法加扩展前缀()
    {
        Assert.Equal("a.txt", N.ToExtendedPath("a.txt"));
    }

    // ------------------------------------------------------------------
    // 目录关系校验（设计文档 02 §7 的五条规则）
    // ------------------------------------------------------------------

    [Fact]
    public void 源目录与备份目录相同被拒()
    {
        var issues = Validator.ValidateTask(@"C:\data", @"C:\data", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeSameSourceAndBackup);
    }

    [Fact]
    public void 备份目录位于源目录之内被拒()
    {
        // 这是最容易犯、后果最严重的配置错误：会与实时监控叠加成自我触发的死循环
        var issues = Validator.ValidateTask(@"C:\data", @"C:\data\backup", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeBackupInsideSource);
    }

    [Fact]
    public void 源目录位于备份目录之内被拒()
    {
        var issues = Validator.ValidateTask(@"C:\backup\data", @"C:\backup", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeSourceInsideBackup);
    }

    [Fact]
    public void 两个任务共用备份目录被拒()
    {
        var other = new BackupTask { Name = "另一个", SourceDir = @"D:\x", BackupDir = @"C:\bak" };
        var issues = Validator.ValidateTask(@"C:\data", @"C:\BAK", [other], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeBackupDirShared);
    }

    [Fact]
    public void 两个任务的备份目录互相包含被拒()
    {
        var other = new BackupTask { Name = "另一个", SourceDir = @"D:\x", BackupDir = @"C:\bak" };
        var issues = Validator.ValidateTask(@"C:\data", @"C:\bak\nested", [other], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeBackupDirsOverlap);
    }

    [Fact]
    public void 还原根位于备份目录之内被拒()
    {
        var issues = Validator.ValidateTask(@"C:\data", @"C:\bak", [], [@"C:\bak\restore"]);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeRestoreRootInsideBackup);
    }

    [Fact]
    public void 备份目录位于还原根之内也被拒()
    {
        // 文档 02 §7 的 5 条没有这条，是刻意补的：一次"整快照还原到该根"就可能覆盖 blob
        var issues = Validator.ValidateTask(@"C:\data", @"C:\restore\bak", [], [@"C:\restore"]);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodeBackupInsideRestoreRoot);
    }

    [Fact]
    public void 非绝对路径被拒()
    {
        var issues = Validator.ValidateTask(@"data", @"C:\bak", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodePathNotAbsolute);
    }

    [Fact]
    public void 含相对片段的路径被拒()
    {
        var issues = Validator.ValidateTask(@"C:\data\..\other", @"C:\bak", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodePathContainsRelativeSegment);
    }

    [Fact]
    public void 空路径被拒()
    {
        var issues = Validator.ValidateTask(string.Empty, @"C:\bak", [], []);
        Assert.Contains(issues, i => i.Code == DirectoryRelationshipValidator.CodePathEmpty);
    }

    [Fact]
    public void 合法配置无冲突()
    {
        var other = new BackupTask { Name = "另一个", SourceDir = @"D:\x", BackupDir = @"E:\bak2" };
        var issues = Validator.ValidateTask(@"C:\data", @"C:\bak", [other], [@"D:\restore"]);
        Assert.Empty(issues);
    }

    [Fact]
    public void 允许一个源目录被多个任务备份()
    {
        // 决策：源目录不设唯一约束，只有备份目录唯一
        var other = new BackupTask { Name = "另一个", SourceDir = @"C:\data", BackupDir = @"E:\bak2" };
        var issues = Validator.ValidateTask(@"C:\data", @"C:\bak", [other], []);
        Assert.Empty(issues);
    }

    [Fact]
    public void 相邻兄弟目录不算冲突()
    {
        var other = new BackupTask { Name = "另一个", SourceDir = @"D:\x", BackupDir = @"C:\bak2" };
        var issues = Validator.ValidateTask(@"C:\data", @"C:\bak", [other], []);
        Assert.Empty(issues);
    }

    // ------------------------------------------------------------------
    // 还原根白名单（设计文档 04 §1.3）
    // ------------------------------------------------------------------

    [Fact]
    public void 目标在白名单内被接受()
    {
        Assert.True(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\restore\sub\a.txt", [@"C:\restore"], out _));
    }

    [Fact]
    public void 目标等于白名单根被接受()
    {
        Assert.True(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\restore", [@"C:\restore"], out _));
    }

    [Fact]
    public void 目标在白名单外被拒绝()
    {
        Assert.False(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\Windows\System32\evil.dll", [@"C:\restore"], out string? reason));
        Assert.NotNull(reason);
    }

    [Fact]
    public void 空白名单一律拒绝()
    {
        Assert.False(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\restore\a.txt", [], out string? reason));
        Assert.Contains("尚未配置", reason);
    }

    [Fact]
    public void 含相对片段的目标被拒绝_穿越防护()
    {
        Assert.False(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\restore\..\Windows\evil.dll", [@"C:\restore"], out _));
    }

    [Fact]
    public void 前缀相似的兄弟目录不算在白名单内()
    {
        Assert.False(DirectoryRelationshipValidator.IsWithinAllowedRoots(
            N, @"C:\restore2\a.txt", [@"C:\restore"], out _));
    }
}
