/**
 * Mock 数据源（仅在 `VITE_USE_MOCK=true` 时装配，**默认关闭**）。
 *
 * 目的：后端尚未实现，用它在浏览器里把页面跑通、验证交互与错误态之外的分支。
 * 不追求语义完备——只是"形状正确"的假数据，绝不能作为契约参考（契约在 ../types.ts）。
 */
import type {
  AppSettings,
  AuditLogEntry,
  BackupLogEntry,
  BackupTask,
  ConflictStrategy,
  CurrentUser,
  FilterRuleSetJson,
  MirrorEntry,
  RecycleBinEntry,
  RootMonitorStatus,
  ServerInfo,
  Snapshot,
  SnapshotType,
  TriggerRecord,
} from '../types'

export const nowIso = (offsetSeconds = 0): string =>
  new Date(Date.now() + offsetSeconds * 1000).toISOString()

export const iso = (daysAgo: number, hour = 3, minute = 0): string => {
  const d = new Date()
  d.setDate(d.getDate() - daysAgo)
  d.setHours(hour, minute, 0, 0)
  return d.toISOString()
}

/* ------------------------------------------------------------------ */
/* 任务                                                                */
/* ------------------------------------------------------------------ */

export const mockTasks: BackupTask[] = [
  {
    id: '8f3a1c40-1111-4a11-9a11-000000000001',
    name: '文档备份',
    sourceDir: 'D:\\Documents',
    backupDir: 'E:\\Backup\\Documents',
    // 库里的形状是扁平的 FilterRuleSetJson（见 ../types.ts），多值用 CRLF 分隔——
    // 这里必须与真实存储一致，否则 mock 下永远看不到"配了规则"的样子。
    filterJson: JSON.stringify({
      IsEnabled: true,
      UseRegex: false,
      IncludeFiles: '*.docx\r\n*.xlsx\r\n*.pdf',
      IncludeFolders: '',
      IncludePaths: '',
      ExcludeFiles: '~$*',
      ExcludeFolders: '~临时',
      ExcludePaths: '',
    } satisfies FilterRuleSetJson),
    byTimeInterval: true,
    timeIntervalMinutes: 120,
    maxAutoIncrementBackupCount: 10,
    isDefaultVirtualBackup: true,
    byWatching: true,
    isEnabled: true,
    createdAt: iso(120),
    updatedAt: iso(2),
    status: 'IncrementalBackup',
    lastBackupTime: iso(0, 3, 0),
    lastBackupResult: {
      status: 'PartialSuccess',
      snapshotId: 12,
      snapshotType: 'Increment',
      failedFileCount: 3,
      time: iso(0, 3, 0),
      message: '7 个文件被独占锁定，3 个因路径过长跳过',
    },
    lastFullBackupTime: iso(5, 2, 0),
    snapshotCount: 12,
    validSnapshotCount: 12,
    monitorMode: 'watcher',
    nextScheduledTime: nowIso(3 * 3600),
    message: '增量链长度 4 / 上限 10',
  },
  {
    id: '8f3a1c40-2222-4a22-9a22-000000000002',
    name: '照片归档（NAS）',
    sourceDir: '\\\\nas01\\photo',
    backupDir: 'E:\\Backup\\Photo',
    filterJson: null,
    byTimeInterval: true,
    timeIntervalMinutes: 1440,
    maxAutoIncrementBackupCount: 30,
    isDefaultVirtualBackup: false,
    byWatching: true,
    isEnabled: true,
    createdAt: iso(300),
    updatedAt: iso(20),
    status: 'Ready',
    lastBackupTime: iso(1, 4, 30),
    lastBackupResult: {
      status: 'Success',
      snapshotId: 31,
      snapshotType: 'Increment',
      failedFileCount: 0,
      time: iso(1, 4, 30),
    },
    lastFullBackupTime: iso(35, 1, 0),
    snapshotCount: 31,
    validSnapshotCount: 31,
    monitorMode: 'polling',
    nextScheduledTime: nowIso(20 * 3600),
    message: 'UNC 源，使用轮询模式（每 3 分钟）',
  },
  {
    id: '8f3a1c40-3333-4a33-9a33-000000000003',
    name: '代码仓库快照',
    sourceDir: 'D:\\Projects',
    backupDir: 'E:\\Backup\\Projects',
    filterJson: null,
    byTimeInterval: false,
    timeIntervalMinutes: 60,
    maxAutoIncrementBackupCount: 5,
    isDefaultVirtualBackup: true,
    byWatching: false,
    isEnabled: false,
    createdAt: iso(60),
    updatedAt: iso(9),
    status: 'Error',
    lastBackupTime: iso(2, 6, 12),
    lastBackupResult: {
      status: 'Aborted',
      snapshotId: 4,
      snapshotType: 'Increment',
      failedFileCount: 0,
      time: iso(2, 6, 12),
      message: '用户取消',
    },
    lastFullBackupTime: iso(9, 0, 0),
    snapshotCount: 4,
    validSnapshotCount: 3,
    monitorMode: 'error',
    nextScheduledTime: null,
    message: '监控异常：FileSystemWatcher 构造失败（句柄耗尽），已回退为 6 小时全量扫描',
  },
]

