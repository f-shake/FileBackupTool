using System.Text.Json;
using System.Text.Json.Serialization;
using FzLib.IO;
using FileBackupTool.Core.Abstractions;

namespace FileBackupTool.Core.Filtering;

/// <summary>
/// 筛选规则的持久化模型。语义与属性名沿用 <c>FzLib.IO.FileFilterRule</c>（决策 #1、设计文档 03）。
/// </summary>
/// <remarks>
/// 刻意不直接序列化 FzLib 的类型，避免把本产品的持久化格式绑死在 FzLib 上。
/// </remarks>
public sealed class FilterRuleSet
{
    /// <summary><b>由调用方判断</b>——<c>FileFilterHelper</c> 自己**不检查** IsEnabled（已实测确认）。</summary>
    public bool IsEnabled { get; set; }

    public bool UseRegex { get; set; }

    public string IncludeFiles { get; set; } = string.Empty;

    public string ExcludeFiles { get; set; } = string.Empty;

    public string IncludeFolders { get; set; } = string.Empty;

    public string ExcludeFolders { get; set; } = string.Empty;

    public string IncludePaths { get; set; } = string.Empty;

    public string ExcludePaths { get; set; } = string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>解析。空串或非法 JSON 一律退化为"空规则"（不过滤），而不是抛异常。</summary>
    public static FilterRuleSet FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new FilterRuleSet();
        }

        try
        {
            return JsonSerializer.Deserialize<FilterRuleSet>(json, JsonOptions) ?? new FilterRuleSet();
        }
        catch (JsonException)
        {
            return new FilterRuleSet();
        }
    }

    /// <summary>转换为 FzLib 的规则对象。</summary>
    public FileFilterRule ToFzLibRule() => new()
    {
        IsEnabled = IsEnabled,
        UseRegex = UseRegex,
        IncludeFiles = IncludeFiles,
        ExcludeFiles = ExcludeFiles,
        IncludeFolders = IncludeFolders,
        ExcludeFolders = ExcludeFolders,
        IncludePaths = IncludePaths,
        ExcludePaths = ExcludePaths,
    };

    public bool IsEffectivelyEmpty =>
        string.IsNullOrWhiteSpace(IncludeFiles) && string.IsNullOrWhiteSpace(ExcludeFiles) &&
        string.IsNullOrWhiteSpace(IncludeFolders) && string.IsNullOrWhiteSpace(ExcludeFolders) &&
        string.IsNullOrWhiteSpace(IncludePaths) && string.IsNullOrWhiteSpace(ExcludePaths);
}

/// <summary>
/// 基于 <c>FzLib.IO.FileFilterHelper</c> 的筛选器。
/// </summary>
/// <remarks>
/// 两个实测出来的语义要点（<b>不要凭直觉改</b>）：
/// <list type="number">
/// <item><c>IsMatched(path, isFile)</c> 的第二参**默认 true = 文件**。传 false 才是目录，
/// 且目录模式下<b>路径的最后一段也参与文件夹名匹配</b>。</item>
/// <item>多值分隔符是 <c>Environment.NewLine</c>（Windows 上是 <c>\r\n</c>），
/// <b>不是</b>分号或逗号。</item>
/// </list>
/// </remarks>
public sealed class FzLibFileFilter : IFileFilter
{
    private readonly FileFilterHelper? _include;
    private readonly FileFilterHelper? _excludeProbe;
    private readonly IPathNormalizer _normalizer;

    public FzLibFileFilter(FilterRuleSet rule, IPathNormalizer normalizer)
    {
        _normalizer = normalizer;
        if (!rule.IsEnabled)
        {
            return;
        }

        if (!rule.IsEffectivelyEmpty)
        {
            _include = new FileFilterHelper(rule.ToFzLibRule());
        }

        // 只带 exclude 规则的探针：include 侧全部退化为 "*"，于是
        // IsMatched(dir, isFile: false) == false  ⟺  命中了某条 exclude 规则。
        // 目录剪枝**只能**用 exclude 规则——include 规则一剪枝就会把包含目标的祖先目录也剪掉。
        if (!string.IsNullOrWhiteSpace(rule.ExcludeFiles) ||
            !string.IsNullOrWhiteSpace(rule.ExcludeFolders) ||
            !string.IsNullOrWhiteSpace(rule.ExcludePaths))
        {
            _excludeProbe = new FileFilterHelper(new FileFilterRule
            {
                UseRegex = rule.UseRegex,
                ExcludeFiles = rule.ExcludeFiles,
                ExcludeFolders = rule.ExcludeFolders,
                ExcludePaths = rule.ExcludePaths,
            });
        }
    }

    public bool IncludeFile(string fullPath)
    {
        if (_include is null)
        {
            return true;
        }

        string p = _normalizer.TrimTrailingSeparator(fullPath);
        if (string.IsNullOrEmpty(_normalizer.GetFileName(p)))
        {
            // FileFilterHelper.IsMatchedName 对空文件名会抛 ArgumentException
            return true;
        }

        return _include.IsMatched(p, isFile: true);
    }

    public bool DescendInto(string directoryFullPath)
    {
        if (_excludeProbe is null)
        {
            return true;
        }

        string p = _normalizer.TrimTrailingSeparator(directoryFullPath);
        return p.Length > 0 && _excludeProbe.IsMatched(p, isFile: false);
    }
}

/// <summary>
/// 在任意筛选器之上再叠一层**硬编码排除**。这些是产品级约束，不由用户配置决定：
/// <list type="bullet">
/// <item><c>$RECYCLE.BIN</c>——设计文档 03 §3 明确要求硬编码跳过。</item>
/// <item>数据目录（<c>App_Data</c>）——SQLite 的 <c>-wal</c> / <c>-shm</c> 边车文件会产生高频写入，
/// 落进源目录范围时会变成海量噪声事件（设计文档 02 §8）。</item>
/// <item>任务自己的备份目录——纯防御，配置校验已经禁止了这种关系。</item>
/// </list>
/// </summary>
public sealed class HardExcludeFileFilter : IFileFilter
{
    private const string RecycleBinSegment = "$RECYCLE.BIN";

    private readonly IFileFilter _inner;
    private readonly IPathNormalizer _normalizer;
    private readonly IReadOnlyList<string> _excludedPrefixes;

    public HardExcludeFileFilter(IFileFilter inner, IPathNormalizer normalizer, IEnumerable<string> excludedPrefixes)
    {
        _inner = inner;
        _normalizer = normalizer;
        _excludedPrefixes = [.. excludedPrefixes.Where(p => !string.IsNullOrWhiteSpace(p))];
    }

    public bool IncludeFile(string fullPath) => !IsHardExcluded(fullPath) && _inner.IncludeFile(fullPath);

    public bool DescendInto(string directoryFullPath) =>
        !IsHardExcluded(directoryFullPath) && _inner.DescendInto(directoryFullPath);

    private bool IsHardExcluded(string fullPath)
    {
        if (fullPath.Contains(RecycleBinSegment, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (string prefix in _excludedPrefixes)
        {
            if (_normalizer.IsSameOrUnder(fullPath, prefix))
            {
                return true;
            }
        }

        return false;
    }
}
