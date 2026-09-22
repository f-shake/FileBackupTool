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

  /** 幂等启动：并发调用只会真正请求一次。路由守卫与 App.vue 共用。 */
  let bootPromise: Promise<void> | null = null
  async function ensureBootstrapped(force = false): Promise<void> {
    if (force) bootPromise = null
    if (!bootPromise) bootPromise = bootstrap()
    await bootPromise
    bootPromise = null
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

  async function setupTotp(): Promise<TotpSetupResult> {
    return authApi.setupTotp()
  }

  async function confirmTotp(code: string): Promise<string[]> {
    const result = await authApi.confirmTotp(code)
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