/* ------------------------------------------------------------------ */
/* 快照                                                                */
/* ------------------------------------------------------------------ */

function makeSnapshot(
  id: number,
  taskId: string,
  type: SnapshotType,
  begin: string,
  opts: Partial<Snapshot> = {},
): Snapshot {
  const end = new Date(new Date(begin).getTime() + 1000 * (type === 'Full' ? 620 : 95)).toISOString()
  return {
    id,
    taskId,
    type,
    status: 'Completed',
    beginTime: begin,
    endTime: end,
    createdFileCount: type === 'Full' ? 4821 : 0,
    modifiedFileCount: type === 'Full' ? 0 : 1,
    deletedFileCount: 0,
    failedFileCount: 0,
    skippedFileCount: 0,
    totalBytes: type === 'Full' ? 18_446_744_073 : 1_048_576,
    abortReason: null,
    ...opts,
  }
}

export const mockSnapshots: Snapshot[] = [
  // 组 1（最新全量）
  makeSnapshot(10, mockTasks[0]!.id, 'Full', iso(5, 2, 0)),
  makeSnapshot(11, mockTasks[0]!.id, 'Increment', iso(3, 2, 10), {
    modifiedFileCount: 42,
    deletedFileCount: 3,
    totalBytes: 734_003_200,
    failedFileCount: 1,
  }),
  makeSnapshot(12, mockTasks[0]!.id, 'Increment', iso(0, 3, 0), {
    status: 'InProgress',
    endTime: null,
    modifiedFileCount: 17,
    totalBytes: 52_428_800,
    failedFileCount: 3,
    abortReason: null,
  }),
  // 组 2（更早的全量）
  makeSnapshot(5, mockTasks[0]!.id, 'VirtualFull', iso(12, 2, 0), {
    createdFileCount: 0,
    modifiedFileCount: 0,
    totalBytes: 0,
  }),
  makeSnapshot(6, mockTasks[0]!.id, 'Increment', iso(10, 2, 5), {
    modifiedFileCount: 8,
    totalBytes: 3_145_728,
  }),
  makeSnapshot(7, mockTasks[0]!.id, 'Increment', iso(8, 2, 5), {
    modifiedFileCount: 121,
    deletedFileCount: 9,
    totalBytes: 2_147_483_648,
    failedFileCount: 12,
  }),
  // 组 3（更早的全量）
  makeSnapshot(1, mockTasks[0]!.id, 'Full', iso(40, 1, 0), {
    createdFileCount: 4711,
    totalBytes: 17_179_869_184,
  }),
  makeSnapshot(2, mockTasks[0]!.id, 'Increment', iso(38, 1, 30), {
    modifiedFileCount: 30,
    totalBytes: 4_194_304,
  }),
  // 第二个任务
  makeSnapshot(30, mockTasks[1]!.id, 'Full', iso(35, 1, 0), {
    createdFileCount: 128_400,
    totalBytes: 549_755_813_888,
  }),
  makeSnapshot(31, mockTasks[1]!.id, 'Increment', iso(1, 4, 30), {
    createdFileCount: 320,
    modifiedFileCount: 4,
    totalBytes: 12_884_901_888,
  }),
  // 第三个任务
  makeSnapshot(3, mockTasks[2]!.id, 'Full', iso(9, 0, 0), {
    createdFileCount: 12_001,
    totalBytes: 2_147_483_648,
  }),
  makeSnapshot(4, mockTasks[2]!.id, 'Increment', iso(2, 6, 12), {
    status: 'Aborted',
    endTime: iso(2, 6, 40),
    abortReason: '用户取消',
    modifiedFileCount: 55,
    totalBytes: 104_857_600,
  }),
]

