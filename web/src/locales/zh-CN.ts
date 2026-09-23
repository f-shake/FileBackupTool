/**
 * 文案集中地（07 §1：不引入 i18n 框架，但文案集中以便将来抽取）。
 *
 * 这里放**枚举映射与共用文案**；页面内的局部文案仍写在页面里，
 * 只有需要跨页面一致（或将来要做本地化）的才上提。
 */
import type {
  ConflictStrategy,
  FileChangeType,
  JobPhase,
  JobState,
  LogLevel,
  MonitorRootState,
  SnapshotStatus,
  SnapshotType,
  TaskRuntimeStatus,
} from '@/api/types'

export const appTitle = 'FileBackupTool'
export const appSubtitle = '文件备份工具'

/* ------------------------------------------------------------------ */
/* 标签映射                                                            */
/* ------------------------------------------------------------------ */

export const snapshotTypeLabels: Record<SnapshotType, string> = {
  Full: '全量',
  VirtualFull: '虚拟全量',
  Increment: '增量',
}

/**
 * 三种快照类型的帮助文案 —— 对等清单要求「原样保留」。
 * 来源：旧界面的说明文本，语义未变。
 */
export const snapshotTypeHelp: Record<SnapshotType, string> = {
  Full: '全量备份：把源目录中所有符合条件的文件都备份一次，未变更的文件通过内容哈希去重，不产生额外写入。全量备份是增量链的起点。',
  VirtualFull:
    '虚拟全量备份：不在磁盘上复制任何文件，只在数据库中记录一条与全量等价的状态，从而开启一条新的增量链。适合“内容几乎没变但想结束当前增量链”的场景。注意：虚拟全量中的文件没有实际备份文件，若它的 blob 已被回收，则无法还原。',
  Increment:
    '增量备份：只处理相对于上一条快照新增、修改、删除的文件。必须依赖同一条增量链上之前的所有快照才能完整还原。',
}

export const snapshotStatusLabels: Record<SnapshotStatus, string> = {
  InProgress: '进行中',
  Completed: '已完成',
  Aborted: '已中断',
}

export const snapshotStatusTagType: Record<SnapshotStatus, 'success' | 'primary' | 'danger'> = {
  InProgress: 'primary',
  Completed: 'success',
  Aborted: 'danger',
}

export const taskStatusLabels: Record<TaskRuntimeStatus, string> = {
  Ready: '就绪',
  FullBackup: '正在全量备份',
  IncrementalBackup: '正在增量备份',
  Error: '错误',
  Queued: '已排队',
}

export const taskStatusTagType: Record<
  TaskRuntimeStatus,
  'success' | 'warning' | 'danger' | 'info' | 'primary'
> = {
  Ready: 'success',
  FullBackup: 'primary',
  IncrementalBackup: 'primary',
  Error: 'danger',
  Queued: 'warning',
}

export const monitorStateLabels: Record<MonitorRootState, string> = {
  watcher: '实时监控',
  polling: '轮询模式',
  error: '监控异常',
}

export const monitorStateTagType: Record<MonitorRootState, 'success' | 'warning' | 'danger'> = {
  watcher: 'success',
  polling: 'warning',
  error: 'danger',
}

export const fileChangeLabels: Record<FileChangeType, string> = {
  Created: '新增',
  Modified: '修改',
  Deleted: '删除',
}

export const conflictStrategyLabels: Record<ConflictStrategy, string> = {
  Skip: '跳过',
  Overwrite: '覆盖',
  KeepBoth: '保留两者',
  NewerWins: '较新者优先',
}

export const conflictStrategyHelp: Record<ConflictStrategy, string> = {
  Skip: '目标已存在则不动（默认，最不具破坏性）',
  Overwrite: '用备份中的文件覆盖目标文件',
  KeepBoth: '把目标文件重命名为「原名 (1).ext」，两个都留',
  NewerWins: '比较时间戳，新的一方胜出',
}

/**
 * 日志级别文案 —— 沿用旧界面的映射（07 §4.4）。
 * `None` 不会出现在日志里，仅为类型完备保留。
 */
export const logLevelLabels: Record<LogLevel, string> = {
  Trace: '细节',
  Debug: '调试',
  Information: '信息',
  Warning: '警告',
  Error: '错误',
  Critical: '关键',
  None: '全部',
}

/** 下拉选项的取值顺序与旧界面一致，外加「全部」 */
export const logLevelOptions: { value: LogLevel | 'All'; label: string }[] = [
  { value: 'All', label: '全部' },
  { value: 'Trace', label: '细节' },
  { value: 'Debug', label: '调试' },
  { value: 'Information', label: '信息' },
  { value: 'Warning', label: '警告' },
  { value: 'Error', label: '错误' },
  { value: 'Critical', label: '关键' },
]

