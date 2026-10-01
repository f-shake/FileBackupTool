/**
 * 筛选规则的解析与序列化（`BackupTask.filterJson` ↔ 编辑器模型 `FileFilterRule`）。
 *
 * ⚠️ 这两者**不是同一个形状**：库里存的是扁平的 `FilterRuleSetJson`（与后端
 * `FileBackupTool.Core.Filtering.FilterRuleSet` 逐字对应），编辑器用的是
 * 白名单/黑名单 × 文件/文件夹/路径 的嵌套模型。本文件是**唯一**的转换点：
 *
 *   · 任何要发给后端的 filterJson 都必须出自 `serializeFilterRule`
 *   · 任何从后端拿到的 filterJson 都必须经过 `parseFilterRule`
 *
 * 曾经的缺陷（2026-10-01 修复，见 docs/plans/09）：保存时把编辑器模型直接
 * `JSON.stringify` 进库，后端认不出形状、`FromJson` 静默退化为"不过滤"——
 * 界面里配的规则被整体忽略，而界面上看起来一切正常。
 */
import type { FileFilterRule, FilterRuleGroup, FilterRuleSetJson } from '@/api/types'

/**
 * 多值分隔符必须是 **CRLF**。
 *
 * 这不是风格问题：后端的 `FileFilterHelper` 按 `Environment.NewLine`
 * （Windows 上 `\r\n`）拆行，而 `<textarea>` 给我们的值按 HTML 规范是 `\n`。
 * 直接用 textarea 的原文，多行条件会被当成**一条**永远匹配不上的模式。
 */
const WIRE_SEPARATOR = '\r\n'

const emptyGroup = (): FilterRuleGroup => ({ file: '', folder: '', path: '' })

export function emptyFilterRule(): FileFilterRule {
  return { whitelist: emptyGroup(), blacklist: emptyGroup(), useRegex: false }
}

/** 库里/接口上的多行文本 → textarea 显示的文本（CRLF、CR 一律还原成 LF）。 */
function toEditorText(value: unknown): string {
  return typeof value === 'string' ? value.replace(/\r\n?/g, '\n') : ''
}

/** textarea 的文本 → 线上格式：逐行 trim、丢掉空行、CRLF 连接。 */
function toWireText(value: string): string {
  return splitConditions(value).join(WIRE_SEPARATOR)
}

function isGroupEmpty(group: FilterRuleGroup): boolean {
  return !group.file.trim() && !group.folder.trim() && !group.path.trim()
}

/**
 * 解析库里的 `filterJson`（宽容：null、空串、非法 JSON、非对象一律得到空规则）。
 *
 * **只认扁平形状**：历史上前端写进去的嵌套形状（whitelist/blacklist）不在兼容范围内，
 * 会得到空规则——它与"不过滤"在语义上一致，且不会假装规则还在。
 */
export function parseFilterRule(json: string | null | undefined): FileFilterRule {
  if (!json) return emptyFilterRule()

  let raw: Partial<FilterRuleSetJson>
  try {
    const parsed: unknown = JSON.parse(json)
    if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed)) {
      return emptyFilterRule()
    }
    raw = parsed as Partial<FilterRuleSetJson>
  } catch {
    return emptyFilterRule()
  }

  return {
    whitelist: {
      file: toEditorText(raw.IncludeFiles),
      folder: toEditorText(raw.IncludeFolders),
      path: toEditorText(raw.IncludePaths),
    },
    blacklist: {
      file: toEditorText(raw.ExcludeFiles),
      folder: toEditorText(raw.ExcludeFolders),
      path: toEditorText(raw.ExcludePaths),
    },
    useRegex: Boolean(raw.UseRegex),
  }
}

/**
 * 编辑器模型 → 库里的扁平 JSON；**完全没有任何条件时返回 null**
 * （后端把 null 归一化成 `{}`，不会留下一条空规则）。
 */
export function serializeFilterRule(rule: FileFilterRule): string | null {
  if (isGroupEmpty(rule.whitelist) && isGroupEmpty(rule.blacklist)) {
    return null
  }

  const wire: FilterRuleSetJson = {
    // 界面上没有"启用筛选"开关：填了条件就是要用。恒为 true——
    // 后端 `FilterRuleSet.FromJson` 要求 IsEnabled 才应用规则，
    // 写 false 就等于"配了不生效"（这是本缺陷的第二半）。
    IsEnabled: true,
    UseRegex: rule.useRegex,
    IncludeFiles: toWireText(rule.whitelist.file),
    IncludeFolders: toWireText(rule.whitelist.folder),
    IncludePaths: toWireText(rule.whitelist.path),
    ExcludeFiles: toWireText(rule.blacklist.file),
    ExcludeFolders: toWireText(rule.blacklist.folder),
    ExcludePaths: toWireText(rule.blacklist.path),
  }

  return JSON.stringify(wire)
}

/**
 * 把多行文本切成非空条件行。
 * 同时接受 LF 与 CRLF：textarea 给 LF，库里存的是 CRLF。
 */
export function splitConditions(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

/** 统计规则里的条件条数，用于列表页摘要与"使用当前目录测试"的启用判断。 */
export function countConditions(rule: FileFilterRule): number {
  const groups = [rule.whitelist, rule.blacklist]
  let count = 0
  for (const g of groups) {
    count += splitConditions(g.file).length
    count += splitConditions(g.folder).length
    count += splitConditions(g.path).length
  }
  return count
}