/* ------------------------------------------------------------------ */
/* 镜像（虚拟目录树）                                                  */
/* ------------------------------------------------------------------ */

interface MockFile {
  path: string
  length: number
  mtime: string
  hash: string | null
}

export function hashOf(seed: string): string {
  // 稳定的假 SHA-256（64 位十六进制）
  let h = 2166136261
  for (let i = 0; i < seed.length; i++) {
    h ^= seed.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  let out = ''
  let x = h >>> 0
  for (let i = 0; i < 8; i++) {
    x = (Math.imul(x, 1103515245) + 12345) >>> 0
    out += x.toString(16).padStart(8, '0')
  }
  return out.slice(0, 64)
}

export const mockFiles: MockFile[] = [
  { path: '报告/2026年度总结.docx', length: 1_258_291, mtime: iso(3, 9, 12), hash: hashOf('报告/2026年度总结.docx') },
  { path: '报告/2026 Q3 财报.xlsx', length: 88_120, mtime: iso(6, 14, 2), hash: hashOf('报告/2026 Q3 财报.xlsx') },
  { path: '报告/附件/明细.csv', length: 4_194_304, mtime: iso(6, 14, 5), hash: hashOf('报告/附件/明细.csv') },
  { path: '合同/供应商A.pdf', length: 2_097_152, mtime: iso(20, 10, 0), hash: hashOf('合同/供应商A.pdf') },
  { path: '合同/供应商B.pdf', length: 1_048_576, mtime: iso(21, 11, 30), hash: hashOf('合同/供应商B.pdf') },
  { path: '会议纪要/2026-09-01.md', length: 12_288, mtime: iso(21, 16, 0), hash: hashOf('会议纪要/2026-09-01.md') },
  { path: '会议纪要/2026-09-15.md', length: 15_360, mtime: iso(7, 16, 0), hash: hashOf('会议纪要/2026-09-15.md') },
  { path: 'README.txt', length: 1024, mtime: iso(100, 8, 0), hash: hashOf('README.txt') },
  { path: '归档/2019.zip', length: 536_870_912, mtime: iso(400, 8, 0), hash: hashOf('归档/2019.zip') },
  { path: '归档/2020.zip', length: 1_073_741_824, mtime: iso(300, 8, 0), hash: hashOf('归档/2020.zip') },
  // 虚拟全量产生的条目：没有实际 blob
  { path: '归档/2021.zip', length: 268_435_456, mtime: iso(200, 8, 0), hash: null },
]

/** 把路径列表展成某个目录下的 `MirrorEntry[]` */
export function listMirrorEntries(dirPath: string): MirrorEntry[] {
  const prefix = dirPath ? `${dirPath.replace(/\/+$/, '')}/` : ''
  const dirs = new Map<string, { files: number; bytes: number; dirs: number; mtime: string }>()
  const files: MirrorEntry[] = []

  for (const f of mockFiles) {
    if (!f.path.startsWith(prefix)) continue
    const rest = f.path.slice(prefix.length)
    const slash = rest.indexOf('/')
    if (slash >= 0) {
      const name = rest.slice(0, slash)
      const cur = dirs.get(name) ?? { files: 0, bytes: 0, dirs: 0, mtime: f.mtime }
      cur.files += 1
      cur.bytes += f.length
      if (new Date(f.mtime) > new Date(cur.mtime)) cur.mtime = f.mtime
      dirs.set(name, cur)
    } else {
      files.push({
        name: rest,
        relativePath: f.path,
        isDirectory: false,
        length: f.length,
        fileTimeUtc: f.mtime,
        contentHash: f.hash,
        restorable: f.hash !== null,
        blockedReason:
          f.hash === null
            ? '该文件不存在实际备份文件，可能是由虚拟快照生成'
            : null,
      })
    }
  }

  // 二次遍历统计子目录数量
  for (const [name] of dirs) {
    const sub = listSubDirs(`${prefix}${name}`)
    const cur = dirs.get(name)!
    cur.dirs = sub.length
  }

  const dirEntries: MirrorEntry[] = [...dirs.entries()].map(([name, v]) => ({
    name,
    relativePath: `${prefix}${name}`,
    isDirectory: true,
    length: v.bytes,
    fileTimeUtc: v.mtime,
    contentHash: null,
    childDirCount: v.dirs,
    childFileCount: v.files,
    childTotalBytes: v.bytes,
  }))

  return [...dirEntries, ...files].sort((a, b) => {
    if (a.isDirectory !== b.isDirectory) return a.isDirectory ? -1 : 1
    return a.name.localeCompare(b.name, 'zh-CN')
  })
}

function listSubDirs(dirPath: string): string[] {
  const prefix = dirPath ? `${dirPath.replace(/\/+$/, '')}/` : ''
  const set = new Set<string>()
  for (const f of mockFiles) {
    if (!f.path.startsWith(prefix)) continue
    const rest = f.path.slice(prefix.length)
    const slash = rest.indexOf('/')
    if (slash >= 0) set.add(rest.slice(0, slash))
  }
  return [...set]
}

/** 目录树骨架 */
export function mockTree(path = ''): { name: string; relativePath: string; hasChildren: boolean; dirCount: number; fileCount: number }[] {
  return listSubDirs(path).map((name) => {
    const rel = path ? `${path}/${name}` : name
    const sub = listSubDirs(rel)
    const entries = listMirrorEntries(rel)
    return {
      name,
      relativePath: rel,
      hasChildren: sub.length > 0,
      dirCount: sub.length,
      fileCount: entries.filter((e) => !e.isDirectory).length,
    }
  })
}

/* ------------------------------------------------------------------ */
/* 日志 / 审计                                                         */
/* ------------------------------------------------------------------ */

export function makeLogs(count = 300, taskId = mockTasks[0]!.id): BackupLogEntry[] {
  const pool: { level: BackupLogEntry['level']; message: string; detail?: string }[] = [
    { level: 'Information', message: '开始增量备份，范围继承自上次全量快照 #10' },
    { level: 'Debug', message: '枚举到变更目录 12 个' },
    { level: 'Information', message: '已发现 17 个新增/修改文件' },
    { level: 'Warning', message: '文件被独占锁定，跳过：D:\\Documents\\~$报告.docx', detail: 'System.IO.IOException: 文件正由另一进程使用\r\n   at System.IO.FileStream..ctor(...)' },
    { level: 'Error', message: '路径过长，无法访问，已计入失败计数：D:\\Documents\\极深目录\\\\...', detail: 'System.IO.PathTooLongException: 指定的路径或文件名太长' },
    { level: 'Critical', message: 'blob 写入失败，磁盘空间不足' },
    { level: 'Information', message: '快照提交完成：新增 0 / 修改 17 / 删除 0 / 失败 3' },
    { level: 'Debug', message: '增量回收：检查 934 个哈希，未发现无引用 blob' },
    { level: 'Trace', message: '内容寻址去重命中 881 次（Length 预筛）' },
    { level: 'Information', message: '抢救清单已重写 3 个分片' },
  ]
  const detailMap: Record<string, string> = {
    '文件被独占锁定，跳过：D:\\Documents\\~$报告.docx':
      'System.IO.IOException: 文件正由另一进程使用，因此该进程无法访问此文件。\r\n' +
      '   at System.IO.FileStream..ctor(String path, FileMode mode, FileAccess access, FileShare share)\r\n' +
      '   at FileBackupTool.Engine.BlobStore.WriteAsync(String sourcePath, String hash, CancellationToken ct)',
    '路径过长，无法访问，已计入失败计数：D:\\Documents\\极深目录\\\\...':
      'System.IO.PathTooLongException: 指定的路径或文件名太长。\r\n' +
      '   at FileBackupTool.Core.Pathing.PathNormalizer.ToExtended(String path)',
  }
  return Array.from({ length: count }, (_, i) => {
    const p = pool[i % pool.length]!
    return {
      id: count - i,
      taskId,
      snapshotId: i % 3 === 0 ? 12 : i % 3 === 1 ? 11 : 10,
      level: p.level,
      time: iso(0, 3, 0).replace(/T.*/, 'T') + new Date(Date.now() - i * 47_000).toISOString().slice(11),
      message: p.message,
      hasDetail: Boolean(p.message in detailMap),
      _detail: detailMap[p.message],
    } as BackupLogEntry & { _detail?: string }
  })
}

/**
 * 审计假数据。
 *
 * ⚠️ `eventType` / `target` / `detail` 一律照**后端真实写入的形状**写，不要凭"看起来合理"编。
 * mock 模式是开发期唯一能看到的形态，假数据与真实契约不一致，会让人照着一份错的形状去对齐
 * （审计页的「目标」列是直接渲染 `target` 的）。
 *
 * ⚠️ 尤其 `target`：后端**只会**写这四种——用户名、任务名、`任务名/快照号`、以及字面量 `全局设置`；
 * 没有 `session:` / `task:` / `snapshot:` 这类前缀。各写入点见
 * `AuthController` / `TasksController` / `SnapshotsController` / `RestoreController` / `SettingsController`
 * 里的 `AuditLogger.WriteAsync` 调用。
 *
 * `detail` 中「会话创建的来源 IP/UA」「还原与快照删除的结果」由控制器动态拼，此处只保证格式像样；
 * 而 `LOGIN_FAILED` / `TASK_UPDATED` 两条的 detail 是**固定格式**，已逐字对齐。
 */
export const mockAudit: AuditLogEntry[] = [
  {
    id: 1,
    time: iso(0, 2, 59),
    // 用 `SESSION_CREATED` 而不是 `LOGIN_SUCCESS`：后者在后端**没有任何写入方**
    //（`AuditEventType.LoginSucceeded` 只有枚举定义与名称映射），成功登录的真实留痕
    // 就是这条会话创建。
    eventType: 'SESSION_CREATED',
    actor: 'admin',
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0',
    // target 是**用户名**；会话 id 在后端属于 detail，不是 target。
    target: 'admin',
    detail: '新会话 8f3a…c40 建立（来源 203.0.113.42）。',
  },
  {
    id: 2,
    time: iso(0, 2, 58),
    // 登录改为单密码后 TOTP 事件不再产生，这里换成后端**真的会写**的事件，
    // 免得这份假数据把"TOTP 事件还会出现"这个错觉传染给照着 mock 对齐契约的人。
    eventType: 'PASSWORD_CHANGED',
    actor: 'admin',
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0',
    target: 'admin',
    detail: '密码已修改，2 个会话被强制失效（含当前会话）。',
  },
  {
    id: 3,
    time: iso(1, 4, 31),
    eventType: 'RESTORE_EXECUTE',
    actor: 'admin',
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) Safari/604.1',
    // 后端写 $"{task.Name}/{SnapshotId}"。
    target: '文档备份/31',
    detail: '成功 320 / 跳过 4 / 失败 0',
  },
  {
    id: 4,
    time: iso(2, 6, 40),
    eventType: 'SNAPSHOT_DELETE',
    actor: 'admin',
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0',
    // 后端写 $"{task.Name}/{sid}"。
    target: '文档备份/4',
    detail: '级联影响 1 个快照',
  },
  {
    id: 5,
    time: iso(3, 1, 2),
    eventType: 'LOGIN_FAILED',
    actor: 'admin',
    sourceIp: '198.51.100.7',
    userAgent: 'curl/8.10.1',
    // 后端写用户名作 target、失败原因作 detail。密码错时原因是固定的「密码不正确」——
    // "还剩几次"只写进日志，**不进审计**，所以这里不能编一个带次数的 detail。
    target: 'admin',
    detail: '密码不正确',
  },
  {
    id: 6,
    time: iso(5, 2, 1),
    eventType: 'TASK_UPDATED',
    actor: 'admin',
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0',
    // 后端写 task.Name 作 target，detail 是固定的配置摘要格式。
    target: '文档备份',
    detail: '源 E:\\Photo；备份 D:\\Backup\\Photo；定时 True；监控 True；启用 True',
  },
]

