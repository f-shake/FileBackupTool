/**
 * Mock 适配器：以 axios adapter 的形式拦截请求，返回 fixtures 里的假数据。
 *
 * 装配条件：`VITE_USE_MOCK=true`（默认关闭）。
 * 未命中的路径返回 404 —— 与真实后端"端点尚未实现"的表现一致，便于验证错误态 UI。
 */
import type { AxiosAdapter, AxiosRequestConfig, AxiosResponse, InternalAxiosRequestConfig } from 'axios'
import type {
  BackupTask,
  ConflictStrategy,
  DeleteImpact,
  MirrorListing,
  PagedResult,
  RestorePlan,
  RestorePlanItem,
  Snapshot,
  SnapshotGroup,
} from '../types'
import {
  defaultStrategy,
  hashOf,
  listMirrorEntries,
  makeLogs,
  mockAudit,
  mockFiles,
  mockFsRoots,
  mockRecycleBin,
  mockRestoreRoots,
  mockRoots,
  mockServerInfo,
  mockSessions,
  mockSettings,
  mockSnapshots,
  mockTasks,
  mockTree,
  mockTriggers,
  mockUser,
  nowIso,
  iso,
} from './fixtures'

/* --- 可变状态（页面里的增删改会反映到后续请求） --- */
let tasks: BackupTask[] = JSON.parse(JSON.stringify(mockTasks)) as BackupTask[]
let snapshots: Snapshot[] = JSON.parse(JSON.stringify(mockSnapshots)) as Snapshot[]
let settings = JSON.parse(JSON.stringify(mockSettings))
const jobsInFlight: { jobId: string; type: string; taskId: string; startedAt: number }[] = []

const sleep = (ms: number) => new Promise<void>((r) => setTimeout(r, ms))

function ok<T>(config: AxiosRequestConfig, data: T, status = 200): AxiosResponse<T> {
  return {
    data,
    status,
    statusText: 'OK',
    headers: {},
    config: config as InternalAxiosRequestConfig,
  }
}

function fail(config: AxiosRequestConfig, status: number, body: unknown): AxiosResponse {
  return {
    data: body as never,
    status,
    statusText: 'Error',
    headers: {},
    config: config as InternalAxiosRequestConfig,
  }
}

function paged<T>(items: T[], page = 1, pageSize = 20): PagedResult<T> {
  const p = Math.max(1, page || 1)
  const size = Math.max(1, pageSize || 20)
  return {
    items: items.slice((p - 1) * size, p * size),
    total: items.length,
    page: p,
    pageSize: size,
  }
}

function groupSnapshots(list: Snapshot[]): SnapshotGroup[] {
  const sorted = [...list].sort(
    (a, b) => new Date(a.beginTime).getTime() - new Date(b.beginTime).getTime(),
  )
  const groups: SnapshotGroup[] = []
  for (const s of sorted) {
    if (s.type === 'Full' || s.type === 'VirtualFull') {
      groups.push({ fullSnapshot: s, snapshots: [s] })
    } else if (groups.length > 0) {
      groups[groups.length - 1]!.snapshots.push(s)
    } else {
      // 孤儿增量（理论上不该出现），单独成组以免丢失
      groups.push({ fullSnapshot: s, snapshots: [s] })
    }
  }
  return groups.reverse() // 最新的组在前
}

