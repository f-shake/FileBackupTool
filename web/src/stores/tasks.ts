/**
 * 任务状态（07 §4.1）。
 *
 * 进度走 SignalR 的 `backup.progress`（两段式，06 §6）。
 * 断线重连后依据 resync 信号重新拉取全量状态（07 §6）。
 */
import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { asApiError, monitoringApi, tasksApi } from '@/api'
import type {
  BackupProgressEvent,
  BackupTask,
  BackupTaskInput,
  DirValidationResult,
  JobProgress,
  NormalizedApiError,
  TaskRuntimeStatus,
} from '@/api/types'
import { useRealtimeStore } from './realtime'

/** 卡片上展示的实时进度 */
export interface LiveProgress {
  taskId: string
  jobId: string
  progress: JobProgress
}

export const useTasksStore = defineStore('tasks', () => {
  const tasks = ref<BackupTask[]>([])
  const loading = ref(false)
  const error = ref<NormalizedApiError | null>(null)
  /** jobId → 最近一次进度事件 */
  const progressByJob = ref<Record<string, BackupProgressEvent>>({})
  /** jobId → taskId。由 `job.started` 给出，并由 `rehydrateRunningJobs()` 从 REST 补齐。 */
  const jobTask = ref<Record<string, string>>({})
  /** taskId → 刚发起的即时操作提示 */
  const notices = ref<Record<string, string>>({})

  const realtime = useRealtimeStore()

  let wired = false

  const byId = computed(() => {
    const map: Record<string, BackupTask> = {}
    for (const t of tasks.value) map[t.id] = t
    return map
  })

  /** taskId → 正在运行的进度 */
  const liveProgress = computed<Record<string, LiveProgress>>(() => {
    const out: Record<string, LiveProgress> = {}
    for (const [jobId, progress] of Object.entries(progressByJob.value)) {
      const taskId = jobTask.value[jobId]
      if (!taskId) continue
      out[taskId] = { taskId, jobId, progress }
    }
    return out
  })

  function isRunning(taskId: string): boolean {
    return taskId in liveProgress.value
  }

  async function load(): Promise<void> {
    loading.value = true
    error.value = null
    try {
      tasks.value = await tasksApi.listTasks()
    } catch (err) {
      error.value = asApiError(err)
    } finally {
      loading.value = false
    }
  }

  /** 静默刷新（轮询 / 重连后），不改动 loading 态以免闪烁 */
  async function refreshQuietly(): Promise<void> {
    try {
      tasks.value = await tasksApi.listTasks()
      error.value = null
    } catch (err) {
      // 静默刷新失败不覆盖已有内容，但要让用户看到通道异常
      if (tasks.value.length === 0) error.value = asApiError(err)
    }
  }

  async function loadOne(id: string): Promise<BackupTask> {
    const task = await tasksApi.getTask(id)
    upsert(task)
    return task
  }

  function upsert(task: BackupTask): void {
    const idx = tasks.value.findIndex((t) => t.id === task.id)
    if (idx >= 0) tasks.value.splice(idx, 1, { ...tasks.value[idx], ...task })
    else tasks.value.push(task)
  }

  async function create(input: BackupTaskInput): Promise<BackupTask> {
    const created = await tasksApi.createTask(input)
    upsert(created)
    return created
  }

  async function update(id: string, input: BackupTaskInput): Promise<BackupTask> {
    const updated = await tasksApi.updateTask(id, input)
    upsert(updated)
    return updated
  }

  async function remove(id: string): Promise<void> {
    await tasksApi.deleteTask(id)
    tasks.value = tasks.value.filter((t) => t.id !== id)
  }

  async function setEnabled(id: string, enabled: boolean): Promise<void> {
    const updated = await tasksApi.setTaskEnabled(id, enabled)
    upsert(updated)
  }

  async function validateDirs(body: {
    taskId?: string | null
    sourceDir: string
    backupDir: string
  }): Promise<DirValidationResult> {
    return tasksApi.validateDirs(body)
  }

  async function startBackup(id: string, type: 'Full' | 'VirtualFull' | 'Increment'): Promise<void> {
    const { jobId, queuePosition } = await tasksApi.startBackup(id, type)
    jobTask.value[jobId] = id
    notices.value[id] =
      queuePosition && queuePosition > 1
        ? `已排队，前面还有 ${queuePosition - 1} 个作业`
        : '已加入队列'
    patchStatus(id, type === 'Full' ? 'FullBackup' : 'IncrementalBackup')
  }

  async function cancelBackup(id: string): Promise<void> {
    await tasksApi.cancelBackup(id)
    notices.value[id] = '已发送取消请求'
  }

  function patchStatus(id: string, status: TaskRuntimeStatus): void {
    const idx = tasks.value.findIndex((t) => t.id === id)
    if (idx >= 0) tasks.value.splice(idx, 1, { ...tasks.value[idx]!, status })
  }

  /** 装配 SignalR 事件 → store。幂等。 */
  function wireRealtime(): void {
    if (wired) return
    wired = true

    realtime.on('job.started', (e) => {
      if (e.taskId) jobTask.value[e.jobId] = e.taskId
    })

    realtime.on('backup.progress', (e) => {
      progressByJob.value = { ...progressByJob.value, [e.jobId]: e }
    })

    realtime.on('job.queued', (e) => {
      // 排队位置变化
      if (e.position > 0) {
        const taskId = jobTask.value[e.jobId]
        if (taskId) notices.value[taskId] = `已排队，前面还有 ${Math.max(0, e.position - 1)} 个作业`
      }
    })

    const finish = (jobId: string, message: string) => {
      const next = { ...progressByJob.value }
      delete next[jobId]
      progressByJob.value = next
      const taskId = jobTask.value[jobId]
      if (taskId) {
        notices.value = { ...notices.value, [taskId]: message }
        patchStatus(taskId, 'Ready')
      }
      void refreshQuietly()
    }

    realtime.on('job.completed', (e) => {
      const r = e.result
      const suffix = r.failedFileCount > 0 ? `（${r.failedFileCount} 个文件失败）` : ''
      finish(e.jobId, r.success ? `备份完成${suffix}` : `备份未成功${suffix}`)
    })
    realtime.on('job.failed', (e) => finish(e.jobId, `失败：${e.error}`))
    realtime.on('job.aborted', (e) => finish(e.jobId, `已中止：${e.reason}`))

    realtime.on('snapshot.committed', () => {
      // 快照提交 → 刷新列表（快照数与上次备份时间都变了）
      void refreshQuietly()
    })

    realtime.on('monitor.changed', (e) => {
      const idx = tasks.value.findIndex((t) => t.id === e.taskId)
      if (idx >= 0) {
        tasks.value.splice(idx, 1, { ...tasks.value[idx]!, monitorMode: e.rootState })
      }
    })
  }

  /** 供页面在收到 resync 信号时调用 */
  function onResync(): void {
    void refreshQuietly()
    void rehydrateRunningJobs()
  }

  /**
   * 从 REST 补回"哪个作业属于哪个任务"。
   *
   * `jobTask` 原来只由 `job.started` 填充，而那是**一次性**事件：
   * 在作业已经跑起来**之后**才打开或刷新页面的用户永远收不到它，
   * 于是 `liveProgress` 查不到 taskId → 进度条停在不确定态、没有文件计数、
   * 一直空转到作业结束——而 `backup.progress` 事件其实一直在推（只是被丢掉了）。
   * 这正是 07 §6「重连后必须用 REST 拉一次全量状态」要补的那一步。
   */
  async function rehydrateRunningJobs(): Promise<void> {
    try {
      const snapshot = await monitoringApi.listJobs()
      const next = { ...jobTask.value }
      for (const job of [snapshot.running, ...snapshot.queued]) {
        if (job?.taskId) next[job.jobId] = job.taskId
      }
      jobTask.value = next
    } catch {
      // 补不上只是让进度条退化成不确定态，不该打断别的东西（也不该覆盖已有错误态）
    }
  }

  return {
    tasks,
    loading,
    error,
    byId,
    progressByJob,
    liveProgress,
    notices,
    isRunning,
    load,
    refreshQuietly,
    loadOne,
    upsert,
    create,
    update,
    remove,
    setEnabled,
    validateDirs,
    startBackup,
    cancelBackup,
    wireRealtime,
    onResync,
  }
})
