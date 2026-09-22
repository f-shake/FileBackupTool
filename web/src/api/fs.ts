/**
 * 06 §4.8 文件系统浏览（用于目录选择）。
 * 只返回目录，不返回文件内容（05 §8）。
 */
import { get, post } from './http'
import type {
  DirValidationResult,
  FilterTestRequest,
  FilterTestResult,
  FsEntry,
  FsRoot,
  FsValidateRequest,
} from './types'

/** `GET /api/fs/roots` —— 可浏览的根（驱动器 / 配置的根） */
export function listFsRoots(): Promise<FsRoot[]> {
  return get<FsRoot[]>('/fs/roots')
}

/** `GET /api/fs/list?path=` —— 列出子目录 */
export function listFsDirs(path: string): Promise<FsEntry[]> {
  return get<FsEntry[]>('/fs/list', { path })
}

/** `POST /api/fs/validate` —— 校验目录关系（源 vs 备份 vs 还原根） */
export function validateFsPaths(body: FsValidateRequest): Promise<DirValidationResult> {
  return post<DirValidationResult>('/fs/validate', body)
}

/**
 * `POST /api/fs/filter-test` —— 「使用当前目录测试」（对等清单要求）。
 *
 * ⚠ **该端点在 docs/plans/06-后端与API.md 的 §4 端点清单里没有出现**，是本前端为了
 * 落实对等清单里的「使用当前目录测试 → 返回文件数量与文件大小」而**假定**的接口。
 * 路径与请求/响应形状都需要后端确认；若后端采用别的路径，只需改这一处。
 */
export function testFilter(body: FilterTestRequest): Promise<FilterTestResult> {
  return post<FilterTestResult>('/fs/filter-test', body)
}
