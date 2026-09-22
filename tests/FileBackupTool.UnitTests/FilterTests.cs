using FileBackupTool.Core.Abstractions;
using FileBackupTool.Core.Filtering;
using FileBackupTool.Core.Paths;
using Xunit;

namespace FileBackupTool.UnitTests;

/// <summary>
/// 筛选规则。语义沿用 <c>FzLib.IO.FileFilterRule</c>，见决策 #1。
/// </summary>
/// <remarks>
/// 这里断言的是<b>实测出来的</b>语义，不是想当然的语义。三条最容易搞错的：
/// <list type="number">
/// <item><c>IsMatched(path, isFile)</c> 的第二参<b>默认 true = 文件</b>；传 false 才是目录，
/// 且目录模式下<b>路径最后一段也参与文件夹名匹配</b>。</item>
/// <item>多值分隔符是 <c>Environment.NewLine</c>（Windows 上是 <c>\r\n</c>），
/// <b>不是</b>分号或逗号。</item>
/// <item><c>IsEnabled</c> 由<b>调用方</b>判断，<c>FileFilterHelper</c> 自己不查。</item>
/// </list>
/// </remarks>
public class FilterTests
{
    private static readonly IPathNormalizer N = WindowsPathNormalizer.Instance;

    private const string Root = @"C:\source";

    private static FzLibFileFilter Filter(FilterRuleSet rule) => new(rule, N);

    private static string P(string relative) => N.Combine(Root, relative);

