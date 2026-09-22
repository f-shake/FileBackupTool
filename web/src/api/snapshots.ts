/**
 * 06 §4.2 快照端点。
 */
import { del, get } from './http'
import type {
  DeleteImpact,
  MirrorListing,
  PagedResult,
  SnapshotChanges,
  SnapshotDetail,
  SnapshotGroup,
  SnapshotListQuery,
  TreeNode,
} from './types'

/** `GET /api/tasks/{id}/snapshots` —— 按全量分组，支持类型/日期筛选与分页 */
export function listSnapshots(
  taskId: string,
  query: SnapshotListQuery = {},
): Promise<PagedResult<SnapshotGroup>> {
  return get<PagedResult<SnapshotGroup>>(`/tasks/${encodeURIComponent(taskId)}/snapshots`, {
    page: query.page,
    pageSize: query.pageSize,
    type: query.type && query.type !== 'All' ? query.type : undefined,
    from: query.from,
    to: query.to,
  })
}

/** `GET /api/snapshots/{sid}` —— 快照详情（计数、失败数、占用空间） */
export function getSnapshot(snapshotId: number): Promise<SnapshotDetail> {
  return get<SnapshotDetail>(`/snapshots/${snapshotId}`)
}

/** `GET /api/snapshots/{sid}/delete-impact` —— 级联删除影响清单（删除前**必须**调） */
export function getDeleteImpact(snapshotId: number): Promise<DeleteImpact> {
  return get<DeleteImpact>(`/snapshots/${snapshotId}/delete-impact`)
}

/** `DELETE /api/snapshots/{sid}` —— 删除（级联） */
export function deleteSnapshot(snapshotId: number): Promise<void> {
  return del<void>(`/snapshots/${snapshotId}`)
}

/** `GET /api/snapshots/{sid}/changes` —— 变更内容（新增/修改/删除三类，分页） */
export function getSnapshotChanges(
  snapshotId: number,
  query: { page?: number; pageSize?: number } = {},
): Promise<SnapshotChanges> {
  return get<SnapshotChanges>(`/snapshots/${snapshotId}/changes`, {
    page: query.page,
    pageSize: query.pageSize,
  })
}

/**
 * `GET /api/snapshots/{sid}/files` —— 镜像浏览。
 * 服务端分页（百万级目录无法整体加载，07 §5 的唯一能力取舍）。
 */
export function listMirror(
  snapshotId: number,
  path: string,
  query: { page?: number; pageSize?: number } = {},
): Promise<MirrorListing> {
  return get<MirrorListing>(`/snapshots/${snapshotId}/files`, {
    path: path || '',
    page: query.page,
    pageSize: query.pageSize,
  })
}

/** `GET /api/snapshots/{sid}/tree` —— 目录树骨架（用于导航，不含文件） */
export function getSnapshotTree(snapshotId: number, path = ''): Promise<TreeNode[]> {
  return get<TreeNode[]>(`/snapshots/${snapshotId}/tree`, { path: path || '' })
}
