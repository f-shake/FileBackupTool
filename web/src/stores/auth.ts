/**
 * 认证状态（05 §2）。
 *
 * 关键约束：**未绑定 TOTP 前不能访问任何业务功能**。
 * 因此 store 暴露 `mustBindTotp`，由路由守卫强制跳到登录页的绑定步骤。
 */
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { authApi, asApiError } from '@/api'
import { setUnauthorizedHandler } from '@/api/http'
import type {
  ChangePasswordRequest,
  CurrentUser,
  LoginRequest,
  NormalizedApiError,
  SessionInfo,
  TotpSetupResult,
} from '@/api/types'

export type AuthStatus = 'unknown' | 'authenticated' | 'unauthenticated' | 'error'

export const useAuthStore = defineStore('auth', () => {
  const status = ref<AuthStatus>('unknown')
  const user = ref<CurrentUser | null>(null)
  const error = ref<NormalizedApiError | null>(null)

  const isAuthenticated = computed(() => status.value === 'authenticated' && user.value !== null)
  /** 已登录但尚未完成 TOTP 绑定 —— 必须走绑定流程 */
  const mustBindTotp = computed(
    () => isAuthenticated.value && user.value !== null && !user.value.twoFactorConfirmed,
  )

  async function bootstrap(): Promise<void> {
    status.value = 'unknown'
    error.value = null
    try {
      user.value = await authApi.getCurrentUser()
      status.value = 'authenticated'
    } catch (err) {
      const e = asApiError(err)
      if (e.status === 401) {
        // 未登录是正常状态，不是错误
        user.value = null
        status.value = 'unauthenticated'
      } else {
        // 后端未部署 / 网络不通 —— 明确展示错误态，不能静默当作未登录
        error.value = e
        status.value = 'error'
      }
    }
  }

  /**
   * 幂等启动：**一次会话内只真正请求一次**。路由守卫与 App.vue 共用。
   *
   * ⚠️ 别把 `bootPromise` 在完成后置回 `null`——那会让**每一次路由跳转**都重新 bootstrap：
   * `bootstrap()` 的第一件事是 `status.value = 'unknown'`，而 App.vue 用
   * `v-if="auth.status === 'unknown'"` 把整个 `<router-view>` 换成启动页。
   * 症状是每次点导航整屏闪一次"正在确认登录状态…"，并且原页面组件被**销毁重建**
   * （列表滚动位置、筛选条件、打开的弹窗全丢）；后端慢时，闪烁时长正好是一次
   * `/api/auth/me` 的往返。
   *
   * 需要"重新确认"时显式传 `force`（例如启动错误卡片上的"重试"）。
   */
  let bootPromise: Promise<void> | null = null
  let booted = false

  async function ensureBootstrapped(force = false): Promise<void> {
    if (force) {
      bootPromise = null
      booted = false
    }

    // 已经判定过就直接返回——这条是**每次导航**都会走的路径。
    // 判 `booted` 而不是 `status !== 'unknown'`：登录成功后 status 已是 'authenticated'，
    // 那种情况同样不该再打一次 /api/auth/me。
    if (booted && !bootPromise) return

    if (!bootPromise) {
      bootPromise = bootstrap().finally(() => {
        booted = true
        bootPromise = null
      })
    }

    await bootPromise
  }


  function setUnauthenticated(): void {
    user.value = null
    status.value = 'unauthenticated'
  }

  /** 登录（可能只走完第一步，需要 TOTP） */
  async function login(request: LoginRequest) {
    const result = await authApi.login(request)
    if (result.authenticated) {
      user.value = result.user ?? null
      status.value = 'authenticated'
      if (!user.value) await bootstrap()
    }
    return result
  }

  async function logout(): Promise<void> {
    try {
      await authApi.logout()
    } catch {
      // 登出失败也要在本地清掉状态，否则用户被卡住
    }
    setUnauthenticated()
  }

  async function refreshUser(): Promise<void> {
    user.value = await authApi.getCurrentUser()
    status.value = 'authenticated'
  }

  /* ------------------------- TOTP 强制绑定 ------------------------- */

  /** `password` 只在已绑定状态下的重绑时必填（见 `api/auth.ts` 的说明）。 */
  async function setupTotp(password?: string): Promise<TotpSetupResult> {
    return authApi.setupTotp(password)
  }

  async function confirmTotp(code: string, password?: string): Promise<string[]> {
    const result = await authApi.confirmTotp(code, password)
    await refreshUser()
    return result.recoveryCodes
  }

  async function regenerateRecoveryCodes(password: string): Promise<string[]> {
    const result = await authApi.regenerateRecoveryCodes(password)
    return result.recoveryCodes
  }

  async function changePassword(body: ChangePasswordRequest): Promise<void> {
    // 改密码会靠 Identity 安全戳一次性失效全部会话（05 §3）
    await authApi.changePassword(body)
    setUnauthenticated()
  }

  async function listSessions(): Promise<SessionInfo[]> {
    return authApi.listSessions()
  }

  async function revokeSession(id: string): Promise<void> {
    await authApi.revokeSession(id)
  }

  /** 由 HTTP 层在收到 401 时回调 */
  function installUnauthorizedHandler(): void {
    setUnauthorizedHandler(() => {
      if (status.value === 'authenticated') setUnauthenticated()
    })
  }

  return {
    status,
    user,
    error,
    isAuthenticated,
    mustBindTotp,
    bootstrap,
    ensureBootstrapped,
    setUnauthenticated,
    installUnauthorizedHandler,
    login,
    logout,
    refreshUser,
    setupTotp,
    confirmTotp,
    regenerateRecoveryCodes,
    changePassword,
    listSessions,
    revokeSession,
  }
})
