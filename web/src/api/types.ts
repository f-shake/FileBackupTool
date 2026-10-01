/**
 * 与后端 REST API 的完整类型契约。
 *
 * 来源：docs/plans/06-后端与API.md §4（端点清单）、docs/plans/02-存储与数据模型.md（实体）。
 * 后端尚未实现，因此以下类型是**前端的声明**；一旦后端定型，应以此文件为唯一对齐点。
 *
 * 通用约定（06 §4）：
 *   - 统一前缀 `/api`
 *   - 分页统一 `?page=&pageSize=`，响应 `{ items, total, page, pageSize }`
 *   - 错误统一 `{ code, message, detail? }`
 *   - 时间戳一律为 UTC ISO 8601 字符串，UI 侧转本地时区显示
 */

/* ------------------------------------------------------------------ */
/* 通用                                                                */
/* ------------------------------------------------------------------ */

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface PagedQuery {
  page?: number
  pageSize?: number
}

/** 统一错误响应体（06 §4）。`code` 为稳定字符串，前端可据此做本地化。 */
export interface ApiErrorBody {
  code: string
  message: string
  detail?: string
}

/** 前端归一化后的错误对象：网络错误、超时、业务错误统一成这一种形状。 */
export interface NormalizedApiError {
  /** HTTP 状态码；网络层失败时为 0 */
  status: number
  /** 后端 `code`，网络层失败时前端自造（如 `NETWORK_ERROR` / `NOT_IMPLEMENTED`） */
  code: string
  message: string
  detail?: string
  /** 是否为「后端未实现/未部署」（404 / 网络不可达），用于提示文案区分 */
  backendUnavailable: boolean
}

/* ------------------------------------------------------------------ */
/* 枚举（02 §2）                                                       */
/* ------------------------------------------------------------------ */

/** 快照类型：全量 / 虚拟全量 / 增量 */
export type SnapshotType = 'Full' | 'VirtualFull' | 'Increment'

/** 快照状态。取代旧的 `EndTime == default` 隐式哨兵。 */
export type SnapshotStatus = 'InProgress' | 'Completed' | 'Aborted'

/** 文件变更类型 */
export type FileChangeType = 'Created' | 'Modified' | 'Deleted'

/** 手动触发备份的类型（06 §4.1 `POST /api/tasks/{id}/backup` body） */
export type BackupRunType = SnapshotType

/** 任务运行时状态（任务卡片徽章） */
export type TaskRuntimeStatus =
  | 'Ready'
  | 'FullBackup'
  | 'IncrementalBackup'
  | 'Error'
  | 'Queued'

/** 监控根状态（06 §4.5 / SignalR `monitor.changed`） */
export type MonitorRootState = 'watcher' | 'polling' | 'error'

/** 日志级别（微软 LogLevel 的语义，02 §2.4 存 int；此处用名称，两端需约定序列化形式） */
export type LogLevel =
  | 'Trace'
  | 'Debug'
  | 'Information'
  | 'Warning'
  | 'Error'
  | 'Critical'
  | 'None'

/** 还原冲突策略（04 §1.4）。全局默认 `Skip`。 */
export type ConflictStrategy = 'Skip' | 'Overwrite' | 'KeepBoth' | 'NewerWins'

/** 作业状态（06 §4.5 / SignalR 作业事件） */
export type JobState = 'Queued' | 'Running' | 'Completed' | 'Failed' | 'Aborted'

/** 作业类型。全局 IO 队列串行执行（01 §4.1）。 */
export type JobType =
  | 'Backup'
  | 'Restore'
  | 'Verify'
  | 'Collect'
  | 'SnapshotDelete'
  | 'Manifest'

/** 进度阶段（06 §6 两段式进度模型） */
export type JobPhase =
  | 'Enumerating'
  | 'Processing'
  | 'Committing'
  | 'Manifest'
  | 'Collecting'

/** 主题偏好（07 §1） */
export type ThemePreference = 'auto' | 'light' | 'dark'

/** 实时通道（07 §6） */
export type RealtimeChannel = 'realtime' | 'polling'

/* ------------------------------------------------------------------ */
/* 4.1 任务                                                            */
/* ------------------------------------------------------------------ */