function computeDeleteImpact(snapshotId: number): DeleteImpact | null {
  const target = snapshots.find((s) => s.id === snapshotId)
  if (!target) return null
  const ordered = [...snapshots]
    .filter((s) => s.taskId === target.taskId)
    .sort((a, b) => new Date(a.beginTime).getTime() - new Date(b.beginTime).getTime())
  const idx = ordered.findIndex((s) => s.id === snapshotId)
  const affected: Snapshot[] = []
  for (let i = idx; i < ordered.length; i++) {
    const s = ordered[i]!
    affected.push(s)
    // 到下一个 Full / VirtualFull（不含）为止
    const next = ordered[i + 1]
    if (!next || next.type === 'Full' || next.type === 'VirtualFull') break
  }
  return {
    snapshotId,
    affectedSnapshots: affected,
    affectedCount: affected.length,
    affectedFileCount: affected.reduce(
      (sum, s) => sum + s.createdFileCount + s.modifiedFileCount + s.deletedFileCount,
      0,
    ),
    softDeletedFileCount: affected.reduce((sum, s) => sum + s.createdFileCount + s.modifiedFileCount, 0),
    reclaimableBytes: affected.reduce((sum, s) => sum + s.totalBytes, 0),
    notices: [
      '软删除不立即释放空间：blob 只有在没有任何记录（含软删除记录）引用时才移入回收站。',
      '删除后请到「回收站」查看占用，并手动清理以释放空间。',
    ],
  }
}

function buildRestorePlan(body: any): RestorePlan {
  const scope: RestorePlan['scope'] = body?.scope ?? 'Snapshot'
  const strategy: ConflictStrategy = body?.defaultStrategy ?? defaultStrategy
  const targetRoot: string = body?.targetRoot ?? 'E:\\Restore'
  const sub: string = body?.targetSubPath ?? ''
  const strategies: Record<string, ConflictStrategy> = body?.strategies ?? {}

  let picked = mockFiles
  if (scope === 'Directory' && body?.directory) {
    const prefix = `${String(body.directory).replace(/\/+$/, '')}/`
    picked = mockFiles.filter((f) => f.path.startsWith(prefix))
  } else if (scope === 'Files' && Array.isArray(body?.paths)) {
    const set = new Set<string>(body.paths)
    picked = mockFiles.filter((f) => set.has(f.path))
  }

  const items: RestorePlanItem[] = picked.map((f, i) => {
    const s = strategies[f.path] ?? strategy
    // 让一部分文件表现为"目标已存在"，以演示冲突明细
    const conflict = i % 4 === 0
    const restorable = f.hash !== null
    let action: RestorePlanItem['action'] = 'Create'
    if (!restorable) action = 'Blocked'
    else if (conflict) {
      action = s === 'Skip' ? 'Skip' : s === 'KeepBoth' ? 'Rename' : 'Overwrite'
    }
    return {
      relativePath: f.path,
      targetPath: `${targetRoot}\\${[sub, f.path].filter(Boolean).join('\\')}`,
      length: f.length,
      fileTimeUtc: f.mtime,
      conflict: conflict && restorable,
      targetLength: conflict ? f.length - 512 : null,
      targetFileTimeUtc: conflict ? f.mtime : null,
      action,
      strategy: s,
      restorable,
      blockedReason: restorable ? null : '该文件不存在实际备份文件，可能是由虚拟快照生成',
    }
  })

  const blockedItems = items.filter((i) => !i.restorable)
  return {
    planToken: `mock-plan-${Date.now()}`,
    scope,
    snapshotId: Number(body?.snapshotId ?? 0),
    targetRoot,
    targetSubPath: sub || null,
    items: items.slice(0, 500),
    writeCount: items.filter((i) => i.action === 'Create' || i.action === 'Overwrite' || i.action === 'Rename').length,
    skipCount: items.filter((i) => i.action === 'Skip').length,
    conflictCount: items.filter((i) => i.conflict).length,
    blockedCount: blockedItems.length,
    totalBytes: items.reduce((s, i) => (i.restorable ? s + i.length : s), 0),
    defaultStrategy: strategy,
    blockedItems,
  }
}

