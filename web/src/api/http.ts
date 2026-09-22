import axios, {
  AxiosError,
  type AxiosInstance,
  type AxiosRequestConfig,
  type AxiosResponse,
} from 'axios'
import type { ApiErrorBody, NormalizedApiError } from './types'

/** REST 基础路径。由 Vite 代理到后端（开发期）。 */
export const API_BASE = import.meta.env.VITE_API_BASE || '/api'

/** 是否启用 Mock 层。默认关闭（VITE_USE_MOCK 未设置即为关闭）。 */
export const USE_MOCK = import.meta.env.VITE_USE_MOCK === 'true'

/**
 * antiforgery **请求令牌**（写操作放在 `X-XSRF-TOKEN` 头里）。
 *
 * ⚠️ 它**不是** `XSRF-TOKEN` cookie 的值。ASP.NET Core 用的是"配对令牌"模型：
 * cookie 里存的是 *cookie token*，请求头要带的是**另一个** *request token*，
 * 校验时比较两者的配对关系。所以不能像 Angular 那样"读 cookie 回填请求头"——
 * 那样会得到 `The cookie token and the request token were swapped`，
 * 表现为**所有写操作（含登录）都 400**。请求令牌只能由 `GET /api/auth/csrf`
 * 的**响应体**给出（该端点匿名可访问，所以登录前也能拿到）。
 *
 * 令牌与调用方的 cookie 绑定，因此 `withCredentials: true` 必须保持开启。
 */
let csrfToken: string | null = null
let csrfPending: Promise<string | null> | null = null

async function fetchCsrfToken(): Promise<string | null> {
  try {
    // 用裸 axios，避免走本实例的拦截器（否则再触发一次取令牌 → 递归）
    const res = await axios.get<{ token: string }>(`${API_BASE}/auth/csrf`, {
      withCredentials: true,
      timeout: 15_000,
    })
    return res.data?.token ?? null
  } catch {
    // 拿不到令牌不该让请求直接失败——让它照常发出，由后端以 400 + ANTIFORGERY_FAILED 拒绝，
    // 前端会看到一条明确的错误提示，而不是一个莫名其妙的网络错误。
    return null
  }
}

async function ensureCsrfToken(force = false): Promise<string | null> {
  if (USE_MOCK) return null
  if (force) {
    csrfToken = null
    csrfPending = null
  }
  if (csrfToken) return csrfToken
  if (!csrfPending) {
    csrfPending = fetchCsrfToken().then((token) => {
      csrfToken = token
      csrfPending = null
      return token
    })
  }
  return await csrfPending
}

/**
 * 后端错误统一为 `{ code, message, detail? }`（06 §4）。
 * 网络层失败（后端未部署 / 代理不通 / 404）也要归一化 —— 页面据此渲染错误态，绝不白屏。
 */
export function normalizeError(error: unknown): NormalizedApiError {
  if (axios.isAxiosError(error)) {
    const err = error as AxiosError<ApiErrorBody>
    const status = err.response?.status ?? 0
    const body = err.response?.data

    if (!err.response) {
      const isTimeout = err.code === 'ECONNABORTED'
      return {
        status: 0,
        code: isTimeout ? 'TIMEOUT' : 'NETWORK_ERROR',
        message: isTimeout
          ? '请求超时，后端可能正在处理耗时操作或已无响应'
          : '无法连接后端服务，请确认服务已启动（开发期默认 http://localhost:5000）',
        detail: err.message,
        backendUnavailable: true,
      }
    }

    // 后端尚未实现时，这里是最常见的分支
    if (status === 404) {
      return {
        status,
        code: body?.code || 'NOT_FOUND',
        message: body?.message || '接口不存在（404）——后端可能尚未实现该端点',
        detail: body?.detail,
        backendUnavailable: true,
      }
    }

    if (status === 401) {
      return {
        status,
        code: body?.code || 'UNAUTHORIZED',
        message: body?.message || '登录状态已失效，请重新登录',
        detail: body?.detail,
        backendUnavailable: false,
      }
    }

    if (status === 403) {
      return {
        status,
        code: body?.code || 'FORBIDDEN',
        message: body?.message || '没有权限执行该操作（可能未完成 TOTP 绑定）',
        detail: body?.detail,
        backendUnavailable: false,
      }
    }

    if (status === 429) {
      return {
        status,
        code: body?.code || 'RATE_LIMITED',
        message: body?.message || '请求过于频繁，请稍后再试',
        detail: body?.detail,
        backendUnavailable: false,
      }
    }

    if (status >= 500) {
      return {
        status,
        code: body?.code || 'SERVER_ERROR',
        message: body?.message || `服务端错误（${status}）`,
        detail: body?.detail,
        backendUnavailable: false,
      }
    }

    return {
      status,
      code: body?.code || `HTTP_${status}`,
      message: body?.message || err.message || `请求失败（${status}）`,
      detail: body?.detail,
      backendUnavailable: false,
    }
  }

  if (error instanceof Error) {
    return {
      status: 0,
      code: 'CLIENT_ERROR',
      message: error.message || '发生未知错误',
      detail: error.stack,
      backendUnavailable: false,
    }
  }

  return {
    status: 0,
    code: 'UNKNOWN',
    message: '发生未知错误',
    detail: String(error),
    backendUnavailable: false,
  }
}

