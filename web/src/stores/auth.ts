/**
 * 认证状态（05 §2）。
 *
 * 单密码模式：登录成功即已认证，没有"密码对了但还不能进"的中间态。
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
} from '@/api/types'

export type AuthStatus = 'unknown' | 'authenticated' | 'unauthenticated' | 'error'

export const useAuthStore = defineStore('auth', () => {
  const status = ref<AuthStatus>('unknown')
  const user = ref<CurrentUser | null>(null)
  const error = ref<NormalizedApiError | null>(null)

  const isAuthenticated = computed(() => status.value === 'authenticated' && user.value !== null)

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

  /** 登录（单密码：成功即已认证） */
  async function login(request: LoginRequest): Promise<CurrentUser> {
    const result = await authApi.login(request)

    // 200 但没有响应体（网关吞 body、后端将来改 204 等）时**不能**置为已认证：
    // 那样 LoginView 会先弹"登录成功"，路由守卫随即把用户弹回登录页，而 error 始终为 null——
    // 用户看到的是"提示成功、然后原地回到登录页"，没有任何可自诊断的信息。
    // 宁可在这里明确失败。（原先靠 `if (!user.value) await bootstrap()` 兜住这一点，那个兜底随
    // 两步登录一起被删了，这里用更直接的方式重建。）
    if (!result) {
      const empty: NormalizedApiError = {
        status: 0,
        code: 'EMPTY_LOGIN_RESPONSE',
        message: '登录响应为空，请重试。',
        backendUnavailable: false,
      }
      throw empty
    }

    user.value = result
    status.value = 'authenticated'
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

  /* ------------------------- 密码与会话 ------------------------- */

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
    bootstrap,
    ensureBootstrapped,
    setUnauthenticated,
    installUnauthorizedHandler,
    login,
    logout,
    changePassword,
    listSessions,
    revokeSession,
  }
})
