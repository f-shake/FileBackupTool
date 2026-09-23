/**
 * 06 §4.9 认证端点。
 *
 * 单密码模式：登录只需密码，账号由服务端按配置解析。
 * Cookie 载体（HttpOnly + Secure + SameSite=Strict），滑动 7 天 / 绝对 30 天。
 */
import { del, get, post, resetCsrfToken } from './http'
import type { ChangePasswordRequest, CurrentUser, LoginRequest, SessionInfo } from './types'

/**
 * `POST /api/auth/login` —— 登录（限流 + 锁定）
 *
 * 登录成功后**必须作废缓存里的 CSRF 令牌**：服务端的请求令牌与签发时的身份绑定，
 * 匿名时拿到的令牌在登录之后一律无效（服务端会以
 * `meant for a different claims-based user` 拒绝），
 * 表现为"读操作全好、写操作全 400"。这里重置后，下一个写请求会按新身份重取。
 */
export async function login(body: LoginRequest): Promise<CurrentUser> {
  const result = await post<CurrentUser>('/auth/login', body)
  resetCsrfToken()
  return result
}

/** `POST /api/auth/logout` —— 登出 */
export async function logout(): Promise<void> {
  await post<void>('/auth/logout')
  // 身份从"已登录"变回"匿名"，同理作废，否则匿名身份会继承上一个身份的令牌。
  resetCsrfToken()
}

/** `GET /api/auth/me` —— 当前用户信息 */
export function getCurrentUser(): Promise<CurrentUser> {
  return get<CurrentUser>('/auth/me')
}

/** `POST /api/auth/password` —— 改密码（失效全部会话） */
export function changePassword(body: ChangePasswordRequest): Promise<void> {
  return post<void>('/auth/password', body)
}

/** `GET /api/auth/sessions` —— 会话列表 */
export function listSessions(): Promise<SessionInfo[]> {
  return get<SessionInfo[]>('/auth/sessions')
}

/** `DELETE /api/auth/sessions/{id}` —— 踢出某个会话 */
export function revokeSession(id: string): Promise<void> {
  return del<void>(`/auth/sessions/${encodeURIComponent(id)}`)
}