export const http: AxiosInstance = axios.create({
  baseURL: API_BASE,
  timeout: 30_000,
  withCredentials: true,
  headers: { Accept: 'application/json' },
})

http.interceptors.request.use(async (config) => {
  // 写操作需要防 CSRF（05 §3：全部写操作启用 antiforgery）。
  const method = (config.method || 'get').toLowerCase()
  if (method !== 'get' && method !== 'head' && method !== 'options') {
    const token = await ensureCsrfToken()
    if (token) {
      config.headers = config.headers ?? {}
      ;(config.headers as Record<string, string>)['X-XSRF-TOKEN'] = token
    }
  }
  return config
})

/** 认证失效的全局通知，由 auth store 订阅后决定是否跳登录页。 */
type UnauthorizedHandler = () => void
let onUnauthorized: UnauthorizedHandler | null = null
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  onUnauthorized = handler
}

http.interceptors.response.use(
  (response: AxiosResponse) => response,
  async (error: unknown) => {
    // 令牌会随服务端轮换/过期（页面开很久之后的第一次写操作最常遇到）。
    // 这时清掉缓存、重取一次、把原请求**重放一次**。
    // 只重试一次：用 `__csrfRetried` 打标，避免"反复 400 → 反复重试"的死循环。
    if (axios.isAxiosError(error) && error.response?.status === 400) {
      const body = error.response.data as ApiErrorBody | undefined
      const config = error.config as (AxiosRequestConfig & { __csrfRetried?: boolean }) | undefined

      if (body?.code === 'ANTIFORGERY_FAILED' && config && !config.__csrfRetried) {
        config.__csrfRetried = true
        const token = await ensureCsrfToken(true)
        if (token) {
          config.headers = config.headers ?? {}
          ;(config.headers as Record<string, string>)['X-XSRF-TOKEN'] = token
          return await http.request(config)
        }
      }
    }

    const normalized = normalizeError(error)
    if (normalized.status === 401) onUnauthorized?.()
    return Promise.reject(normalized)
  },
)

/** 类型化的 GET 包装：失败时抛出 `NormalizedApiError`。 */
export async function get<T>(
  url: string,
  params?: Record<string, unknown>,
  config?: AxiosRequestConfig,
): Promise<T> {
  const res = await http.get<T>(url, { params, ...config })
  return res.data
}

export async function post<T>(
  url: string,
  body?: unknown,
  config?: AxiosRequestConfig,
): Promise<T> {
  const res = await http.post<T>(url, body, config)
  return res.data
}

export async function put<T>(url: string, body?: unknown): Promise<T> {
  const res = await http.put<T>(url, body)
  return res.data
}

export async function del<T>(url: string, params?: Record<string, unknown>): Promise<T> {
  const res = await http.delete<T>(url, { params })
  return res.data
}

/** 把任意抛出物转成可直接展示的 `NormalizedApiError`。 */
export function asApiError(error: unknown): NormalizedApiError {
  if (isNormalizedApiError(error)) return error
  return normalizeError(error)
}

function isNormalizedApiError(value: unknown): value is NormalizedApiError {
  return (
    typeof value === 'object' &&
    value !== null &&
    'code' in value &&
    'message' in value &&
    'backendUnavailable' in value
  )
}