/** 级别着色（旧界面无着色，07 §4.4 补齐） */
export const logLevelColor: Record<LogLevel, string> = {
  Trace: 'var(--el-text-color-placeholder)',
  Debug: 'var(--el-text-color-secondary)',
  Information: 'var(--el-text-color-primary)',
  Warning: 'var(--el-color-warning)',
  Error: 'var(--el-color-danger)',
  Critical: 'var(--el-color-danger)',
  None: 'var(--el-text-color-secondary)',
}

export const logLevelTagType: Record<LogLevel, 'info' | 'primary' | 'warning' | 'danger'> = {
  Trace: 'info',
  Debug: 'info',
  Information: 'primary',
  Warning: 'warning',
  Error: 'danger',
  Critical: 'danger',
  None: 'info',
}

export const jobPhaseLabels: Record<JobPhase, string> = {
  Enumerating: '正在枚举文件',
  Processing: '正在处理文件',
  Committing: '正在写入数据库',
  Manifest: '正在生成抢救清单',
  Collecting: '正在回收',
}

export const jobStateLabels: Record<JobState, string> = {
  Queued: '排队中',
  Running: '运行中',
  Completed: '已完成',
  Failed: '失败',
  Aborted: '已中止',
}

export const jobTypeLabels: Record<string, string> = {
  Backup: '备份',
  Restore: '还原',
  Verify: '完整校验',
  Collect: '回收',
  SnapshotDelete: '删除快照',
  Manifest: '生成清单',
}

/**
 * 审计事件类型 → 中文。仅用于显示，但**键名是契约**。
 *
 * ⚠️ 键必须与后端 `AuditEventNames`（`src/FileBackupTool.Api/Infrastructure/AuditLogger.cs`）
 * 的字符串**逐字一致**，顺序也照抄那个枚举。理由有两层，都别凭直觉改：
 *   1. `AuditLog.EventType` 以 int 落库，出参由后端映射成这些字符串。键写错 → 走
 *      `AuditView` 的 `?? type` 兜底，页面上显示**原始英文码**（中文标签永远不生效）。
 *   2. `AuditView` 的筛选下拉是直接由本字典的键生成的，选中即作为 `?eventType=` 发给后端；
 *      而后端对认不出的名字**抛 400**（不静默降级），于是"下拉里有这一项，选了就报错"。
 *
 * ⚠️ `TOTP_*` / `RECOVERY_CODE*` 这几条**保留但不再产生新事件**：登录已改为单密码，
 * 后端不再写它们（`AuditEventType` 的枚举成员与后端映射**故意保留**，因为历史
 * `AuditLog` 行里存的是对应的 int，删掉枚举会让旧记录退化成数字或被拒收）。
 * 标签留着，历史行才能正常显示中文。
 */
export const auditEventLabels: Record<string, string> = {
  LOGIN_SUCCESS: '登录成功',
  LOGIN_FAILED: '登录失败',
  TOTP_SUCCESS: 'TOTP 校验成功',
  TOTP_FAILED: 'TOTP 校验失败',
  SESSION_CREATED: '会话创建',
  SESSION_REVOKED: '会话踢出',
  PASSWORD_CHANGED: '密码修改',
  TOTP_REBOUND: 'TOTP 重绑',
  RECOVERY_CODE_USED: '恢复码使用',
  RECOVERY_CODES_REGENERATED: '恢复码重新生成',
  TASK_CREATED: '新建任务',
  TASK_UPDATED: '任务配置变更',
  TASK_DELETED: '删除任务',
  SNAPSHOT_DELETE: '快照删除',
  RESTORE_EXECUTE: '还原执行',
  RECYCLE_BIN_CLEAR: '清空回收站',
  SETTINGS_CHANGED: '设置变更',
}

/* ------------------------------------------------------------------ */
/* 共用提示文案                                                        */
/* ------------------------------------------------------------------ */

export const copy = {
  /** 接口不可用时的统一措辞，避免各页面各写一套 */
  backendUnavailable: '后端服务不可用',
  retry: '重试',
  refresh: '刷新',
  loading: '加载中…',
  empty: '暂无数据',
  cancel: '取消',
  confirm: '确定',
  save: '保存',
  close: '关闭',

  /** 长任务一律有进度、可取消（07 §7） */
  queueNotice: (position: number) =>
    position > 1 ? `已排队，前面还有 ${position - 1} 个作业` : '已加入队列，即将开始',

  /** 「成功但不完整」必须醒目（07 §7） */
  partialSuccess: (failedCount: number) => `成功（${failedCount} 个文件失败）`,

  pathCopied: '路径已复制',
  hashCopied: '哈希已复制',
  nameCopied: '文件名已复制',

  destructiveConfirmTitle: '请确认',
  irreversible: '此操作不可逆。',
}