/** 上一次备份的结果摘要。失败计数必须显眼（07 §4.1、§7）。 */
export interface LastBackupResult {
  status: 'Success' | 'PartialSuccess' | 'Aborted' | 'Failed'
  snapshotId?: number | null
  snapshotType?: SnapshotType | null
  /** 本次未成功备份的文件数 */
  failedFileCount: number
  time?: string | null
  message?: string | null
}

/**
 * 备份任务定义（02 §2.1）。
 * `status` / `lastBackup*` / `snapshotCount` 等为列表接口附带的实时派生字段。
 */
export interface BackupTask {
  id: string
  name: string
  /** 需要备份的目录（绝对路径） */
  sourceDir: string
  /** 备份文件存放目录（绝对路径），全局唯一 */
  backupDir: string
  /** 序列化的筛选规则。库里存的是 `FilterRuleSetJson` 的扁平形状，见本文件末尾的说明。 */
  filterJson: string | null

  /** 是否启用定时备份 */
  byTimeInterval: boolean
  /** 备份间隔（**分钟**，非时刻） */
  timeIntervalMinutes: number
  /** 增量链长度上限，超过则跑全量 */
  maxAutoIncrementBackupCount: number
  /** 自动全量时使用虚拟全量 */
  isDefaultVirtualBackup: boolean
  /** 是否启用变更监控（旧的 ByWatching 是死配置，新设计真正实现） */
  byWatching: boolean
  /** 任务级启用开关（旧设计缺失） */
  isEnabled: boolean

  createdAt: string
  updatedAt: string

  /* --- 列表/详情附带的实时状态 --- */
  status?: TaskRuntimeStatus
  lastBackupTime?: string | null
  lastBackupResult?: LastBackupResult | null
  /** 对等清单要求：上一次全量备份时间 */
  lastFullBackupTime?: string | null
  snapshotCount?: number
  /** 有效快照数（`Status == Completed`） */
  validSnapshotCount?: number
  /** 监控模式徽章：实时监控 / 轮询模式 */
  monitorMode?: MonitorRootState | null
  /** 下次计划备份时间 */
  nextScheduledTime?: string | null
  /** 「信息」列（旧界面同名列），如上次中断原因 */
  message?: string | null
}

/** 新建 / 修改任务的请求体（02 §2.1 的字段子集，对齐 07 §4.1 编辑表单）。 */
export interface BackupTaskInput {
  name: string
  sourceDir: string
  backupDir: string
  filterJson: string | null
  byTimeInterval: boolean
  timeIntervalMinutes: number
  maxAutoIncrementBackupCount: number
  isDefaultVirtualBackup: boolean
  byWatching: boolean
  isEnabled: boolean
}

/** `POST /api/tasks/validate-dirs` 请求体 */
export interface ValidateDirsRequest {
  taskId?: string | null
  sourceDir: string
  backupDir: string
  /** 同时校验与还原根白名单的关系（02 §7 第 5 条） */
  restoreRoots?: string[]
}

/** 目录关系校验结果（02 §7 五条规则） */
export interface DirValidationResult {
  ok: boolean
  issues: DirValidationIssue[]
}

export interface DirValidationIssue {
  /** 稳定错误码，如 `BACKUP_DIR_INSIDE_SOURCE` */
  code: string
  message: string
  /** 冲突的两个路径，便于 UI 指出是哪两个 */
  pathA?: string
  pathB?: string
  /** 冲突类型说明 */
  kind?: string
}

/** `POST /api/tasks/{id}/backup` 请求体 */
export interface BackupRequest {
  type: BackupRunType
}

/* ------------------------------------------------------------------ */
/* 4.2 快照                                                            */
/* ------------------------------------------------------------------ */

/** 快照（02 §2.2）。时间戳 UTC，UI 转本地显示。 */
export interface Snapshot {
  id: number
  taskId: string
  type: SnapshotType
  status: SnapshotStatus
  beginTime: string
  endTime: string | null
  createdFileCount: number
  modifiedFileCount: number
  deletedFileCount: number
  /** 本次备份中处理失败的文件数（02 新增） */
  failedFileCount: number
  /** 本次实际写入 blob 的字节数（占用空间列） */
  totalBytes: number
  abortReason?: string | null
}