/** 路由表：按注册顺序匹配，第一个命中的生效。 */
const routes: { method: string; pattern: RegExp; handler: (m: RegExpMatchArray, config: AxiosRequestConfig) => unknown }[] = [
  /* ---------------- 认证 ---------------- */
  {
    method: 'post',
    pattern: /^\/auth\/login$/,
    handler: () => mockUser,
  },
  { method: 'post', pattern: /^\/auth\/logout$/, handler: () => null },
  { method: 'get', pattern: /^\/auth\/me$/, handler: () => mockUser },
  { method: 'post', pattern: /^\/auth\/password$/, handler: () => null },
  { method: 'get', pattern: /^\/auth\/sessions$/, handler: () => mockSessions },
  { method: 'delete', pattern: /^\/auth\/sessions\/([^/]+)$/, handler: () => null },

  /* ---------------- 设置 ---------------- */
  {
    method: 'get',
    pattern: /^\/settings\/info$/,
    handler: () => ({ ...mockServerInfo, serverTime: nowIso() }),
  },
  { method: 'get', pattern: /^\/settings$/, handler: () => settings },
  {
    method: 'put',
    pattern: /^\/settings$/,
    handler: (_m, config) => {
      settings = { ...settings, ...(config.data as object) }
      return settings
    },
  },

  /* ---------------- 任务 ---------------- */
  { method: 'get', pattern: /^\/tasks$/, handler: () => tasks },
  {
    method: 'post',
    pattern: /^\/tasks$/,
    handler: (_m, config) => {
      const body = (config.data ?? {}) as Partial<BackupTask>
      const created: BackupTask = {
        id: crypto.randomUUID(),
        name: body.name ?? '新任务',
        sourceDir: body.sourceDir ?? '',
        backupDir: body.backupDir ?? '',
        filterJson: body.filterJson ?? null,
        byTimeInterval: body.byTimeInterval ?? false,
        timeIntervalMinutes: body.timeIntervalMinutes ?? 60,
        maxAutoIncrementBackupCount: body.maxAutoIncrementBackupCount ?? 10,
        isDefaultVirtualBackup: body.isDefaultVirtualBackup ?? false,
        byWatching: body.byWatching ?? false,
        isEnabled: body.isEnabled ?? true,
        createdAt: nowIso(),
        updatedAt: nowIso(),
        status: 'Ready',
        snapshotCount: 0,
        validSnapshotCount: 0,
        monitorMode: null,
        lastBackupTime: null,
        lastFullBackupTime: null,
        lastBackupResult: null,
        nextScheduledTime: null,
        message: '（Mock 新建，未持久化）',
      }
      tasks = [...tasks, created]
      return created
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/validate-dirs$/,
    handler: (_m, config) => {
      const body = (config.data ?? {}) as { sourceDir?: string; backupDir?: string; taskId?: string }
      const issues: unknown[] = []
      const src = (body.sourceDir ?? '').toLowerCase().replace(/\\+$/, '')
      const bak = (body.backupDir ?? '').toLowerCase().replace(/\\+$/, '')
      if (src && bak) {
        if (src === bak) {
          issues.push({ code: 'SAME_DIR', message: '源目录与备份目录不能相同', pathA: body.sourceDir, pathB: body.backupDir, kind: '相同' })
        } else if (bak.startsWith(`${src}\\`)) {
          issues.push({
            code: 'BACKUP_INSIDE_SOURCE',
            message: '备份目录不能位于源目录之内 —— 这会与实时监控叠加成自我触发的死循环',
            pathA: body.backupDir,
            pathB: body.sourceDir,
            kind: '包含关系',
          })
        } else if (src.startsWith(`${bak}\\`)) {
          issues.push({ code: 'SOURCE_INSIDE_BACKUP', message: '源目录不能位于备份目录之内', pathA: body.sourceDir, pathB: body.backupDir, kind: '包含关系' })
        }
        const dup = tasks.find((t) => t.id !== body.taskId && t.backupDir.toLowerCase().replace(/\\+$/, '') === bak)
        if (dup) {
          issues.push({ code: 'BACKUP_DIR_TAKEN', message: `备份目录已被任务「${dup.name}」占用（备份目录全局唯一）`, pathA: body.backupDir, pathB: dup.backupDir, kind: '重复' })
        }
      }
      return { ok: issues.length === 0, issues }
    },
  },
  {
    method: 'get',
    pattern: /^\/tasks\/([^/]+)$/,
    handler: (m) => tasks.find((t) => t.id === m[1]) ?? null,
  },
  {
    method: 'put',
    pattern: /^\/tasks\/([^/]+)$/,
    handler: (m, config) => {
      const body = (config.data ?? {}) as Partial<BackupTask>
      tasks = tasks.map((t) => (t.id === m[1] ? { ...t, ...body, updatedAt: nowIso() } : t))
      return tasks.find((t) => t.id === m[1])
    },
  },
  {
    method: 'delete',
    pattern: /^\/tasks\/([^/]+)$/,
    handler: (m) => {
      tasks = tasks.filter((t) => t.id !== m[1])
      return null
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/enable$/,
    handler: (m, config) => {
      const enabled = Boolean((config.data as { enabled?: boolean })?.enabled)
      tasks = tasks.map((t) => (t.id === m[1] ? { ...t, isEnabled: enabled } : t))
      return tasks.find((t) => t.id === m[1])
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/backup$/,
    handler: (m, config) => {
      const jobId = crypto.randomUUID()
      const idx = tasks.findIndex((t) => t.id === m[1])
      if (idx >= 0) {
        const type = (config.data as { type?: Snapshot['type'] })?.type ?? 'Full'
        tasks = tasks.map((t, i) =>
          i === idx
            ? {
                ...t,
                status: type === 'Full' ? 'FullBackup' : 'IncrementalBackup',
                message: `（Mock）正在执行 ${type}，未真正落盘`,
              }
            : t,
        )
        jobsInFlight.push({ jobId, type, taskId: m[1]!, startedAt: Date.now() })
      }
      return { jobId, queuePosition: 1 }
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/cancel$/,
    handler: (m) => {
      // 取消后任务回到就绪态（TaskRuntimeStatus 没有 Aborted，中止是快照/本次备份的结果状态）
      tasks = tasks.map((t) => (t.id === m[1] ? { ...t, status: 'Ready', message: '（Mock）已取消' } : t))
      return null
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/scan$/,
    handler: () => ({ jobId: crypto.randomUUID() }),
  },

  /* ---------------- 快照 ---------------- */
  {
    method: 'get',
    pattern: /^\/tasks\/([^/]+)\/snapshots$/,
    handler: (m, config) => {
      const params = (config.params ?? {}) as Record<string, string | number | undefined>
      let list = snapshots.filter((s) => s.taskId === m[1])
      if (params.type && params.type !== 'All') list = list.filter((s) => s.type === params.type)
      if (params.from) list = list.filter((s) => new Date(s.beginTime) >= new Date(String(params.from)))
      if (params.to) list = list.filter((s) => new Date(s.beginTime) <= new Date(String(params.to)))
      const groups = groupSnapshots(list)
      return paged(groups, Number(params.page ?? 1), Number(params.pageSize ?? 20))
    },
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)\/delete-impact$/,
    handler: (m) => computeDeleteImpact(Number(m[1])),
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)\/changes$/,
    handler: (m, config) => {
      const sid = Number(m[1])
      const params = (config.params ?? {}) as Record<string, number | undefined>
      const version = sid % 3
      const created = mockFiles.slice(0, 2 + version).map((f) => ({
        relativePath: f.path,
        name: f.path.split('/').pop() ?? f.path,
        type: 'Created' as const,
        length: f.length,
        fileTimeUtc: f.mtime,
        contentHash: f.hash,
        info: null,
      }))
      const modified = mockFiles.slice(2, 5).map((f) => ({
        relativePath: f.path,
        name: f.path.split('/').pop() ?? f.path,
        type: 'Modified' as const,
        length: f.length,
        fileTimeUtc: f.mtime,
        contentHash: f.hash,
        info: null,
      }))
      const deleted = [
        {
          relativePath: '报告/已删除的草稿.docx',
          name: '已删除的草稿.docx',
          type: 'Deleted' as const,
          length: 20_480,
          fileTimeUtc: iso(4, 10, 0),
          contentHash: null,
          info: '该文件在本快照中被删除',
        },
      ]
      return {
        created: paged(created, Number(params.page ?? 1), Number(params.pageSize ?? 100)),
        modified: paged(modified, Number(params.page ?? 1), Number(params.pageSize ?? 100)),
        deleted: paged(deleted, Number(params.page ?? 1), Number(params.pageSize ?? 100)),
      }
    },
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)\/tree$/,
    handler: (_m, config) => mockTree(String((config.params as Record<string, string>)?.path ?? '')),
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)\/file$/,
    handler: (m, config) => {
      const path = String((config.params as Record<string, string>)?.path ?? '')
      const f = mockFiles.find((x) => x.path === path)
      const sid = Number(m[1])
      const snap = snapshots.find((s) => s.id === sid)
      if (!f) return null
      return {
        snapshotId: sid,
        relativePath: f.path,
        name: f.path.split('/').pop() ?? f.path,
        length: f.length,
        fileTimeUtc: f.mtime,
        contentHash: f.hash,
        snapshotType: snap?.type ?? 'Full',
        snapshotBeginTime: snap?.beginTime ?? nowIso(),
        restorable: f.hash !== null,
        blockedReason: f.hash === null ? '该文件不存在实际备份文件，可能是由虚拟快照生成' : null,
      }
    },
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)\/files$/,
    handler: (m, config) => {
      const params = (config.params ?? {}) as Record<string, string | number | undefined>
      const path = String(params.path ?? '')
      const entries = listMirrorEntries(path)
      const page = Number(params.page ?? 1)
      const size = Number(params.pageSize ?? 50)
      const result: MirrorListing = {
        ...paged(entries, page, size),
        path,
        dirCount: entries.filter((e) => e.isDirectory).length,
        fileCount: entries.filter((e) => !e.isDirectory).length,
        totalBytes: entries.filter((e) => !e.isDirectory).reduce((s, e) => s + e.length, 0),
      }
      return result
    },
  },
  {
    method: 'get',
    pattern: /^\/snapshots\/(\d+)$/,
    handler: (m) => {
      const s = snapshots.find((x) => x.id === Number(m[1]))
      if (!s) return null
      const entries = listMirrorEntries('')
      return {
        ...s,
        taskName: tasks.find((t) => t.id === s.taskId)?.name,
        mirrorFileCount: entries.filter((e) => !e.isDirectory).length,
        mirrorDirCount: entries.filter((e) => e.isDirectory).length,
        mirrorTotalBytes: entries.filter((e) => !e.isDirectory).reduce((sum, e) => sum + e.length, 0),
        missingBlobCount: s.id % 5 === 0 ? 2 : 0,
      }
    },
  },
  {
    method: 'delete',
    pattern: /^\/snapshots\/(\d+)$/,
    handler: (m) => {
      const impact = computeDeleteImpact(Number(m[1]))
      const ids = new Set(impact?.affectedSnapshots.map((s) => s.id) ?? [Number(m[1])])
      snapshots = snapshots.filter((s) => !ids.has(s.id))
      tasks = tasks.map((t) => ({
        ...t,
        snapshotCount: snapshots.filter((s) => s.taskId === t.id).length,
        validSnapshotCount: snapshots.filter((s) => s.taskId === t.id && s.status === 'Completed').length,
      }))
      return null
    },
  },

  /* ---------------- 文件与历史 ---------------- */
  {
    method: 'get',
    pattern: /^\/tasks\/([^/]+)\/file-history$/,
    handler: (_m, config) => {
      const path = String((config.params as Record<string, string>)?.path ?? '')
      const f = mockFiles.find((x) => x.path === path)
      const versions = snapshots
        .filter((s) => s.status === 'Completed')
        .slice(0, 5)
        .map((s, i) => ({
          snapshotId: s.id,
          snapshotType: s.type,
          snapshotBeginTime: s.beginTime,
          snapshotEndTime: s.endTime,
          relativePath: path,
          length: f ? f.length - i * 1024 : 0,
          fileTimeUtc: f ? f.mtime : null,
          contentHash: f ? hashOf(`${path}#${s.id}`) : null,
          changeType: i === 0 ? ('Modified' as const) : ('Created' as const),
          restorable: true,
          blockedReason: null,
        }))
      return { relativePath: path, versions }
    },
  },
  {
    method: 'get',
    pattern: /^\/tasks\/([^/]+)\/search$/,
    handler: (_m, config) => {
      const params = (config.params ?? {}) as Record<string, string | number | undefined>
      const kw = String(params.keyword ?? '').toLowerCase()
      const entries = listMirrorEntries('').filter((e) => e.name.toLowerCase().includes(kw))
      const all = listMirrorEntries('报告').concat(listMirrorEntries('合同'), listMirrorEntries('会议纪要'))
      const hits = kw ? [...entries, ...all.filter((e) => e.name.toLowerCase().includes(kw))] : []
      return paged(hits, Number(params.page ?? 1), Number(params.pageSize ?? 50))
    },
  },

  /* ---------------- 还原 ---------------- */
  { method: 'get', pattern: /^\/restore\/roots$/, handler: () => mockRestoreRoots },
  { method: 'post', pattern: /^\/restore\/plan$/, handler: (_m, config) => buildRestorePlan(config.data) },
  {
    method: 'post',
    pattern: /^\/restore\/execute$/,
    handler: () => ({ jobId: crypto.randomUUID(), queuePosition: 1 }),
  },
  { method: 'delete', pattern: /^\/restore\/([^/]+)$/, handler: () => null },

  /* ---------------- 作业与监控 ---------------- */
  {
    method: 'get',
    pattern: /^\/jobs$/,
    handler: () => {
      const running = jobsInFlight[0]
      return {
        running: running
          ? {
              jobId: running.jobId,
              type: running.type,
              state: 'Running',
              taskId: running.taskId,
              taskName: tasks.find((t) => t.id === running.taskId)?.name ?? null,
              enqueuedAt: new Date(running.startedAt).toISOString(),
              startedAt: new Date(running.startedAt).toISOString(),
              progress: {
                jobId: running.jobId,
                phase: 'Processing',
                discovered: 17,
                processed: 9,
                total: 17,
                currentPath: 'D:\\Documents\\报告\\2026年度总结.docx',
                bytesDone: 6_291_456,
                bytesTotal: 52_428_800,
              },
            }
          : null,
        queued: [
          {
            jobId: crypto.randomUUID(),
            type: 'Restore',
            state: 'Queued',
            taskId: tasks[0]?.id ?? null,
            taskName: tasks[0]?.name ?? null,
            position: 1,
            enqueuedAt: nowIso(-40),
          },
          {
            jobId: crypto.randomUUID(),
            type: 'Verify',
            state: 'Queued',
            taskId: tasks[1]?.id ?? null,
            taskName: tasks[1]?.name ?? null,
            position: 2,
            enqueuedAt: nowIso(-12),
          },
        ],
      }
    },
  },
  {
    method: 'get',
    pattern: /^\/jobs\/([^/]+)$/,
    handler: (m) => ({
      jobId: m[1],
      type: 'Backup',
      state: 'Running',
      enqueuedAt: nowIso(-120),
      startedAt: nowIso(-118),
      progress: {
        jobId: m[1],
        phase: 'Enumerating',
        discovered: 123_456,
        processed: 0,
        total: null,
        currentPath: 'D:\\Documents\\归档\\2021.zip',
        bytesDone: 0,
        bytesTotal: null,
      },
    }),
  },
  {
    method: 'get',
    pattern: /^\/monitoring$/,
    handler: () => ({
      roots: mockRoots,
      jobs: {
        running: jobsInFlight[0]
          ? {
              jobId: jobsInFlight[0].jobId,
              type: jobsInFlight[0].type,
              state: 'Running',
              taskId: jobsInFlight[0].taskId,
              enqueuedAt: new Date(jobsInFlight[0].startedAt).toISOString(),
              progress: {
                jobId: jobsInFlight[0].jobId,
                phase: 'Processing',
                discovered: 17,
                processed: 9,
                total: 17,
                currentPath: 'D:\\Documents\\报告\\2026年度总结.docx',
                bytesDone: 6_291_456,
                bytesTotal: 52_428_800,
              },
            }
          : null,
        queued: [],
      },
      recentTriggers: mockTriggers,
      serverTime: nowIso(),
    }),
  },

  /* ---------------- 完整性 ---------------- */
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/verify$/,
    handler: (_m, config) => {
      const deep = Boolean((config.data as { deepHashCheck?: boolean })?.deepHashCheck)
      return {
        taskId: _m[1],
        checkedAt: nowIso(),
        deepHashChecked: deep,
        orphanBlobs: [
          { hash: hashOf('orphan1'), length: 4_194_304, referencedPath: null, snapshotId: null },
          { hash: hashOf('orphan2'), length: 1024, referencedPath: null, snapshotId: null },
        ],
        orphanBytes: 4_195_328,
        missingBlobs: [
          { hash: hashOf('missing1'), length: 1_048_576, referencedPath: '归档/2018.zip', snapshotId: 7 },
        ],
        brokenChains: [{ snapshotId: 12, type: 'Increment', reason: '前一个快照缺失，增量链断裂', expectedPreviousSnapshotId: 11 }],
        corruptBlobs: deep ? [{ hash: hashOf('corrupt'), length: 20_480, referencedPath: 'README.txt', snapshotId: 10 }] : [],
        checkedRecordCount: 13_022,
        summary: deep
          ? '发现 2 个多余文件、1 个丢失文件、1 个哈希不匹配'
          : '发现 2 个多余文件、1 个丢失文件（未重算哈希）',
      }
    },
  },
  {
    method: 'post',
    pattern: /^\/tasks\/([^/]+)\/collect$/,
    handler: () => ({ jobId: crypto.randomUUID(), movedBlobCount: 2, movedBytes: 4_195_328, recycleBinBytes: 128_849_018_880 }),
  },
  {
    method: 'get',
    pattern: /^\/tasks\/([^/]+)\/recycle-bin$/,
    handler: (_m, config) => {
      const params = (config.params ?? {}) as Record<string, number | undefined>
      return {
        blobCount: mockRecycleBin.length,
        totalBytes: mockRecycleBin.reduce((s, e) => s + e.length, 0),
        entries: paged(mockRecycleBin, Number(params.page ?? 1), Number(params.pageSize ?? 20)).items,
        entriesTotal: mockRecycleBin.length,
        entriesPage: Number(params.page ?? 1),
        entriesPageSize: Number(params.pageSize ?? 20),
        ledgerPath: '<BackupDir>\\_deleted\\manifest.json',
      }
    },
  },
  { method: 'delete', pattern: /^\/tasks\/([^/]+)\/recycle-bin$/, handler: () => null },

  /* ---------------- 日志与审计 ---------------- */
  {
    method: 'get',
    pattern: /^\/logs\/(\d+)$/,
    handler: (m) => {
      const all = makeLogs(300)
      const found = all.find((l) => l.id === Number(m[1])) ?? all[0]!
      return {
        ...found,
        detail:
          (found as { _detail?: string })._detail ??
          '（Mock）该日志没有附加明细。真实实现里 Detail 存放异常堆栈等长文本。',
      }
    },
  },
  {
    method: 'get',
    pattern: /^\/logs$/,
    handler: (_m, config) => {
      const params = (config.params ?? {}) as Record<string, string | number | undefined>
      let list = makeLogs(300, String(params.taskId ?? mockTasks[0]!.id))
      if (params.level && params.level !== 'All') list = list.filter((l) => l.level === params.level)
      if (params.snapshotId) list = list.filter((l) => l.snapshotId === Number(params.snapshotId))
      if (params.keyword) {
        const kw = String(params.keyword).toLowerCase()
        list = list.filter((l) => l.message.toLowerCase().includes(kw))
      }
      if (params.from) list = list.filter((l) => new Date(l.time) >= new Date(String(params.from)))
      if (params.to) list = list.filter((l) => new Date(l.time) <= new Date(String(params.to)))
      return paged(list, Number(params.page ?? 1), Number(params.pageSize ?? 100))
    },
  },
  {
    method: 'get',
    pattern: /^\/audit$/,
    handler: (_m, config) => {
      const params = (config.params ?? {}) as Record<string, string | number | undefined>
      let list = [...mockAudit]
      if (params.eventType) list = list.filter((a) => a.eventType === params.eventType)
      if (params.keyword) {
        const kw = String(params.keyword).toLowerCase()
        list = list.filter(
          (a) =>
            a.eventType.toLowerCase().includes(kw) ||
            a.actor.toLowerCase().includes(kw) ||
            a.target.toLowerCase().includes(kw) ||
            (a.detail ?? '').toLowerCase().includes(kw),
        )
      }
      return paged(list, Number(params.page ?? 1), Number(params.pageSize ?? 50))
    },
  },

  /* ---------------- 文件系统浏览 ---------------- */
  { method: 'get', pattern: /^\/fs\/roots$/, handler: () => mockFsRoots },
  {
    method: 'get',
    pattern: /^\/fs\/list$/,
    handler: (_m, config) => {
      const path = String((config.params as Record<string, string>)?.path ?? 'D:\\')
      const base = path.replace(/\\+$/, '')
      const names = path.toLowerCase().startsWith('d:')
        ? ['Documents', 'Projects', 'Archive', 'Temp']
        : ['Restore', 'Backup']
      return names.map((n) => ({
        name: n,
        path: `${base}\\${n}`,
        lastWriteTimeUtc: iso(1, 12, 0),
        hasChildren: true,
      }))
    },
  },
  { method: 'post', pattern: /^\/fs\/validate$/, handler: () => ({ ok: true, issues: [] }) },
  {
    method: 'post',
    pattern: /^\/fs\/filter-test$/,
    handler: () => ({
      fileCount: mockFiles.length,
      totalBytes: mockFiles.reduce((s, f) => s + f.length, 0),
      files: mockFiles.slice(0, 50).map((f) => ({
        relativePath: f.path,
        length: f.length,
        fileTimeUtc: f.mtime,
      })),
      skippedDirCount: 1,
      truncated: mockFiles.length > 50,
      elapsedMs: 128,
    }),
  },
]

