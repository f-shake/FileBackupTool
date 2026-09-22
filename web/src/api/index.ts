/**
 * API 门面。所有页面只从这里导入接口函数，便于统一替换（如 Mock 层）。
 */
export * as authApi from './auth'
export * as filesApi from './files'
export * as fsApi from './fs'
export * as integrityApi from './integrity'
export * as logsApi from './logs'
export * as monitoringApi from './monitoring'
export * as restoreApi from './restore'
export * as settingsApi from './settings'
export * as snapshotsApi from './snapshots'
export * as tasksApi from './tasks'

export { asApiError, API_BASE, http, normalizeError, USE_MOCK } from './http'
export type * from './types'
