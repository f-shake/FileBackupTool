/**
 * 全局类型声明。
 */

declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<Record<string, unknown>, Record<string, unknown>, unknown>
  export default component
}

interface ImportMetaEnv {
  /** 是否启用前端 Mock 层（无后端演示用）。默认关闭。 */
  readonly VITE_USE_MOCK?: string
  /** API 基础路径，默认 `/api`。 */
  readonly VITE_API_BASE?: string
  /** SignalR Hub 路径，默认 `/hubs/events`。 */
  readonly VITE_HUB_PATH?: string
  /** REST 轮询间隔（毫秒），默认 5000。 */
  readonly VITE_POLL_INTERVAL_MS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
