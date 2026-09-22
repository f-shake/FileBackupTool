/**
 * 剪贴板（对等清单的「复制信息」：文件名 / 相对路径 / 哈希）。
 * 降级：`navigator.clipboard` 在非安全上下文不可用，回落到 textarea + execCommand。
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    /* 落到降级实现 */
  }

  try {
    const el = document.createElement('textarea')
    el.value = text
    el.setAttribute('readonly', 'readonly')
    el.style.position = 'fixed'
    el.style.opacity = '0'
    document.body.appendChild(el)
    el.select()
    const ok = document.execCommand('copy')
    document.body.removeChild(el)
    return ok
  } catch {
    return false
  }
}
