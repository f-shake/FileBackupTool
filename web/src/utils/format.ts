/** 数值与文本格式化。 */

const UNITS = ['B', 'KB', 'MB', 'GB', 'TB', 'PB']

/** 字节 → `1.23 GB`。保留两位有效小数，0 显示 `0 B`。 */
export function formatBytes(bytes: number | null | undefined, fractionDigits = 2): string {
  if (bytes === null || bytes === undefined || !Number.isFinite(bytes)) return '—'
  if (bytes === 0) return '0 B'
  const sign = bytes < 0 ? '-' : ''
  const abs = Math.abs(bytes)
  const i = Math.min(UNITS.length - 1, Math.floor(Math.log(abs) / Math.log(1024)))
  const value = abs / Math.pow(1024, i)
  return `${sign}${value.toFixed(i === 0 ? 0 : fractionDigits)} ${UNITS[i]}`
}

/** 千分位整数 */
export function formatCount(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return value.toLocaleString('zh-CN')
}

/** 进度百分比（0–100），null 表示不确定态 */
export function formatPercent(value: number | null | undefined): string {
  if (value === null || value === undefined || !Number.isFinite(value)) return '—'
  return `${value.toFixed(1)}%`
}

/**
 * 长路径中段省略：`D:\Documents\...\2026年度总结.docx`。
 * hover 由调用方通过 title 属性给出全路径（07 §7）。
 */
export function ellipsisPath(path: string | null | undefined, maxLength = 48): string {
  if (!path) return '—'
  if (path.length <= maxLength) return path
  const separator = path.includes('\\') ? '\\' : '/'
  const parts = path.split(separator).filter(Boolean)
  if (parts.length <= 2) return `${path.slice(0, maxLength - 1)}…`

  const head = parts.slice(0, 2).join(separator)
  const tail = parts[parts.length - 1] ?? ''
  const middle = `${separator}…${separator}`
  const result = `${head}${middle}${tail}`
  return result.length <= maxLength ? result : `${path.slice(0, maxLength - 1)}…`
}

/** 文件图标（不做内容预览，只按扩展名分类——07 §4.2 明确只显示文件名与元数据） */
export function fileKindOf(name: string): 'image' | 'video' | 'audio' | 'archive' | 'document' | 'code' | 'other' {
  const ext = name.includes('.') ? name.split('.').pop()!.toLowerCase() : ''
  if (['jpg', 'jpeg', 'png', 'gif', 'bmp', 'webp', 'tif', 'tiff', 'heic', 'svg'].includes(ext)) return 'image'
  if (['mp4', 'mkv', 'avi', 'mov', 'wmv', 'flv', 'webm', 'm4v'].includes(ext)) return 'video'
  if (['mp3', 'flac', 'wav', 'aac', 'm4a', 'ogg'].includes(ext)) return 'audio'
  if (['zip', 'rar', '7z', 'tar', 'gz', 'bz2', 'xz'].includes(ext)) return 'archive'
  if (['doc', 'docx', 'xls', 'xlsx', 'ppt', 'pptx', 'pdf', 'txt', 'md', 'rtf', 'csv'].includes(ext)) return 'document'
  if (['cs', 'js', 'ts', 'vue', 'py', 'java', 'go', 'rs', 'json', 'xml', 'yml', 'yaml', 'sql', 'sh', 'ps1'].includes(ext)) return 'code'
  return 'other'
}

/** 哈希缩写：`abcdef01…ef01` */
export function shortHash(hash: string | null | undefined, head = 8, tail = 4): string {
  if (!hash) return '—'
  if (hash.length <= head + tail + 1) return hash
  return `${hash.slice(0, head)}…${hash.slice(-tail)}`
}