/* ------------------------------------------------------------------ */
/* 监控 / 作业                                                         */
/* ------------------------------------------------------------------ */

export const mockRoots: RootMonitorStatus[] = [
  {
    taskId: mockTasks[0]!.id,
    taskName: mockTasks[0]!.name,
    sourceDir: mockTasks[0]!.sourceDir,
    state: 'watcher',
    lastScanTime: iso(0, 3, 1),
    nextScanTime: nowIso(6 * 3600),
    pendingCount: 3,
    pendingDirs: [
      {
        relativePath: '报告',
        changedFileCount: 2,
        firstChangeTime: nowIso(-14),
        settleSecondsRemaining: 4,
      },
      {
        relativePath: '会议纪要',
        changedFileCount: 1,
        firstChangeTime: nowIso(-9),
        settleSecondsRemaining: 8,
      },
      {
        relativePath: '合同\\附件',
        changedFileCount: 5,
        firstChangeTime: nowIso(-3),
        settleSecondsRemaining: 14,
      },
    ],
    lastTriggerTime: iso(0, 3, 0),
    lastTriggerReason: '变更合并窗口到期（45s）',
    coalesceSecondsRemaining: 31,
  },
  {
    taskId: mockTasks[1]!.id,
    taskName: mockTasks[1]!.name,
    sourceDir: mockTasks[1]!.sourceDir,
    state: 'polling',
    lastScanTime: iso(0, 4, 27),
    nextScanTime: nowIso(112),
    pendingCount: 0,
    pendingDirs: [],
    lastTriggerTime: iso(1, 4, 30),
    lastTriggerReason: '轮询扫描发现 320 个新文件',
  },
  {
    taskId: mockTasks[2]!.id,
    taskName: mockTasks[2]!.name,
    sourceDir: mockTasks[2]!.sourceDir,
    state: 'error',
    pendingCount: 0,
    pendingDirs: [],
    lastTriggerTime: null,
    lastTriggerReason: null,
    error: 'FileSystemWatcher 构造失败：已达到系统句柄上限，已交由 6 小时全量扫描兜底',
  },
]

