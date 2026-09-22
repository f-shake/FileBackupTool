/**
 * 主题：Element Plus 亮/暗色，**自动跟随系统**（`prefers-color-scheme`），允许手动覆盖（07 §1）。
 */
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import type { ThemePreference } from '@/api/types'

const STORAGE_KEY = 'fbt.theme'

function readStored(): ThemePreference {
  const raw = localStorage.getItem(STORAGE_KEY)
  return raw === 'light' || raw === 'dark' ? raw : 'auto'
}

export const useThemeStore = defineStore('theme', () => {
  const preference = ref<ThemePreference>(readStored())
  const systemDark = ref(
    typeof window !== 'undefined' && window.matchMedia
      ? window.matchMedia('(prefers-color-scheme: dark)').matches
      : false,
  )

  /** 实际生效的是不是暗色 */
  const isDark = computed(() =>
    preference.value === 'auto' ? systemDark.value : preference.value === 'dark',
  )

  function apply(): void {
    // Element Plus 的暗色主题靠 <html class="dark"> 上的 css-vars 生效
    document.documentElement.classList.toggle('dark', isDark.value)
    document.documentElement.style.colorScheme = isDark.value ? 'dark' : 'light'
  }

  function setPreference(value: ThemePreference): void {
    preference.value = value
    if (value === 'auto') localStorage.removeItem(STORAGE_KEY)
    else localStorage.setItem(STORAGE_KEY, value)
    apply()
  }

  /** 跟随系统变化。返回取消订阅函数。 */
  function watchSystem(): () => void {
    if (!window.matchMedia) return () => undefined
    const mq = window.matchMedia('(prefers-color-scheme: dark)')
    const handler = (e: MediaQueryListEvent) => {
      systemDark.value = e.matches
      if (preference.value === 'auto') apply()
    }
    mq.addEventListener('change', handler)
    return () => mq.removeEventListener('change', handler)
  }

  apply()

  return { preference, systemDark, isDark, setPreference, apply, watchSystem }
})
