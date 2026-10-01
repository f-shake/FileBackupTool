using System.Text.Json;
using System.Text.RegularExpressions;

namespace FileBackupTool.Core.Filtering;

/// <summary>筛选规则 JSON 的校验失败类别。API 层据此选错误码与文案。</summary>
public enum FilterRuleError
{
    None = 0,

    /// <summary>不是合法 JSON。</summary>
    NotJson,

    /// <summary>不是 JSON 对象（数组、字符串、数字……）。</summary>
    NotObject,

    /// <summary>超过长度上限。</summary>
    TooLong,

    /// <summary>
    /// 是对象、也有内容，但**一个已知字段都没有**——例如历史上前端存进去的
    /// <c>{whitelist,blacklist,useRegex}</c> 嵌套形状。
    /// </summary>
    UnknownShape,

    /// <summary>已知字段的 JSON 类型不对（例如 <c>"UseRegex": "yes"</c>）。</summary>
    InvalidType,

    /// <summary>带了条件却置 <c>IsEnabled=false</c>——引擎会整体忽略它。</summary>
    DisabledWithRules,

    /// <summary>正则模式下有编译不过的表达式。</summary>
    InvalidRegex,

    /// <summary>
    /// 正则模式下同一个字段写了多条（多行）。单独成一类而不是并进 <see cref="InvalidRegex"/>：
    /// 保存失败时界面只弹 <c>message</c>（不展示 detail），提示必须自带完整信息才送得到用户眼前。
    /// </summary>
    RegexMultiplePerField,
}

/// <summary>校验结果。<see cref="Normalized"/> 在成功后是可直接入库的规范文本。</summary>
public sealed record FilterRuleValidationResult(
    bool Ok,
    string Normalized,
    FilterRuleSet? Rule,
    FilterRuleError Error,
    string? ErrorField,
    string? ErrorDetail)
{
    public static FilterRuleValidationResult Success(FilterRuleSet rule, string normalized) =>
        new(true, normalized, rule, FilterRuleError.None, null, null);

    public static FilterRuleValidationResult Failure(
        FilterRuleError error,
        string? field = null,
        string? detail = null) =>
        new(false, "{}", null, error, field, detail);
}

/// <summary>
/// 筛选规则 JSON 的解析与校验——**保存路径与试算路径共用同一份**。
/// </summary>
/// <remarks>
/// <para>
/// 存在的理由是一次真实缺陷（2026-10-01）：<c>FilterRuleSet.FromJson</c> 为了容错，
/// 遇到"认不出的 JSON"会静默返回空规则；而空规则等于**不过滤**。于是"界面里配了
/// 筛选规则"与"备份把源目录整个收进去"可以同时为真，且没有任何一方报错——
/// 对备份工具来说这是最坏的一类失败：它看起来成功了。
/// </para>
/// <para>
/// 所以这里的原则是：<b>凡是"有内容但读不懂"的输入，一律报错，绝不退化为不过滤</b>。
/// 只有真正空的输入（null / 空白 / <c>{}</c>）才被当作"没有规则"。
/// </para>
/// <para>
/// 之所以放在 Core 而不是 API 层：本仓库的测试项目不引用 Api（见 README），
/// 放在这里才能被单元测试直接覆盖。
/// </para>
/// </remarks>
public static class FilterRuleValidator
{
    /// <summary>与 API 层的长度限制保持一致（原 TasksController 里的 64 KB）。</summary>
    public const int MaxLength = 64 * 1024;

    private static readonly string[] KnownFields =
    [
        nameof(FilterRuleSet.IsEnabled),
        nameof(FilterRuleSet.UseRegex),
        nameof(FilterRuleSet.IncludeFiles),
        nameof(FilterRuleSet.IncludeFolders),
        nameof(FilterRuleSet.IncludePaths),
        nameof(FilterRuleSet.ExcludeFiles),
        nameof(FilterRuleSet.ExcludeFolders),
        nameof(FilterRuleSet.ExcludePaths),
    ];