export const mockTriggers: TriggerRecord[] = [
  {
    taskId: mockTasks[0]!.id,
    taskName: mockTasks[0]!.name,
    time: iso(0, 3, 0),
    reason: '变更合并窗口到期',
    triggered: true,
    snapshotId: 12,
  },
  {
    taskId: mockTasks[1]!.id,
    taskName: mockTasks[1]!.name,
    time: iso(1, 4, 30),
    reason: '轮询扫描（每 3 分钟）',
    triggered: true,
    snapshotId: 31,
  },
  {
    taskId: mockTasks[0]!.id,
    taskName: mockTasks[0]!.name,
    time: iso(1, 8, 12),
    reason: '全量扫描兜底（6 小时）',
    triggered: false,
    skippedReason: '距上次快照不足最小间隔（静默期保护）',
  },
  {
    taskId: mockTasks[2]!.id,
    taskName: mockTasks[2]!.name,
    time: iso(2, 6, 12),
    reason: '定时备份到期',
    triggered: true,
    snapshotId: 4,
  },
]

/* ------------------------------------------------------------------ */
/* 设置 / 服务器信息 / 用户                                            */
/* ------------------------------------------------------------------ */

export const mockSettings: AppSettings = {
  autoBackupEnabled: true,
  allowedRestoreRoots: ['E:\\Restore', 'E:\\Restore\\Documents', 'F:\\临时还原'],
  retention: { enabled: false, mode: 'Count', value: 20, applyToFullChains: true },
  verifyDeepHashByDefault: false,
  failureRateThresholdPercent: 10,
  lowPriorityIoEnabled: true,
}