/** 构造 axios adapter。 */
export function createMockAdapter(): AxiosAdapter {
  return async (config) => {
    await sleep(120 + Math.random() * 180) // 模拟一点延迟，让加载态可见
    const method = (config.method || 'get').toLowerCase()
    // axios 会把 baseURL 拼在前面，这里统一剥掉
    const raw = config.url ?? ''
    const url = raw.replace(/^https?:\/\/[^/]+/, '').replace(/^\/api/, '').split('?')[0] || '/'

    for (const route of routes) {
      if (route.method !== method) continue
      const m = url.match(route.pattern)
      if (!m) continue
      const data = route.handler(m, config)
      if (data === null || data === undefined) {
        // 约定：handler 返回 null 表示"资源不存在"
        if (/^\/(tasks|snapshots)\/[^/]+$/.test(url)) {
          return fail(config, 404, { code: 'NOT_FOUND', message: '资源不存在' })
        }
        return ok(config, null, 204)
      }
      // 允许返回 [body, status] 形式
      if (Array.isArray(data) && data.length === 2 && typeof data[1] === 'number') {
        return ok(config, data[0], data[1])
      }
      return ok(config, data)
    }

    return fail(config, 404, {
      code: 'NOT_IMPLEMENTED',
      message: `Mock 未覆盖该端点：${method.toUpperCase()} ${url}`,
      detail: '可在 web/src/api/mock/index.ts 的 routes 中补充。',
    })
  }
}