/**
 * 快照按全量分组（04 §2.1）：每个 Full / VirtualFull 开一个组，其后所有 Increment 归入该组。
 * 对应 `ISnapshotService.ListAsync` 的 `PagedResult<SnapshotGroup>`。
 */
export interface SnapshotGroup {
  /** 该组的头（Full 或 VirtualFull） */
  fullSnapshot: Snapshot
  /** 该组内的全部快照，按开始时间升序 */
  snapshots: Snapshot[]
}

/** `GET /api/tasks/{id}/snapshots` 查询参数（补齐项 1：类型/日期筛选 + 分页） */
export interface SnapshotListQuery extends PagedQuery {
  type?: SnapshotType | 'All'
  /** ISO 日期或日期时间，含边界 */
  from?: string
  to?: string
}

/** 快照详情（06 §4.2 `GET /api/snapshots/{sid}`），在 Snapshot 基础上补派生计数 */
export interface SnapshotDetail extends Snapshot {
  taskName?: string
  /** 镜像内文件总数（该快照物化后的状态） */
  mirrorFileCount?: number
  mirrorDirCount?: number
  mirrorTotalBytes?: number
  /** 该快照的 blob 丢失数（"成功但不完整"要醒目） */
  missingBlobCount?: number
}

/**
 * 级联删除影响清单（04 §2.2）。
 * 删除一个快照会级联删除它到下一个 Full/VirtualFull（不含）之间的所有快照。
 * 对话框**必须**列出全部受影响的快照。
 */
export interface DeleteImpact {
  snapshotId: number
  /** 将一并删除的全部快照（含自身），按开始时间升序 */
  affectedSnapshots: Snapshot[]
  affectedCount: number
  /** 涉及的文件记录数 */
  affectedFileCount: number
  /** 其中将被软删除的记录数 */
  softDeletedFileCount: number
  /** 删除后**可能**移入回收站的字节数（软删除不立即释放） */
  reclaimableBytes: number
  /** 链断裂说明 / 其它提示 */
  notices?: string[]
}

/* ------------------------------------------------------------------ */
/* 4.2 变更内容 / 镜像浏览                                             */
/* ------------------------------------------------------------------ */

/** 变更内容一行（04 § 变更内容三列） */
export interface ChangeEntry {
  relativePath: string
  name: string
  type: FileChangeType
  length: number
  fileTimeUtc: string | null
  contentHash: string | null
  /** 「信息」列：如「该文件不存在实际备份文件，可能是由虚拟快照生成」 */
  info?: string | null
}

/** `GET /api/snapshots/{sid}/changes`：新增/修改/删除三类，各类独立分页 */
export interface SnapshotChanges {
  created: PagedResult<ChangeEntry>
  modified: PagedResult<ChangeEntry>
  deleted: PagedResult<ChangeEntry>
}

/** 镜像中的一个条目（目录或文件）。内容寻址后 blob 名即哈希，故只有一列哈希。 */
export interface MirrorEntry {
  name: string
  /** 相对源目录的路径，用 `/` 分隔 */
  relativePath: string
  isDirectory: boolean
  length: number
  fileTimeUtc: string | null
  /** SHA-256 十六进制；目录与虚拟全量条目为 null */
  contentHash: string | null
  /* --- 目录专用（对等清单要求：目录显示 `{D}个子文件夹，{F}个子文件`） --- */
  childDirCount?: number
  childFileCount?: number
  childTotalBytes?: number
  /* --- 可还原性（04 §1.6） --- */
  restorable?: boolean
  /** 不可还原原因：「该文件不存在实际备份文件，可能是由虚拟快照生成」/「…可能是文件丢失」 */
  blockedReason?: string | null
}

/**
 * `GET /api/snapshots/{sid}/files?path=&page=&pageSize=`
 * 在统一分页响应之上附加当前目录的统计（对等清单：网格尾部统计）。
 */
export interface MirrorListing extends PagedResult<MirrorEntry> {
  /** 当前目录的相对路径（根为 `""` 或 `/`） */
  path: string
  /** 当前目录下的子文件夹总数 */
  dirCount: number
  /** 当前目录下的文件总数 */
  fileCount: number
  /** 当前目录下文件的总字节数 */
  totalBytes: number
}