export const mockServerInfo: ServerInfo = {
  version: '0.1.0-mock',
  dataDir: 'App_Data',
  logDir: 'App_Data\\logs',
  databasePath: 'App_Data\\filebackuptool.db',
  databaseBytes: 1_342_177_280,
  blobPoolBytes: 1_099_511_627_776,
  recycleBinBytes: 128_849_018_880,
  diskFreeBytes: 2_199_023_255_552,
  diskTotalBytes: 4_000_000_000_000,
  startedAt: iso(3, 0, 0),
  serverTime: nowIso(),
  notices: ['SQLite -wal 边车文件已加入监控排除列表', '数据目录不在任何源目录范围内'],
}

export const mockUser: CurrentUser = {
  id: 'u-1',
  username: 'admin',
  lastLoginTime: iso(0, 2, 59),
  lastLoginIp: '203.0.113.42',
}

export const mockRecycleBin: RecycleBinEntry[] = Array.from({ length: 24 }, (_, i) => ({
  hash: hashOf(`recycled-${i}`),
  length: 1_048_576 * (i + 1),
  movedAt: iso(i % 7, 3, i),
  lastPath: ['报告/旧版/草稿.docx', '归档/2018.zip', '合同/作废.pdf'][i % 3] ?? null,
}))

export const mockRestoreRoots = [
  { path: 'E:\\Restore', label: '还原区（主）', exists: true, freeBytes: 800_000_000_000, totalBytes: 2_000_000_000_000 },
  { path: 'E:\\Restore\\Documents', label: '文档还原', exists: true, freeBytes: 800_000_000_000, totalBytes: 2_000_000_000_000 },
  { path: 'F:\\临时还原', label: '临时还原区', exists: false, freeBytes: null, totalBytes: null },
]

export const mockFsRoots = [
  { path: 'C:\\', name: 'C:', type: 'drive' as const, ready: true },
  { path: 'D:\\', name: 'D:', type: 'drive' as const, ready: true },
  { path: 'E:\\', name: 'E:', type: 'drive' as const, ready: true },
  { path: 'F:\\', name: 'F:', type: 'drive' as const, ready: false },
]

export const mockSessions = [
  {
    id: 's-1',
    createdAt: iso(0, 2, 59),
    lastSeenAt: nowIso(-12),
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0',
    current: true,
  },
  {
    id: 's-2',
    createdAt: iso(1, 4, 20),
    lastSeenAt: iso(1, 4, 40),
    sourceIp: '203.0.113.42',
    userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 19_0 like Mac OS X) Safari/604.1',
    current: false,
  },
  {
    id: 's-3',
    createdAt: iso(14, 9, 0),
    lastSeenAt: iso(9, 18, 0),
    sourceIp: '198.51.100.7',
    userAgent: 'curl/8.10.1',
    current: false,
  },
]

/** 默认冲突策略表（供 mock 的还原预演使用） */
export const defaultStrategy: ConflictStrategy = 'Skip'
