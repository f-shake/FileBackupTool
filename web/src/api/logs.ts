/**
 * 06 §4.7 日志与审计端点。
 */
import { get } from './http'
import type {
  AuditLogEntry,
  AuditQuery,
  BackupLogDetail,
  BackupLogEntry,
  LogQuery,
  PagedResult,
} from './types'

/** `GET /api/logs` —— 业务日志（类型/时间范围/关键词/分页） */
export function listLogs(query: LogQuery = {}): Promise<PagedResult<BackupLogEntry>> {
  return get<PagedResult<BackupLogEntry>>('/logs', {
    taskId: query.taskId,
    snapshotId: query.snapshotId,
    level: query.level && query.level !== 'All' ? query.level : undefined,
    from: query.from,
    to: query.to,
    keyword: query.keyword,
    page: query.page,
    pageSize: query.pageSize,
  })
}

/** `GET /api/logs/{id}` —— 单条日志详情（含 Detail） */
export function getLogDetail(id: number): Promise<BackupLogDetail> {
  return get<BackupLogDetail>(`/logs/${id}`)
}

/** `GET /api/audit` —— 审计日志（按时间/类型筛选） */
export function listAudit(query: AuditQuery = {}): Promise<PagedResult<AuditLogEntry>> {
  return get<PagedResult<AuditLogEntry>>('/audit', {
    eventType: query.eventType,
    from: query.from,
    to: query.to,
    keyword: query.keyword,
    page: query.page,
    pageSize: query.pageSize,
  })
}