/** `GET /api/snapshots/{sid}/tree` 目录树骨架（用于导航，不含文件） */
export interface TreeNode {
  name: string
  relativePath: string
  hasChildren: boolean
  dirCount: number
  fileCount: number
}

/** 单文件元数据（06 §4.3 `GET /api/snapshots/{sid}/file?path=`） */
export interface FileMetadata {
  snapshotId: number
  relativePath: string
  name: string
  length: number
  fileTimeUtc: string | null
  contentHash: string | null
  /** 所属快照的类型与时间 */
  snapshotType: SnapshotType
  snapshotBeginTime: string
  restorable: boolean
  blockedReason?: string | null
}

/* ------------------------------------------------------------------ */
/* 4.3 文件历史                                                        */
/* ------------------------------------------------------------------ */

/** 某个路径的所有版本（对等清单：文件历史记录 → 快照时间 + 另存为） */
export interface FileVersionEntry {
  snapshotId: number
  snapshotType: SnapshotType
  snapshotBeginTime: string
  snapshotEndTime: string | null
  relativePath: string
  length: number
  fileTimeUtc: string | null
  contentHash: string | null
  changeType: FileChangeType
  restorable: boolean
  blockedReason?: string | null
}

/** `GET /api/tasks/{id}/file-history?path=` 的响应 */
export interface FileHistoryResult {
  relativePath: string
  versions: FileVersionEntry[]
}

/** `GET /api/tasks/{id}/search`（真过滤，非"展开+多选"） */
export interface SearchQuery extends PagedQuery {
  /** 在指定快照的镜像中搜索 */
  snapshotId: number
  /** 限定子树，缺省为全镜像 */
  path?: string
  keyword: string
  /** 是否把关键词当正则 */
  regex?: boolean
  /** 是否大小写敏感（Windows 上默认不敏感） */
  caseSensitive?: boolean
}

/* ------------------------------------------------------------------ */
/* 4.4 还原                                                            */
/* ------------------------------------------------------------------ */

/** 白名单还原根目录（04 §1.3） */
export interface RestoreRoot {
  path: string
  label?: string | null
  exists: boolean
  freeBytes?: number | null
  totalBytes?: number | null
}

/** 还原范围粒度（04 §1.2） */
export type RestoreScope = 'Snapshot' | 'Directory' | 'Files'

/** `POST /api/restore/plan` 请求体 */
export interface RestoreRequest {
  scope: RestoreScope
  snapshotId: number
  /** scope=Directory 时为单个目录相对路径 */
  directory?: string | null
  /** scope=Files 时为多选文件的相对路径集合 */
  paths?: string[]
  /** 白名单内的还原根 */
  targetRoot: string
  /** 目标根下的子路径，缺省为空（直接落到根） */
  targetSubPath?: string | null
  /** 默认冲突策略，缺省 Skip */
  defaultStrategy?: ConflictStrategy
  /** 逐文件覆盖的冲突策略，key 为 relativePath */
  strategies?: Record<string, ConflictStrategy> | null
}

/** 预演结果中的一条（04 §1.5 向导第 3 步：冲突明细） */
export interface RestorePlanItem {
  relativePath: string
  targetPath: string
  length: number
  fileTimeUtc: string | null
  /** 目标是否已存在 */
  conflict: boolean
  /** 冲突时目标文件的现有大小/时间，供用户判断 */
  targetLength?: number | null
  targetFileTimeUtc?: string | null
  /** 按当前策略将执行的动作 */
  action: 'Create' | 'Overwrite' | 'Skip' | 'Rename' | 'Blocked'
  /** 实际生效的冲突策略 */
  strategy: ConflictStrategy
  /** 是否可还原（不可还原见 04 §1.6） */
  restorable: boolean
  blockedReason?: string | null
}

