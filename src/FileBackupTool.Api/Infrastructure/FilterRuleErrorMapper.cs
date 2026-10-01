using FileBackupTool.Api.Contracts;
using FileBackupTool.Core.Filtering;

namespace FileBackupTool.Api.Infrastructure;

/// <summary>
/// 把 Core 的 <see cref="FilterRuleValidationResult"/> 翻译成对外的 400。
/// </summary>
/// <remarks>
/// 单独一个文件是为了让任务保存（<c>TasksController</c>）与筛选试算
/// （<c>FsController.FilterTest</c>）**说同一套话**：同一个非法规则，两个端点
/// 必须给出同一个错误码与同一句文案，否则用户会以为"试算说不行、保存说行"。
///
/// 映射放在 Api 层而不是 Core：Core 不该依赖 <see cref="ApiException"/>。
/// </remarks>
internal static class FilterRuleErrorMapper
{
    public static ApiException ToApiException(FilterRuleValidationResult result)
    {
        return result.Error switch
        {
            FilterRuleError.TooLong => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                $"筛选规则过长（上限 {FilterRuleValidator.MaxLength / 1024} KB）。"),

            FilterRuleError.NotJson => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则不是合法的 JSON，已拒绝保存。",
                result.ErrorDetail),

            FilterRuleError.NotObject => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则必须是一个 JSON 对象。",
                result.ErrorDetail),

            // 这条是 2026-10-01 那次缺陷的门神：认不出的形状必须报错。
            // 文案要直说后果，因为用户唯一能看到的症状就是"配了规则却不生效"。
            FilterRuleError.UnknownShape => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则的字段无法识别，已拒绝保存——若静默忽略，备份范围会与预期不符。",
                result.ErrorDetail),

            FilterRuleError.InvalidType => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则的字段类型不正确。",
                result.ErrorDetail),

            // 带了条件却把开关关掉：引擎会整体忽略它，用户看到的却是"配了规则"。
            // 拒绝而不是替它改成 true——替它启用可能悄悄少备份文件，比不备份更糟。
            FilterRuleError.DisabledWithRules => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则带了条件却把 IsEnabled 置为 false——引擎会整体忽略它。请置为 true，或去掉条件。",
                result.ErrorDetail),

            FilterRuleError.InvalidRegex => ApiException.BadRequest(
                ApiErrorCodes.FilterRegexInvalid,
                $"筛选规则里的正则表达式无效（{result.ErrorField}）。",
                result.ErrorDetail),

            // 保存失败时界面只弹 message（不展示 detail），所以这条提示必须自带完整信息：
            // 用户写的两条正则各自都合法，他无从知道问题出在"写成了两行"。
            FilterRuleError.RegexMultiplePerField => ApiException.BadRequest(
                ApiErrorCodes.FilterRegexInvalid,
                $"正则模式下每个输入框只能写一条表达式（{result.ErrorField} 写了多条，多条请用 | 连接）。",
                result.ErrorDetail),

            _ => ApiException.BadRequest(
                ApiErrorCodes.FilterRuleInvalid,
                "筛选规则不合法。",
                result.ErrorDetail),
        };
    }
}
