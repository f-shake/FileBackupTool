/**
 * 06 §4.9 设置端点。
 */
import { get, put } from './http'
import type { AppSettings, ServerInfo } from './types'

/** `GET /api/settings` —— 全局设置（还原根白名单、保留策略等） */
export function getSettings(): Promise<AppSettings> {
  return get<AppSettings>('/settings')
}

/** `PUT /api/settings` —— 保存全局设置 */
export function updateSettings(settings: AppSettings): Promise<AppSettings> {
  return put<AppSettings>('/settings', settings)
}

/** `GET /api/settings/info` —— 版本、数据目录、磁盘占用等只读信息 */
export function getServerInfo(): Promise<ServerInfo> {
  return get<ServerInfo>('/settings/info')
}
