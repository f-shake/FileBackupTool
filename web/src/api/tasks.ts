/**
 * 06 §4.1 任务端点。
 */
import { del, get, post, put } from './http'
import type {
  BackupRequest,
  BackupTask,
  BackupTaskInput,
  DirValidationResult,
  ValidateDirsRequest,
} from './types'

/** `GET /api/tasks` —— 任务列表（含实时状态、上次备份时间、快照数） */
export function listTasks(): Promise<BackupTask[]> {
  return get<BackupTask[]>('/tasks')
}

/** `POST /api/tasks` —— 新建任务（触发目录关系校验） */
export function createTask(input: BackupTaskInput): Promise<BackupTask> {
  return post<BackupTask>('/tasks', input)
}

/** `GET /api/tasks/{id}` —— 任务详情 */
export function getTask(id: string): Promise<BackupTask> {
  return get<BackupTask>(`/tasks/${encodeURIComponent(id)}`)
}

/** `PUT /api/tasks/{id}` —— 修改任务（触发目录关系校验） */
export function updateTask(id: string, input: BackupTaskInput): Promise<BackupTask> {
  return put<BackupTask>(`/tasks/${encodeURIComponent(id)}`, input)
}

/** `DELETE /api/tasks/{id}` —— 软删除任务（快照保留，blob 交 GC） */
export function deleteTask(id: string): Promise<void> {
  return del<void>(`/tasks/${encodeURIComponent(id)}`)
}

/** `POST /api/tasks/{id}/enable` —— 启用 / 禁用 */
export function setTaskEnabled(id: string, enabled: boolean): Promise<BackupTask> {
  return post<BackupTask>(`/tasks/${encodeURIComponent(id)}/enable`, { enabled })
}

/** `POST /api/tasks/validate-dirs` —— 目录关系预校验（表单实时反馈用） */
export function validateDirs(body: ValidateDirsRequest): Promise<DirValidationResult> {
  return post<DirValidationResult>('/tasks/validate-dirs', body)
}

/** `POST /api/tasks/{id}/backup` —— 立即备份（Full / VirtualFull / Increment） */
export function startBackup(id: string, type: BackupRequest['type']): Promise<{ jobId: string; queuePosition?: number | null }> {
  return post<{ jobId: string; queuePosition?: number | null }>(
    `/tasks/${encodeURIComponent(id)}/backup`,
    { type } satisfies BackupRequest,
  )
}

/** `POST /api/tasks/{id}/cancel` —— 取消当前备份 */
export function cancelBackup(id: string): Promise<void> {
  return post<void>(`/tasks/${encodeURIComponent(id)}/cancel`)
}

/** `POST /api/tasks/{id}/scan` —— 手动触发扫描（局部或全量，06 §4.5） */
export function triggerScan(
  id: string,
  body: { full?: boolean; path?: string | null } = {},
): Promise<{ jobId?: string }> {
  return post<{ jobId?: string }>(`/tasks/${encodeURIComponent(id)}/scan`, body)
}
