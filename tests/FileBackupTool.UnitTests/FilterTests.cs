using System.Text.Json;
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

    // ------------------------------------------------------------------
    // 规则校验（FilterRuleValidator）
    //
    // 2026-10-01 的缺陷（界面配了筛选却不生效）的回归防线：
    // 原实现把"读不懂的规则"静默当成"不过滤"，这里锁死"必须报错"。
    // ------------------------------------------------------------------

    /// <summary>
    /// 与前端 <c>serializeFilterRule</c> 产出的文本**逐字一致**（多行用 <c>\r\n</c> 转义，
    /// 字段名 PascalCase、顺序固定）。改动前端序列化必须同步这条常量。
    /// </summary>
    private const string FrontendProducedJson =
        """{"IsEnabled":true,"UseRegex":false,"IncludeFiles":"*.txt\r\n*.jpg","IncludeFolders":"","IncludePaths":"","ExcludeFiles":"","ExcludeFolders":".git","ExcludePaths":""}""";

    [Fact]
    public void 前端产出的扁平JSON被接受且多行条件真的生效()
    {
        FilterRuleValidationResult result = FilterRuleValidator.Validate(FrontendProducedJson);

        Assert.True(result.Ok, result.ErrorDetail);
        Assert.Equal("*.txt\r\n*.jpg", result.Rule!.IncludeFiles);
        Assert.Equal(".git", result.Rule.ExcludeFolders);

        // 光"能解析"不算数：必须真的在两行上都命中，且黑名单真的剪枝。
        FzLibFileFilter filter = Filter(result.Rule);
        Assert.True(filter.IncludeFile(P("a.txt")));
        Assert.True(filter.IncludeFile(P("b.jpg")));
        Assert.False(filter.IncludeFile(P("c.png")));
        Assert.False(filter.DescendInto(P(".git")));
    }

    [Fact]
    public void 认不出的形状必须报错而不是退化为不过滤()
    {
        // 就是历史上前端写进库的那种嵌套形状。
        const string legacy =
            """{"whitelist":{"file":"*.docx","folder":"","path":""},"blacklist":{"file":"","folder":"","path":""},"useRegex":false}""";

        FilterRuleValidationResult result = FilterRuleValidator.Validate(legacy);

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.UnknownShape, result.Error);
        Assert.Contains("whitelist", result.ErrorDetail!);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{}")]
    [InlineData("{ }")]
    public void 空输入等价于没有规则(string? json)
    {
        FilterRuleValidationResult result = FilterRuleValidator.Validate(json);

        Assert.True(result.Ok);
        Assert.Equal("{}", result.Normalized);
        Assert.True(result.Rule!.IsEffectivelyEmpty);
    }

    [Fact]
    public void 不是合法JSON时报错()
    {
        FilterRuleValidationResult result = FilterRuleValidator.Validate("{ 这不是 json");

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.NotJson, result.Error);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"txt\"")]
    [InlineData("42")]
    public void 不是JSON对象时报错(string json)
    {
        Assert.Equal(FilterRuleError.NotObject, FilterRuleValidator.Validate(json).Error);
    }

    [Fact]
    public void 已知字段的类型不对时报错()
    {
        FilterRuleValidationResult result = FilterRuleValidator.Validate("""{"UseRegex":"yes"}""");

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.InvalidType, result.Error);
    }

    [Fact]
    public void 正则模式下编译不过的表达式被拒绝并指出字段()
    {
        // `*.txt` 是通配符写法：当正则用时 `*` 前面没有可重复的东西，编译就失败。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"IsEnabled":true,"UseRegex":true,"IncludeFiles":"*.txt"}""");

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.InvalidRegex, result.Error);
        Assert.Equal(nameof(FilterRuleSet.IncludeFiles), result.ErrorField);
    }

    [Fact]
    public void 正则模式下合法的表达式放行()
    {
        // JSON 文本里 `\\.` 才是"正则里的 \."——JSON 本身不允许 `\.` 这种转义。
        Assert.True(FilterRuleValidator
            .Validate("""{"IsEnabled":true,"UseRegex":true,"IncludeFiles":"^.*\\.txt$"}""").Ok);
    }

    [Fact]
    public void 通配符模式下不做正则校验()
    {
        // 同样是 *.txt，通配符语义下完全正常——切换开关不该把正常配置拦下来。
        Assert.True(FilterRuleValidator
            .Validate("""{"IsEnabled":true,"UseRegex":false,"IncludeFiles":"*.txt"}""").Ok);
    }

    [Fact]
    public void 超过长度上限时报错()
    {
        string json = "{\"IncludeFiles\":\"" + new string('x', FilterRuleValidator.MaxLength) + "\"}";

        FilterRuleValidationResult result = FilterRuleValidator.Validate(json);

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.TooLong, result.Error);
    }

    [Fact]
    public void 混进未知字段时整条拒绝而不是悄悄丢掉它()
    {
        // 未知字段意味着"用户意图里有一部分引擎执行不了"；悄悄丢掉同样是静默退化。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"IncludeFiles":"*.txt","SomethingElse":42}""");

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.UnknownShape, result.Error);
        Assert.Contains("SomethingElse", result.ErrorDetail!);
    }

    [Fact]
    public void 大小写混用的字段被归一化成PascalCase()
    {
        // 引擎侧的 FilterRuleSet.FromJson 区分大小写；不归一化等于把字段读丢——
        // 又一种"看起来保存成功、实际不生效"。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"isEnabled":true,"includeFiles":"*.txt"}""");

        Assert.True(result.Ok);
        Assert.True(FilterRuleSet.FromJson(result.Normalized).IsEnabled);
        Assert.Equal("*.txt", FilterRuleSet.FromJson(result.Normalized).IncludeFiles);
    }

    [Fact]
    public void 空规则归一化成空对象以保住HasFilter的约定()
    {
        // BackupTask.HasFilter 判断的是 `FilterJson != "{}"`；归一化若写成
        // "字段齐全但都是空"的 JSON，任务列表会永远显示"有筛选"。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"IsEnabled":false,"IncludeFiles":"  "}""");

        Assert.True(result.Ok);
        Assert.Equal("{}", result.Normalized);
    }

    [Fact]
    public void 归一化输出只含约定的八个字段()
    {
        // 曾经漏掉过：IsEffectivelyEmpty 是公开的计算属性，System.Text.Json 默认会
        // 把它一起序列化进来，于是校验器认不出**自己产出的**文本——
        // "GET 任务 → 原样 PUT 回去"会拿到一个服务端自己制造的 400。
        FilterRuleValidationResult result = FilterRuleValidator.Validate(FrontendProducedJson);

        Assert.True(result.Ok);
        using JsonDocument document = JsonDocument.Parse(result.Normalized);
        string[] names = [.. document.RootElement.EnumerateObject().Select(p => p.Name)];

        // 只断言**字段集合**：顺序由 FilterRuleSet 的声明顺序决定（与前端序列化器的
        // 顺序不同，但两边都按字段名读写，顺序无语义）。这里要守的是"不多不少这八个"。
        string[] expected =
        [
            "ExcludeFiles", "ExcludeFolders", "ExcludePaths",
            "IncludeFiles", "IncludeFolders", "IncludePaths",
            "IsEnabled", "UseRegex",
        ];
        Assert.Equal(expected.Order(), names.Order());
    }

    [Fact]
    public void 归一化结果可以再次通过校验()
    {
        FilterRuleValidationResult first = FilterRuleValidator.Validate(FrontendProducedJson);
        Assert.True(first.Ok);

        FilterRuleValidationResult second = FilterRuleValidator.Validate(first.Normalized);

        Assert.True(second.Ok, $"{second.Error}: {second.ErrorDetail}");
        Assert.Equal(first.Normalized, second.Normalized);
    }

    [Fact]
    public void 正则模式下多行条件被拒绝()
    {
        // FzLib 的正则分支把整个字段当成**一条**表达式：写成两行不是"两条规则"，
        // 而是一条含换行的正则——保存通过、试算通过、备份时一条都匹配不上。
        const string json =
            """{"IsEnabled":true,"UseRegex":true,"IncludeFiles":"^a\\.txt$\r\n^b\\.txt$"}""";

        FilterRuleValidationResult result = FilterRuleValidator.Validate(json);

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.RegexMultiplePerField, result.Error);
        Assert.Equal(nameof(FilterRuleSet.IncludeFiles), result.ErrorField);
    }

    [Fact]
    public void 带条件却关掉开关的规则被拒绝()
    {
        // IsEnabled=false 时引擎走 NullFileFilter：规则被整体忽略，而 HasFilter 仍为 true。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"IsEnabled":false,"ExcludeFiles":"*.tmp"}""");

        Assert.False(result.Ok);
        Assert.Equal(FilterRuleError.DisabledWithRules, result.Error);
    }

    [Fact]
    public void 关掉开关且没有条件时按空规则处理()
    {
        FilterRuleValidationResult result = FilterRuleValidator.Validate("""{"IsEnabled":false}""");

        Assert.True(result.Ok);
        Assert.Equal("{}", result.Normalized);
    }

    [Fact]
    public void null字段被归一化成空串()
    {
        // JSON 的 null 对 string 属性是合法的（不抛异常），归一化必须把它抹平，
        // 否则库里会出现 "IncludeFiles":null——前端把该字段声明为 string。
        FilterRuleValidationResult result =
            FilterRuleValidator.Validate("""{"IsEnabled":true,"IncludeFiles":null,"ExcludeFiles":"*.tmp"}""");

        Assert.True(result.Ok, result.ErrorDetail);
        Assert.Equal(string.Empty, result.Rule!.IncludeFiles);
        Assert.DoesNotContain("null", result.Normalized);
    }
}
