/**
 * 06 §4.6 完整性端点。
 *
 * 对应旧界面的「整理文件」，拆成两个入口（07 §5 对等清单）：
 *   - 完整校验（verify）：交叉比对 blob 池与数据库记录
 *   - 回收（collect）：把无引用的 blob 移入回收站
 */
import { del, get, post } from './http'
import type { GcResult, IntegrityReport, RecycleBinInfo } from './types'

/**
 * `POST /api/tasks/{id}/verify` —— 完整校验。
 * `deepHashCheck` 默认应传 false：重算哈希在 TB 级极慢，需用户显式勾选（08 §4.2）。
 */
export function verifyTask(
  taskId: string,
  deepHashCheck = false,
): Promise<IntegrityReport> {
  return post<IntegrityReport>(`/tasks/${encodeURIComponent(taskId)}/verify`, { deepHashCheck })
}

/** `POST /api/tasks/{id}/collect` —— 回收（移入回收站） */
export function collectTask(taskId: string): Promise<GcResult> {
  return post<GcResult>(`/tasks/${encodeURIComponent(taskId)}/collect`)
}

/** `GET /api/tasks/{id}/recycle-bin` —— 回收站占用与账本 */
export function getRecycleBin(
  taskId: string,
  query: { page?: number; pageSize?: number } = {},
): Promise<RecycleBinInfo> {
  return get<RecycleBinInfo>(`/tasks/${encodeURIComponent(taskId)}/recycle-bin`, {
    page: query.page,
    pageSize: query.pageSize,
  })
}

/** `DELETE /api/tasks/{id}/recycle-bin` —— 清空回收站（**硬删除，不可逆**，需二次确认） */
export function emptyRecycleBin(taskId: string): Promise<void> {
  return del<void>(`/tasks/${encodeURIComponent(taskId)}/recycle-bin`)
}