    [Fact]
    public void 规则未启用时全部包含()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = false, IncludeFiles = "*.txt" });
        Assert.True(filter.IncludeFile(P(@"a.jpg")));
        Assert.True(filter.DescendInto(P("anything")));
    }

    [Fact]
    public void 空规则全部包含()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true });
        Assert.True(filter.IncludeFile(P(@"a.jpg")));
        Assert.True(filter.IncludeFile(P(@"sub\a.txt")));
    }

    [Fact]
    public void 仅按文件名包含时不匹配的其他文件被排除()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, IncludeFiles = "*.txt" });

        Assert.True(filter.IncludeFile(P("a.txt")));
        Assert.True(filter.IncludeFile(P(@"sub\c.txt")));
        Assert.False(filter.IncludeFile(P("b.jpg")));
    }

    [Fact]
    public void 包含规则绝不剪枝目录()
    {
        // 这是"包含规则"型过滤器的经典陷阱：IncludeFolders=photos 时
        // C:\source 本身不匹配 include，若据此剪枝就永远走不到 C:\source\photos。
        var filter = Filter(new FilterRuleSet { IsEnabled = true, IncludeFolders = "photos" });
        Assert.True(filter.DescendInto(Root));
        Assert.True(filter.DescendInto(P("other")));
    }

    [Fact]
    public void 包含文件夹时该子树任意深度都被包含()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, IncludeFolders = "photos" });

        Assert.True(filter.IncludeFile(P(@"photos\d.jpg")));
        Assert.True(filter.IncludeFile(P(@"photos\2026\01\e.jpg")));
        Assert.False(filter.IncludeFile(P(@"other\a.txt")));
        Assert.False(filter.IncludeFile(P("root-file.txt")));
    }

    [Fact]
    public void 排除文件名生效()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, ExcludeFiles = "*.jpg" });

        Assert.False(filter.IncludeFile(P("b.jpg")));
        Assert.True(filter.IncludeFile(P("a.txt")));
    }

    [Fact]
    public void 排除文件名不会剪枝目录()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, ExcludeFiles = "*.jpg" });
        Assert.True(filter.DescendInto(P("photos")));
    }

    [Fact]
    public void 排除文件夹时整棵子树被剪枝()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, ExcludeFolders = "skip" });

        Assert.False(filter.DescendInto(P("skip")));
        Assert.False(filter.IncludeFile(P(@"skip\f.txt")));
        Assert.True(filter.DescendInto(P("keep")));
    }

    [Fact]
    public void 多值用换行分隔()
    {
        var filter = Filter(new FilterRuleSet
        {
            IsEnabled = true,
            IncludeFiles = "*.txt" + Environment.NewLine + "*.jpg",
        });

        Assert.True(filter.IncludeFile(P("a.txt")));
        Assert.True(filter.IncludeFile(P("b.jpg")));
        Assert.False(filter.IncludeFile(P("c.png")));
    }

    [Fact]
    public void 分号不是多值分隔符()
    {
        // 实测确认：FileFilterHelper 用的是 Environment.NewLine。
        // 把这条行为固化下来，免得以后有人"顺手"改成支持分号而破坏与旧配置的兼容。
        var filter = Filter(new FilterRuleSet { IsEnabled = true, IncludeFiles = "*.txt;*.jpg" });

        Assert.False(filter.IncludeFile(P("a.txt")));
        Assert.False(filter.IncludeFile(P("b.jpg")));
    }

    [Fact]
    public void 排除路径需要通配符才能覆盖子树()
    {
        var exact = Filter(new FilterRuleSet { IsEnabled = true, ExcludePaths = @"C:\source\skip" });
        Assert.True(exact.IncludeFile(P(@"skip\f.txt")));

        var wildcard = Filter(new FilterRuleSet { IsEnabled = true, ExcludePaths = @"C:\source\skip\*" });
        Assert.False(wildcard.IncludeFile(P(@"skip\f.txt")));
    }

    [Fact]
    public void 正则模式()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, UseRegex = true, IncludeFiles = @"^.*\.txt$" });

        Assert.True(filter.IncludeFile(P("a.txt")));
        Assert.False(filter.IncludeFile(P("b.jpg")));
    }

    [Fact]
    public void Windows下文件名匹配不区分大小写()
    {
        var filter = Filter(new FilterRuleSet { IsEnabled = true, IncludeFiles = "*.txt" });
        Assert.True(filter.IncludeFile(P("A.TXT")));
    }

    // ------------------------------------------------------------------
    // 硬排除（设计文档 03 §3 + 02 §8）
    // ------------------------------------------------------------------

    [Fact]
    public void 硬排除回收站目录()
    {
        IFileFilter filter = new HardExcludeFileFilter(NullFileFilter.Instance, N, []);
        Assert.False(filter.DescendInto(@"C:\source\$RECYCLE.BIN"));
        Assert.False(filter.IncludeFile(@"C:\source\$RECYCLE.BIN\S-1-5-18\file.dat"));
        Assert.True(filter.DescendInto(@"C:\source\keep"));
    }

    [Fact]
    public void 硬排除配置的前缀()
    {
        IFileFilter filter = new HardExcludeFileFilter(NullFileFilter.Instance, N, [@"C:\source\backup"]);
        Assert.False(filter.DescendInto(@"C:\source\backup"));
        Assert.False(filter.IncludeFile(@"C:\source\backup\blobs\x"));
        Assert.True(filter.DescendInto(@"C:\source\backup2"));
    }

    // ------------------------------------------------------------------
    // 规则持久化
    // ------------------------------------------------------------------

    [Fact]
    public void 规则可以往返序列化()
    {
        var original = new FilterRuleSet
        {
            IsEnabled = true,
            UseRegex = false,
            IncludeFiles = "*.txt",
            ExcludeFolders = "temp",
        };

        FilterRuleSet parsed = FilterRuleSet.FromJson(original.ToJson());
        Assert.True(parsed.IsEnabled);
        Assert.Equal("*.txt", parsed.IncludeFiles);
        Assert.Equal("temp", parsed.ExcludeFolders);
    }

    [Fact]
    public void 非法JSON退化为空规则而不是抛异常()
    {
        Assert.False(FilterRuleSet.FromJson("{ 这不是 json").IsEnabled);
        Assert.False(FilterRuleSet.FromJson(null).IsEnabled);
        Assert.False(FilterRuleSet.FromJson(string.Empty).IsEnabled);
    }
}