/** 预演结果（`IRestoreService.PlanAsync` → `RestorePlan`） */
export interface RestorePlan {
  /** 预演令牌，执行时回传，避免预演与执行之间的目标漂移 */
  planToken?: string | null
  scope: RestoreScope
  snapshotId: number
  targetRoot: string
  targetSubPath?: string | null
  items: RestorePlanItem[]
  /** 预计写入（含覆盖）的文件数 */
  writeCount: number
  /** 预计跳过的文件数 */
  skipCount: number
  /** 因目标已存在而产生冲突的文件数 */
  conflictCount: number
  /** 不可还原（虚拟全量 / blob 丢失）的文件数 */
  blockedCount: number
  totalBytes: number
  defaultStrategy: ConflictStrategy
  /** 不可还原文件的清单，必须先展示再让用户决定是否继续（04 §1.6） */
  blockedItems: RestorePlanItem[]
}

/** `POST /api/restore/execute` 响应：作业句柄 */
export interface RestoreJobHandle {
  jobId: string
  /** 已排队时前面还有 N 个作业 */
  queuePosition?: number | null
}

/** 还原结果汇总（04 §1.5） */
export interface RestoreResult {
  jobId: string
  succeeded: number
  skipped: number
  failed: number
  totalBytes: number
  elapsedMs?: number | null
  failures: RestoreFailure[]
}

export interface RestoreFailure {
  relativePath: string
  targetPath: string
  error: string
}

/** `restore.progress` SignalR 事件 / 作业进度快照 */
export interface RestoreProgress {
  jobId: string
  processed: number
  total: number
  currentPath: string
}

/* ------------------------------------------------------------------ */
/* 4.5 作业与监控                                                      */
/* ------------------------------------------------------------------ */

/** 两段式进度（06 §6）：枚举阶段不确定态 + 实时计数；处理阶段确定态百分比。 */
export interface JobProgress {
  jobId: string
  phase: JobPhase
  /** 枚举阶段已发现文件数 */
  discovered: number
  processed: number
  /** 处理阶段的总数；枚举阶段为 null（不确定态） */
  total: number | null
  currentPath: string | null
  bytesDone: number
  bytesTotal: number | null
}

/** 作业完成后的结果摘要。不同作业类型填不同的字段。 */
export interface JobResultSummary {
  success?: boolean
  snapshotId?: number | null
  /** 备份：失败文件数 */
  failedFileCount?: number
  /** 还原：成功 / 跳过 / 失败 */
  succeeded?: number
  skipped?: number
  failed?: number
  totalBytes?: number
  failures?: RestoreFailure[]
  message?: string | null
}

/** 队列中的一个作业 */
export interface QueuedJob {
  jobId: string
  type: JobType
  state: JobState
  taskId?: string | null
  taskName?: string | null
  snapshotId?: number | null
  /** 排队位置（1 表示下一个执行） */
  position?: number | null
  enqueuedAt: string
  startedAt?: string | null
  finishedAt?: string | null
  progress?: JobProgress | null
  /** 完成后的结果摘要 */
  result?: JobResultSummary | null
  message?: string | null
  error?: string | null
}

/** `GET /api/jobs` 响应：全局队列只有一个运行中的作业（01 §4.1） */
export interface JobQueueSnapshot {
  running: QueuedJob | null
  queued: QueuedJob[]
}

/** 待处理变更队列中的一条（哪个目录脏了 + 静默期倒计时） */
export interface PendingChangeDir {
  relativePath: string
  changedFileCount: number
  firstChangeTime: string
  /** 静默期剩余秒数（`Monitoring.SettleWindowSeconds`） */
  settleSecondsRemaining: number
}

/** 每个任务/根的监控状态（07 §4.6） */
export interface RootMonitorStatus {
  taskId: string
  taskName: string
  sourceDir: string
  /** `watcher`（实时监控） / `polling`（轮询模式，UNC/NAS 源） / `error`（监控异常） */
  state: MonitorRootState
  lastScanTime?: string | null
  nextScanTime?: string | null
  /** 待处理变更队列长度 */
  pendingCount: number
  pendingDirs: PendingChangeDir[]
  /** 最近一次触发 */
  lastTriggerTime?: string | null
  lastTriggerReason?: string | null
  /** 合并窗口剩余秒数（`Monitoring.CoalesceWindowSeconds`） */
  coalesceSecondsRemaining?: number | null
  error?: string | null
}

/** 最近触发记录 */
export interface TriggerRecord {
  taskId: string
  taskName: string
  time: string
  reason: string
  /** 是否真的触发了备份 */
  triggered: boolean
  /** 未触发时的原因 */
  skippedReason?: string | null
  snapshotId?: number | null
}

