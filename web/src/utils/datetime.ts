/**
 * 中文日期时间格式统一 `yyyy-MM-dd HH:mm:ss`，时区显示为本地（07 §7）。
 * 后端一律给 UTC ISO 8601。
 */

const pad = (n: number): string => String(n).padStart(2, '0')

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === '') return null
  const d = value instanceof Date ? value : new Date(value)
  return Number.isNaN(d.getTime()) ? null : d
}

/** `2026-09-22 03:14:07`（本地时区） */
export function formatDateTime(value: string | number | Date | null | undefined): string {
  const d = toDate(value)
  if (!d) return '—'
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(
    d.getMinutes(),
  )}:${pad(d.getSeconds())}`
}

/** `2026-09-22` */
export function formatDate(value: string | number | Date | null | undefined): string {
  const d = toDate(value)
  if (!d) return '—'
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

/** Element Plus 的日期时间范围值 → ISO 字符串 */
export function rangeToIso(range: [Date, Date] | null | undefined): {
  from?: string
  to?: string
} {
  if (!range || range.length !== 2) return {}
  const [a, b] = range
  if (!a || !b) return {}
  return { from: a.toISOString(), to: b.toISOString() }
}

/** 相对时间：`3 分钟前`。用于监控页的倒计时与最近触发。 */
export function formatRelative(value: string | number | Date | null | undefined): string {
  const d = toDate(value)
  if (!d) return '—'
  const diffMs = Date.now() - d.getTime()
  const abs = Math.abs(diffMs)
  const future = diffMs < 0
  const suffix = future ? '后' : '前'

  const sec = Math.round(abs / 1000)
  if (sec < 60) return `${sec} 秒${suffix}`
  const min = Math.round(sec / 60)
  if (min < 60) return `${min} 分钟${suffix}`
  const hour = Math.round(min / 60)
  if (hour < 24) return `${hour} 小时${suffix}`
  const day = Math.round(hour / 24)
  if (day < 30) return `${day} 天${suffix}`
  return formatDate(d)
}

/** 秒 → `00:12:34`（倒计时显示） */
export function formatDuration(seconds: number | null | undefined): string {
  if (seconds === null || seconds === undefined || !Number.isFinite(seconds)) return '—'
  const s = Math.max(0, Math.floor(seconds))
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  const sec = s % 60
  return h > 0 ? `${pad(h)}:${pad(m)}:${pad(sec)}` : `${pad(m)}:${pad(sec)}`
}

/** 两个时间点之间的耗时 */
export function formatElapsed(
  begin: string | null | undefined,
  end: string | null | undefined,
): string {
  const a = toDate(begin)
  const b = toDate(end)
  if (!a) return '—'
  const ms = (b ?? new Date()).getTime() - a.getTime()
  if (ms < 0) return '—'
  const sec = ms / 1000
  if (sec < 60) return `${sec.toFixed(1)} 秒`
  return formatDuration(sec)
}
