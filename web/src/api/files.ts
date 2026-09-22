/**
 * 06 §4.3 文件与历史。
 */
import { get } from './http'
import type {
  FileHistoryResult,
  FileMetadata,
  MirrorEntry,
  PagedResult,
  SearchQuery,
} from './types'

/** `GET /api/tasks/{id}/file-history?path=` —— 该路径的所有版本 + 快照时间 */
export function getFileHistory(taskId: string, path: string): Promise<FileHistoryResult> {
  return get<FileHistoryResult>(`/tasks/${encodeURIComponent(taskId)}/file-history`, { path })
}

/** `GET /api/snapshots/{sid}/file?path=` —— 单文件元数据 */
export function getFileMetadata(snapshotId: number, path: string): Promise<FileMetadata> {
  return get<FileMetadata>(`/snapshots/${snapshotId}/file`, { path })
}

/** `GET /api/tasks/{id}/search` —— 按文件名搜索（真过滤，在指定快照的镜像中） */
export function searchMirror(
  taskId: string,
  query: SearchQuery,
): Promise<PagedResult<MirrorEntry>> {
  return get<PagedResult<MirrorEntry>>(`/tasks/${encodeURIComponent(taskId)}/search`, {
    snapshotId: query.snapshotId,
    path: query.path,
    keyword: query.keyword,
    regex: query.regex,
    caseSensitive: query.caseSensitive,
    page: query.page,
    pageSize: query.pageSize,
  })
}