/** `GET /api/monitoring` 响应（07 §4.6 全局实时监控页的全部数据） */
export interface MonitoringSnapshot {
  roots: RootMonitorStatus[]
  jobs: JobQueueSnapshot
  recentTriggers: TriggerRecord[]
  /** 服务端当前时间，用于对齐倒计时 */
  serverTime: string
}

/** `POST /api/tasks/{id}/scan` 请求体（手动触发扫描） */
export interface ScanRequest {
  /** 局部扫描（缺省）或全量扫描 */
  full?: boolean
  /** 局部扫描的起始子路径 */
  path?: string | null
}

/* ------------------------------------------------------------------ */
/* 4.6 完整性                                                          */
/* ------------------------------------------------------------------ */

export interface BlobRef {
  hash: string
  length?: number | null
  /** 引用它的记录路径（丢失文件时用） */
  referencedPath?: string | null
  snapshotId?: number | null
}

/** 快照链断裂 */
export interface BrokenChain {
  snapshotId: number
  type: SnapshotType
  reason: string
  expectedPreviousSnapshotId?: number | null
}

/** `POST /api/tasks/{id}/verify` 响应（04 §2.4） */
export interface IntegrityReport {
  taskId: string
  checkedAt: string
  /** 是否重算了哈希（TB 级极慢，默认关闭，需显式勾选） */
  deepHashChecked: boolean
  /** 多余文件：blob 池里有、但没有任何记录引用 */
  orphanBlobs: BlobRef[]
  orphanBytes: number
  /** 丢失文件：有记录、但 blob 既不在池中也不在回收站 */
  missingBlobs: BlobRef[]
  /** 链断裂 */
  brokenChains: BrokenChain[]
  /** 深校验中哈希不匹配的 blob */
  corruptBlobs: BlobRef[]
  /** 文件记录总数 */
  checkedRecordCount: number
  summary: string
}

/** `POST /api/tasks/{id}/collect` 响应（02 §5.4） */
export interface GcResult {
  jobId?: string | null
  movedBlobCount: number
  movedBytes: number
  /** 回收站当前总占用 */
  recycleBinBytes: number
}

/** `GET /api/tasks/{id}/recycle-bin`（02 §5.2 回收账本） */
export interface RecycleBinInfo {
  blobCount: number
  totalBytes: number
  /** 账本条目（分页） */
  entries: RecycleBinEntry[]
  entriesTotal: number
  entriesPage: number
  entriesPageSize: number
  /** 账本是单个 manifest.json，条目数远小于 blob 池 */
  ledgerPath?: string | null
}

export interface RecycleBinEntry {
  hash: string
  length: number
  movedAt: string
  /** 移入回收站前的最后路径 —— 没有它用户只能盲删（02 §5.2） */
  lastPath: string | null
}

/* ------------------------------------------------------------------ */
/* 4.7 日志与审计                                                      */
/* ------------------------------------------------------------------ */

export interface BackupLogEntry {
  id: number
  taskId: string
  snapshotId: number | null
  level: LogLevel
  /** 原始 Microsoft.Extensions.Logging.LogLevel 数值，用于着色排序 */
  levelValue?: number
  time: string
  message: string
  /** 是否有 Detail（决定「详情」按钮是否可用） */
  hasDetail: boolean
}

export interface BackupLogDetail extends BackupLogEntry {
  detail: string | null
}

/** `GET /api/logs` 查询参数（07 §4.4） */
export interface LogQuery extends PagedQuery {
  taskId?: string
  snapshotId?: number
  /** 缺省表示「全部」 */
  level?: LogLevel | 'All'
  /** ISO 8601；UI 用日期时间范围选择器（补齐项 2） */
  from?: string
  to?: string
  /** 关键词，匹配 Message */
  keyword?: string
}

export interface AuditLogEntry {
  id: number
  time: string
  /** 稳定事件类型，如 `LOGIN_SUCCESS` / `SNAPSHOT_DELETE` / `RESTORE_EXECUTE` */
  eventType: string
  actor: string
  sourceIp: string
  userAgent: string
  target: string
  detail: string | null
}

