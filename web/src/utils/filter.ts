/** 筛选规则的解析与序列化（`BackupTask.filterJson` ↔ `FileFilterRule`）。 */
import type { FileFilterRule, FilterRuleGroup } from '@/api/types'

const emptyGroup = (): FilterRuleGroup => ({ file: '', folder: '', path: '' })

export function emptyFilterRule(): FileFilterRule {
  return { whitelist: emptyGroup(), blacklist: emptyGroup(), useRegex: false }
}

/** 宽容解析：后端/旧数据可能给出 null、空串或残缺结构，一律补齐而不是抛错。 */
export function parseFilterRule(json: string | null | undefined): FileFilterRule {
  if (!json) return emptyFilterRule()
  try {
    const raw = JSON.parse(json) as Partial<FileFilterRule>
    return {
      whitelist: { ...emptyGroup(), ...(raw.whitelist ?? {}) },
      blacklist: { ...emptyGroup(), ...(raw.blacklist ?? {}) },
      useRegex: Boolean(raw.useRegex),
    }
  } catch {
    return emptyFilterRule()
  }
}

/** 全空则序列化为 null（表示"不限制"），避免存一堆空规则进库。 */
export function serializeFilterRule(rule: FileFilterRule): string | null {
  const isGroupEmpty = (g: FilterRuleGroup) => !g.file.trim() && !g.folder.trim() && !g.path.trim()
  if (isGroupEmpty(rule.whitelist) && isGroupEmpty(rule.blacklist) && !rule.useRegex) return null
  return JSON.stringify(rule)
}

/** 把多行文本切成非空条件行 */
export function splitConditions(text: string): string[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
}

/** 统计规则里的条件条数，用于列表页摘要 */
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
