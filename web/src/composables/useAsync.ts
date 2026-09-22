/**
 * 统一的「加载中 / 出错 / 空」三态封装。
 *
 * 页面用它保证：**接口 404 或网络错误时一定有明确的错误态 UI**（不白屏、不静默）。
 */
import { ref, shallowRef, type Ref, type ShallowRef } from 'vue'
import { asApiError } from '@/api'
import type { NormalizedApiError } from '@/api/types'

export interface AsyncState<T> {
  data: ShallowRef<T | null>
  loading: Ref<boolean>
  error: Ref<NormalizedApiError | null>
  /** 首次加载尚未完成（用于区分「加载中」与「后台刷新」） */
  initializing: Ref<boolean>
  run: () => Promise<T | null>
  /** 不显示 loading 的静默刷新（轮询/实时重载用） */
  refreshSilently: () => Promise<T | null>
}

export function useAsync<T>(loader: () => Promise<T>, immediate = true): AsyncState<T> {
  const data = shallowRef<T | null>(null)
  const loading = ref(false)
  const initializing = ref(true)
  const error = ref<NormalizedApiError | null>(null)

  async function run(): Promise<T | null> {
    loading.value = true
    error.value = null
    try {
      const result = await loader()
      data.value = result
      return result
    } catch (err) {
      error.value = asApiError(err)
      return null
    } finally {
      loading.value = false
      initializing.value = false
    }
  }

  async function refreshSilently(): Promise<T | null> {
    try {
      const result = await loader()
      data.value = result
      error.value = null
      return result
    } catch (err) {
      // 静默刷新不打断已有内容；但若此前从未成功过，仍需暴露错误
      if (data.value === null) error.value = asApiError(err)
      return null
    } finally {
      initializing.value = false
    }
  }

  if (immediate) void run()

  return { data, loading, error, initializing, run, refreshSilently }
}