    /// <summary>
    /// 反序列化用的大小写不敏感选项：输入写 <c>isEnabled</c> 也认，随后被归一化成
    /// PascalCase——否则引擎侧的 <c>FromJson</c>（区分大小写）会把它读丢，
    /// 又是一次静默退化。
    /// </summary>
    private static readonly JsonSerializerOptions DeserializeOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>校验并归一化筛选规则 JSON。失败时给出类别、字段名与细节。</summary>
    public static FilterRuleValidationResult Validate(string? filterJson)
    {
        if (string.IsNullOrWhiteSpace(filterJson))
        {
            return FilterRuleValidationResult.Success(new FilterRuleSet(), "{}");
        }

        if (filterJson.Length > MaxLength)
        {
            return FilterRuleValidationResult.Failure(FilterRuleError.TooLong);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(filterJson);
        }
        catch (JsonException ex)
        {
            return FilterRuleValidationResult.Failure(FilterRuleError.NotJson, detail: ex.Message);
        }

        using (document)
        {
            JsonElement root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return FilterRuleValidationResult.Failure(FilterRuleError.NotObject);
            }

            List<string> propertyNames = [];
            List<string> unknownNames = [];
            foreach (JsonProperty property in root.EnumerateObject())
            {
                propertyNames.Add(property.Name);
                if (!Array.Exists(KnownFields, f => string.Equals(f, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    unknownNames.Add(property.Name);
                }
            }

            if (propertyNames.Count == 0)
            {
                // 真正的空对象：等价于"没有规则"。
                return FilterRuleValidationResult.Success(new FilterRuleSet(), "{}");
            }

            // 判据是**全部字段都必须认识**，不是"至少认识一个"。
            //
            // 后者会被历史形状蒙混过关：旧的 `{whitelist,blacklist,useRegex}` 里
            // `useRegex` 与已知字段 `UseRegex` 撞名，于是"至少一个已知"成立、
            // 规则被当成"只有 UseRegex 的有效规则"——筛选内容整体丢失且不报错，
            // 正是这次要根治的静默退化。认不出的字段意味着用户的意图有一部分
            // 引擎无法执行，那就必须报错，而不是悄悄丢掉。
            if (unknownNames.Count > 0)
            {
                return FilterRuleValidationResult.Failure(
                    FilterRuleError.UnknownShape,
                    detail: string.Join(", ", unknownNames.Take(8)));
            }

            FilterRuleSet rules;
            try
            {
                rules = JsonSerializer.Deserialize<FilterRuleSet>(filterJson, DeserializeOptions)
                        ?? new FilterRuleSet();
            }
            catch (JsonException ex)
            {
                return FilterRuleValidationResult.Failure(FilterRuleError.InvalidType, ex.Path, ex.Message);
            }

            // JSON 里的 null 对 string 属性是合法的：System.Text.Json 直接赋 null 而不报错。
            // 归一化成空串——否则库里会出现 "IncludeFiles":null，而前端把该字段声明为 string、
            // 引擎按"没有条件"处理，三处口径必须一致。
            rules.IncludeFiles ??= string.Empty;
            rules.IncludeFolders ??= string.Empty;
            rules.IncludePaths ??= string.Empty;
            rules.ExcludeFiles ??= string.Empty;
            rules.ExcludeFolders ??= string.Empty;
            rules.ExcludePaths ??= string.Empty;

            // 带了条件却把开关关掉 = 引擎侧走 NullFileFilter，整条规则被**静默忽略**。
            // 这正是本次要根除的失败形态，只不过入口从"形状"换成了"开关"。
            // 拒绝而不是替它改成 true：替它启用可能悄悄少备份文件，比不备份更糟。
            if (!rules.IsEnabled && !rules.IsEffectivelyEmpty)
            {
                return FilterRuleValidationResult.Failure(
                    FilterRuleError.DisabledWithRules,
                    detail: "IsEnabled=false");
            }

            FilterRuleError regexError = ValidateRegexes(rules, out string? field, out string? detail);
            if (regexError != FilterRuleError.None)
            {
                return FilterRuleValidationResult.Failure(regexError, field, detail);
            }

            // 归一化：字段名统一、顺序固定。空规则一律写成 "{}"——库里的既有约定
            // （BackupTask.HasFilter 靠它判断"有没有筛选"）。
            return FilterRuleValidationResult.Success(rules, rules.IsEffectivelyEmpty ? "{}" : rules.ToJson());
        }
    }

    /// <summary>
    /// 正则模式下的校验：每个字段**只能有一条表达式**，且必须能编译。
    /// </summary>
    /// <remarks>
    /// FzLib 的正则分支把整个字段当成一条表达式（实测：把一个字段写成两行，
    /// 两条都匹配不上——它是一条含换行的正则，而路径里没有换行）。而界面的
    /// 输入习惯是"每行一条"。两者相撞的结果是**静默不匹配**：保存通过、试算通过、
    /// 备份时什么都不生效——又是本次要根除的那类失败。所以在这里拒绝多行，
    /// 并要求用 <c>|</c> 写成一条。同时顺带把 <c>*.txt</c> 这类通配符写法当正则用的
    /// 错误当场拦下，而不是等引擎的 <c>Regex</c> 构造抛异常。
    /// </remarks>
    private static FilterRuleError ValidateRegexes(FilterRuleSet rules, out string? field, out string? detail)
    {
        field = null;
        detail = null;

        if (!rules.UseRegex)
        {
            return FilterRuleError.None;
        }

        (string Name, string Value)[] fields =
        [
            (nameof(FilterRuleSet.IncludeFiles), rules.IncludeFiles),
            (nameof(FilterRuleSet.IncludeFolders), rules.IncludeFolders),
            (nameof(FilterRuleSet.IncludePaths), rules.IncludePaths),
            (nameof(FilterRuleSet.ExcludeFiles), rules.ExcludeFiles),
            (nameof(FilterRuleSet.ExcludeFolders), rules.ExcludeFolders),
            (nameof(FilterRuleSet.ExcludePaths), rules.ExcludePaths),
        ];

        foreach ((string name, string value) in fields)
        {
            List<string> patterns = [.. SplitLines(value)];

            if (patterns.Count > 1)
            {
                field = name;
                // 提示写在 message 里（见枚举的说明），detail 不再重复。
                detail = null;
                return FilterRuleError.RegexMultiplePerField;
            }

            if (patterns.Count == 0)
            {
                continue;
            }

            try
            {
                _ = new Regex(patterns[0]);
            }
            catch (ArgumentException ex)
            {
                field = name;
                detail = ex.Message;
                return FilterRuleError.InvalidRegex;
            }
        }

        return FilterRuleError.None;
    }

    private static IEnumerable<string> SplitLines(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        foreach (string line in value.Split('\n'))
        {
            // Trim() 顺带吃掉 CRLF 的 \r，不必再单独处理。
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                yield return trimmed;
            }
        }
    }
}