/** `GET /api/audit` 查询参数 */
export interface AuditQuery extends PagedQuery {
  eventType?: string
  from?: string
  to?: string
  keyword?: string
}

/* ------------------------------------------------------------------ */
/* 4.8 文件系统浏览（用于目录选择）                                    */
/* ------------------------------------------------------------------ */

export interface FsRoot {
  path: string
  name: string
  /** 驱动器 / 配置的根 */
  type: 'drive' | 'configured'
  ready: boolean
}

/** `/api/fs/list` 只返回目录，不返回文件内容（05 §8） */
export interface FsEntry {
  name: string
  path: string
  lastWriteTimeUtc: string | null
  hasChildren?: boolean
}

/** `POST /api/fs/validate` 请求体 */
export interface FsValidateRequest {
  sourceDir?: string | null
  backupDir?: string | null
  restoreRoot?: string | null
  taskId?: string | null
}

/**
 * 「使用当前目录测试」请求体（对等清单要求）。
 *
 * ⚠ 该端点**不在 06 §4 的清单里**，是本前端为落实对等清单而假定的接口，
 * 路径为 `POST /api/fs/filter-test`。需后端确认。
 */
export interface FilterTestRequest {
  /** 要测试的目录（通常等于任务的 SourceDir） */
  sourceDir: string
  /** 序列化的筛选规则（与 BackupTask.FilterJson 同一格式） */
  filterJson: string | null
}

/** 「使用当前目录测试」响应 */
export interface FilterTestResult {
  fileCount: number
  totalBytes: number
  /** 匹配到的文件（截断，避免百万级目录把响应撑爆） */
  files: {
    relativePath: string
    length: number
    fileTimeUtc: string | null
  }[]
  /** 扫描过程中跳过的目录数（如无权限） */
  skippedDirCount: number
  /** 是否因结果过多而截断 */
  truncated: boolean
  /** 扫描耗时（毫秒） */
  elapsedMs: number
}

/* ------------------------------------------------------------------ */
/* 筛选规则（07 §4.1 可视化筛选规则编辑器）                            */
/* ------------------------------------------------------------------ */

/** 一组匹配条件：三个文本框，每个可输入多行，任意一行命中即该组命中 */
export interface FilterRuleGroup {
  /** 匹配文件名（含扩展名） */
  file: string
  /**
   * 匹配文件夹名：把路径逐段拆开，**每一级目录名**分别整串匹配。
   * 注意包含源目录之上的祖先目录名。
   */
  folder: string
  /**
   * 匹配**完整路径**（绝对路径）的整串。
   * 实现（FzLib `FileFilterHelper`）会把路径与模式里的 `\` 都换成 `/` 再比，
   * 所以 Windows 写法照常可用；但它**不是**相对源目录的路径。
   */
  path: string
}

/**
 * 编辑器使用的**界面模型**（白名单/黑名单 × 文件/文件夹/路径）。
 * 它不是 `BackupTask.FilterJson` 的存储形状——两者由 `@/utils/filter` 互转。
 */
export interface FileFilterRule {
  whitelist: FilterRuleGroup
  blacklist: FilterRuleGroup
  /** 各条条件是否按正则解释，而非通配符 */
  useRegex: boolean
}

/**
 * `BackupTask.FilterJson` **在库里的实际形状**，与后端
 * `FileBackupTool.Core.Filtering.FilterRuleSet` 逐字对应（FzLib `FileFilterRule` 的序列化形式）。
 *
 * ⚠️ 字段名是 **PascalCase**，且每个字段的值是**多行文本**（条件之间用 CRLF 分隔，
 * 不是 JSON 数组）。曾经前端存的是 `{whitelist,blacklist,useRegex}` 的嵌套形状，
 * 后端认不出、`FromJson` 静默退化为"不过滤"——界面里配的规则被整体忽略。
 * 改动这里必须同步 `web/src/utils/filter.ts` 的往返测试。
 */
export interface FilterRuleSetJson {
  IsEnabled: boolean
  UseRegex: boolean
  IncludeFiles: string
  IncludeFolders: string
  IncludePaths: string
  ExcludeFiles: string
  ExcludeFolders: string
  ExcludePaths: string
}

