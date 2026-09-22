/**
 * 06 §4.4 还原端点。
 *
 * 语义见 docs/plans/04-还原与快照管理.md：
 *   - 目标路径**必须**落在白名单还原根内（04 §1.3）
 *   - 先预演（plan）后执行（execute），冲突默认「跳过」，绝不静默覆盖（04 §1.4）
 */
import { del, get, post } from './http'
import type { RestoreJobHandle, RestorePlan, RestoreRequest, RestoreRoot } from './types'

/** `GET /api/restore/roots` —— 白名单还原根目录列表 */
export function listRestoreRoots(): Promise<RestoreRoot[]> {
  return get<RestoreRoot[]>('/restore/roots')
}

/** `POST /api/restore/plan` —— 预演：将写入的清单与冲突明细 */
export function planRestore(request: RestoreRequest): Promise<RestorePlan> {
  return post<RestorePlan>('/restore/plan', request)
}

/** `POST /api/restore/execute` —— 执行（入全局队列），返回作业句柄 */
export function executeRestore(
  request: RestoreRequest & { planToken?: string | null },
): Promise<RestoreJobHandle> {
  return post<RestoreJobHandle>('/restore/execute', request)
}

/** `DELETE /api/restore/{jobId}` —— 取消 */
export function cancelRestore(jobId: string): Promise<void> {
  return del<void>(`/restore/${encodeURIComponent(jobId)}`)
}