/* ------------------------------------------------------------------ */
/* 4.9 认证与设置                                                      */
/* ------------------------------------------------------------------ */

/**
 * 登录请求（单密码模式）。
 * 账号不由用户提供：服务端按配置的 SeedUserName 解析出那唯一的账号。
 */
export interface LoginRequest {
  password: string
}

export interface CurrentUser {
  id: string
  username: string
  /** 本会话的建立时间（不是"上次登录历史"，完整历史在审计日志里） */
  lastLoginTime?: string | null
  lastLoginIp?: string | null
}

/** 改密码（改密靠 Identity 安全戳一次性失效全部会话） */
export interface ChangePasswordRequest {
  currentPassword: string
  newPassword: string
  confirmPassword: string
}

export interface SessionInfo {
  id: string
  createdAt: string
  lastSeenAt: string
  sourceIp: string
  userAgent: string
  /** 是否是当前会话（不允许踢出自己） */
  current: boolean
}

/** `GET` / `PUT /api/settings`：全局设置（04 §2.3、05 §1.1） */
export interface AppSettings {
  /** 自动备份总开关（对等清单要求）。关闭后所有定时与监控触发的备份都被暂停。 */
  autoBackupEnabled: boolean
  /** 还原根白名单（**硬约束**，还原绝不接受白名单之外的路径） */
  allowedRestoreRoots: string[]
  /** 保留策略：默认全手动 */
  retention: RetentionSettings
  /** 「完整校验」默认是否重算哈希（默认 false，TB 级极慢） */
  verifyDeepHashByDefault: boolean
  /** 失败率阈值，超过则告警 */
  failureRateThresholdPercent: number
}

/** 保留策略（04 §2.3） */
export interface RetentionSettings {
  /** 默认关闭。开启时必须二次确认。 */
  enabled: boolean
  mode: 'Count' | 'Days' | 'Size'
  value: number
  /** 只对完整的全量链组生效 */
  applyToFullChains: boolean
}

/** `GET /api/settings/info`：只读信息 */
export interface ServerInfo {
  version: string
  dataDir: string
  logDir: string
  databasePath: string
  databaseBytes: number
  /** blob 池占用 */
  blobPoolBytes: number
  /** 回收站占用（**空间未释放，必须可见**） */
  recycleBinBytes: number
  diskFreeBytes: number
  diskTotalBytes: number
  startedAt: string
  serverTime: string
  /** 迁移/健康状态提示 */
  notices?: string[]
}

/* ------------------------------------------------------------------ */
/* SignalR 事件负载（06 §5）                                           */
/* ------------------------------------------------------------------ */

export interface JobQueuedEvent {
  jobId: string
  type: JobType
  position: number
}

export interface JobStartedEvent {
  jobId: string
  type: JobType
  taskId: string | null
}

/** `backup.progress`：两段式（枚举不确定态 → 处理确定态） */
export interface BackupProgressEvent {
  jobId: string
  phase: JobPhase
  discovered: number
  processed: number
  total: number | null
  currentPath: string | null
  bytesDone: number
  bytesTotal: number | null
}

export interface RestoreProgressEvent {
  jobId: string
  processed: number
  total: number
  currentPath: string
}

export interface JobCompletedEvent {
  jobId: string
  /** 完成结果摘要，含失败计数 */
  result: {
    success: boolean
    snapshotId?: number | null
    failedFileCount: number
    createdFileCount?: number
    modifiedFileCount?: number
    deletedFileCount?: number
    totalBytes?: number
    message?: string | null
  }
}

export interface JobFailedEvent {
  jobId: string
  error: string
}

export interface JobAbortedEvent {
  jobId: string
  /** 取消 / 崩溃自愈 */
  reason: string
}

export interface LogAppendedEvent {
  taskId: string
  snapshotId: number | null
  level: LogLevel
  time: string
  message: string
}

export interface MonitorChangedEvent {
  taskId: string
  rootState: MonitorRootState
  pendingCount: number
}

export interface SnapshotCommittedEvent {
  taskId: string
  snapshotId: number
}

/** `snapshot.committed` 之外，删除/中止也会影响列表；用统一的重载信号简化处理。 */
export interface RealtimeResyncReason {
  kind: 'connected' | 'reconnected' | 'polling'
}
