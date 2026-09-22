<script setup lang="ts">
/**
 * 任务详情 `/tasks/:id`（07 §2、§4）。
 *
 * 头部：任务名 + 快捷操作（立即备份 / 取消 / 刷新）。
 * 内部 Tab：`概览` / `快照` / `日志` / `监控`（Tab 不占路由，只有「快照文件浏览」与「日志」有独立路由）。
 *
 * 概览里并入了旧界面的「最后的日志」，并提供旧界面「整理文件」拆分出的两个入口：
 * 完整校验（verify）与回收（collect），外加回收站。
 */
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ArrowLeft,
  Delete,
  FolderOpened,
  Refresh,
  Search,
  VideoPlay,
} from '@element-plus/icons-vue'
import { asApiError, integrityApi, logsApi, monitoringApi, tasksApi } from '@/api'
import type {
  BackupLogEntry,
  BackupTask,
  GcResult,
  IntegrityReport,
  LogLevel,
  MonitoringSnapshot,
  NormalizedApiError,
  RecycleBinInfo,
  RootMonitorStatus,
  SnapshotType,
} from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import JobProgressBar from '@/components/JobProgressBar.vue'
import PathText from '@/components/PathText.vue'
import SnapshotChangesPanel from '@/components/SnapshotChangesPanel.vue'
import SnapshotListPanel from '@/components/SnapshotListPanel.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import TaskLogsPanel from '@/components/TaskLogsPanel.vue'
import {
  logLevelColor,
  logLevelLabels,
  monitorStateLabels,
  monitorStateTagType,
  taskStatusLabels,
  taskStatusTagType,
} from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { useTasksStore } from '@/stores/tasks'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const props = defineProps<{ id: string }>()

const router = useRouter()
const realtime = useRealtimeStore()
const tasksStore = useTasksStore()

const tab = ref<'overview' | 'snapshots' | 'logs' | 'monitor'>('overview')

const task = ref<BackupTask | null>(null)
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    task.value = await tasksApi.getTask(props.id)
  } catch (err) {
    error.value = asApiError(err)
    task.value = null
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  void load()
  // 进度来自任务 store 的实时事件，需要它先加载过任务列表
  if (tasksStore.tasks.length === 0) void tasksStore.load()
  void realtime.subscribeTask(props.id)
})

// **必须退订。** AppLayout 用 `<component :is="Component" :key="route.path">` 切路由，
// 离开本页即卸载组件；不退订的话服务端分组会一直留着，于是依次看过的**每一个**任务
// 都会把它的 `backup.progress` / `log.appended` 全量推给这个页面，
// 而 `tasksStore.progressByJob` / `notices` 会随访问过的任务数单调增长。
onUnmounted(() => {
  void realtime.unsubscribeTask(props.id)
})

watch(
  () => realtime.resyncTick,
  () => {
    if (!error.value) void load()
  },
)

watch(
  () => props.id,
  (next, previous) => {
    void load()
    // 换任务时**先退旧、再订新**：订阅不去重，只订不退会让这个页面同时留在两个分组里，
    // 服务端于是把同一个事件推两遍。
    if (previous !== undefined && previous !== next) void realtime.unsubscribeTask(previous)
    void realtime.subscribeTask(next)
  },
)

/* ------------------------------- 快捷操作 ------------------------------- */

const cancelling = ref(false)

const liveProgress = computed(() => tasksStore.liveProgress[props.id]?.progress ?? null)
const running = computed(
  () => task.value?.status === 'FullBackup' || task.value?.status === 'IncrementalBackup',
)

async function startBackup(type: SnapshotType): Promise<void> {
  try {
    const result = await tasksApi.startBackup(props.id, type)
    ElMessage.success(result.queuePosition ? `已排队（前面还有 ${result.queuePosition} 个）` : '备份已开始')
    await load()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '启动备份失败')
  }
}

async function cancelBackup(): Promise<void> {
  try {
    await ElMessageBox.confirm('确定要取消当前备份吗？已写入的快照会被标记为「已中止」。', '取消备份', {
      type: 'warning',
      confirmButtonText: '取消备份',
      cancelButtonText: '不取消',
    })
  } catch {
    return
  }
  cancelling.value = true
  try {
    await tasksApi.cancelBackup(props.id)
    ElMessage.success('已请求取消')
    await load()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '取消失败')
  } finally {
    cancelling.value = false
  }
}

/* ------------------------------- 概览：最后的日志 ------------------------------- */

const lastLogs = ref<BackupLogEntry[]>([])
const lastLogsError = ref<NormalizedApiError | null>(null)

async function loadLastLogs(): Promise<void> {
  try {
    const result = await logsApi.listLogs({ taskId: props.id, page: 1, pageSize: 20 })
    lastLogs.value = result.items
    lastLogsError.value = null
  } catch (err) {
    lastLogsError.value = asApiError(err)
    lastLogs.value = []
  }
}

onMounted(loadLastLogs)
watch(
  () => realtime.resyncTick,
  () => {
    void loadLastLogs()
  },
)
realtime.on('log.appended', (payload) => {
  if (payload.taskId !== props.id) return
  lastLogs.value = [
    {
      id: -Date.now(),
      taskId: payload.taskId,
      snapshotId: payload.snapshotId,
      level: payload.level,
      time: payload.time,
      message: payload.message,
      hasDetail: false,
    },
    ...lastLogs.value,
  ].slice(0, 20)
})

/* ------------------------------- 变更内容 ------------------------------- */

const changesSnapshotId = ref<number | null>(null)

function openChanges(snapshotId: number): void {
  changesSnapshotId.value = snapshotId
  tab.value = 'snapshots'
}

/* ------------------------------- 完整校验 ------------------------------- */

const verifyVisible = ref(false)
const verifying = ref(false)
const verifyError = ref<NormalizedApiError | null>(null)
const verifyReport = ref<IntegrityReport | null>(null)
const deepHashCheck = ref(false)

function openVerify(): void {
  verifyVisible.value = true
  verifyReport.value = null
  verifyError.value = null
  deepHashCheck.value = false
}

async function runVerify(): Promise<void> {
  verifying.value = true
  verifyError.value = null
  try {
    verifyReport.value = await integrityApi.verifyTask(props.id, deepHashCheck.value)
  } catch (err) {
    verifyError.value = asApiError(err)
    verifyReport.value = null
  } finally {
    verifying.value = false
  }
}

/* ------------------------------- 回收与回收站 ------------------------------- */

const collecting = ref(false)
const collectResult = ref<GcResult | null>(null)

async function runCollect(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      '回收会把没有任何快照记录引用的 blob 移入回收站（不是硬删除，可回滚）。确定继续吗？',
      '回收多余文件',
      { type: 'warning', confirmButtonText: '开始回收', cancelButtonText: '取消' },
    )
  } catch {
    return
  }
  collecting.value = true
  try {
    collectResult.value = await integrityApi.collectTask(props.id)
    ElMessage.success('回收完成')
  } catch (err) {
    ElMessage.error(asApiError(err).message || '回收失败')
  } finally {
    collecting.value = false
  }
}

const binVisible = ref(false)
const binLoading = ref(false)
const binError = ref<NormalizedApiError | null>(null)
const bin = ref<RecycleBinInfo | null>(null)
const binPage = ref(1)
const emptying = ref(false)

async function openBin(): Promise<void> {
  binVisible.value = true
  await loadBin()
}

async function loadBin(): Promise<void> {
  binLoading.value = true
  binError.value = null
  try {
    bin.value = await integrityApi.getRecycleBin(props.id, { page: binPage.value, pageSize: 100 })
  } catch (err) {
    binError.value = asApiError(err)
    bin.value = null
  } finally {
    binLoading.value = false
  }
}

watch(binPage, () => {
  if (binVisible.value) void loadBin()
})

async function emptyBin(): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `将永久删除回收站中的 ${formatCount(bin.value?.blobCount ?? 0)} 个 blob（${formatBytes(
        bin.value?.totalBytes ?? 0,
      )}）。此操作不可逆，且无法从任何快照还原。`,
      '清空回收站',
      { type: 'error', confirmButtonText: '永久删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }
  emptying.value = true
  try {
    await integrityApi.emptyRecycleBin(props.id)
    ElMessage.success('回收站已清空')
    await loadBin()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '清空失败')
  } finally {
    emptying.value = false
  }
}

/* ------------------------------- 监控 Tab ------------------------------- */

const monitor = ref<MonitoringSnapshot | null>(null)
const monitorLoading = ref(false)
const monitorError = ref<NormalizedApiError | null>(null)

const rootStatus = computed<RootMonitorStatus | null>(() => {
  const roots = monitor.value?.roots ?? []
  return roots.find((r) => r.taskId === props.id) ?? null
})

async function loadMonitor(): Promise<void> {
  monitorLoading.value = true
  monitorError.value = null
  try {
    monitor.value = await monitoringApi.getMonitoring()
  } catch (err) {
    monitorError.value = asApiError(err)
    monitor.value = null
  } finally {
    monitorLoading.value = false
  }
}

watch(tab, (t) => {
  if (t === 'monitor' && !monitor.value) void loadMonitor()
})

// 监控 Tab 打开时才跟随实时推送，避免后台无谓刷新
watch(
  () => realtime.resyncTick,
  () => {
    if (tab.value === 'monitor') void loadMonitor()
  },
)
realtime.on('monitor.changed', (payload) => {
  if (payload.taskId !== props.id || tab.value !== 'monitor') return
  void loadMonitor()
})

const scanning = ref(false)

async function scan(full: boolean): Promise<void> {
  scanning.value = true
  try {
    await tasksApi.triggerScan(props.id, { full })
    ElMessage.success(full ? '已触发全量扫描' : '已触发局部扫描')
    await loadMonitor()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '触发扫描失败')
  } finally {
    scanning.value = false
  }
}

/* 表格插槽里的 row 是 any，用带类型的辅助函数取文案，避免隐式 any 索引报错 */

function logLabel(level: LogLevel): string {
  return logLevelLabels[level]
}

function logColor(level: LogLevel): string {
  return logLevelColor[level] ?? 'inherit'
}
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div class="detail__heading">
        <el-button text :icon="ArrowLeft" @click="router.push({ name: 'tasks' })">任务列表</el-button>
        <h1 class="fbt-page-title">
          {{ task?.name ?? id }}
          <el-tag
            v-if="task?.status"
            :type="taskStatusTagType[task.status]"
            size="small"
            class="detail__status"
          >
            {{ taskStatusLabels[task.status] }}
          </el-tag>
          <el-tag v-if="task && !task.isEnabled" type="info" size="small">已禁用</el-tag>
        </h1>
        <div class="fbt-page-desc">
          <PathText :path="task?.sourceDir" :max-length="60" />
          <span class="fbt-muted"> → </span>
          <PathText :path="task?.backupDir" :max-length="60" />
        </div>
      </div>

      <div class="fbt-toolbar">
        <el-dropdown :disabled="!task" @command="(t: SnapshotType) => startBackup(t)">
          <el-button size="small" type="primary" :icon="VideoPlay">立即备份</el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item command="Full">全量备份</el-dropdown-item>
              <el-dropdown-item command="VirtualFull">虚拟全量备份</el-dropdown-item>
              <el-dropdown-item command="Increment">增量备份</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <el-button size="small" :icon="Delete" :loading="cancelling" :disabled="!running" @click="cancelBackup">
          取消
        </el-button>
        <el-button size="small" :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <ErrorState v-if="error" :error="error" @retry="load" />
    <StatePlaceholder v-else-if="loading && !task" text="正在加载任务…" />

    <template v-else-if="task">
      <el-tabs v-model="tab" class="detail__tabs">
        <!-- ============================ 概览 ============================ -->
        <el-tab-pane label="概览" name="overview">
          <JobProgressBar
            v-if="liveProgress || running"
            :progress="liveProgress"
            class="fbt-section"
          />

          <div class="detail__grid">
            <div class="fbt-pane">
              <div class="fbt-pane-title">任务定义</div>
              <el-descriptions :column="1" size="small" border>
                <el-descriptions-item label="任务名">{{ task.name }}</el-descriptions-item>
                <el-descriptions-item label="需要备份的目录">
                  <PathText :path="task.sourceDir" :max-length="70" />
                </el-descriptions-item>
                <el-descriptions-item label="备份文件存放目录">
                  <PathText :path="task.backupDir" :max-length="70" />
                </el-descriptions-item>
                <el-descriptions-item label="启用">
                  <el-tag :type="task.isEnabled ? 'success' : 'info'" size="small">
                    {{ task.isEnabled ? '已启用' : '已禁用' }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item label="定时备份">
                  <template v-if="task.byTimeInterval">
                    每 {{ task.timeIntervalMinutes }} 分钟
                  </template>
                  <span v-else class="fbt-muted">未开启</span>
                </el-descriptions-item>
                <el-descriptions-item label="增量备份最大数量">
                  {{ task.maxAutoIncrementBackupCount }}
                  <span class="fbt-footnote">
                    （当一个连续的增量备份超过该数量后，将进行一次全量备份）
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="自动全量时使用虚拟全量">
                  {{ task.isDefaultVirtualBackup ? '是' : '否' }}
                </el-descriptions-item>
                <el-descriptions-item label="变更监控">
                  <el-tag :type="task.byWatching ? 'success' : 'info'" size="small">
                    {{ task.byWatching ? '开启' : '关闭' }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item label="创建 / 修改">
                  <span class="fbt-mono">
                    {{ formatDateTime(task.createdAt) }} / {{ formatDateTime(task.updatedAt) }}
                  </span>
                </el-descriptions-item>
              </el-descriptions>
            </div>

            <div class="fbt-pane">
              <div class="fbt-pane-title">运行状态</div>
              <el-descriptions :column="1" size="small" border>
                <el-descriptions-item label="当前状态">
                  <el-tag v-if="task.status" :type="taskStatusTagType[task.status]" size="small">
                    {{ taskStatusLabels[task.status] }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item label="监控模式">
                  <el-tag
                    v-if="task.monitorMode"
                    :type="monitorStateTagType[task.monitorMode]"
                    size="small"
                  >
                    {{ monitorStateLabels[task.monitorMode] }}
                  </el-tag>
                  <span v-else class="fbt-muted">—</span>
                </el-descriptions-item>
                <el-descriptions-item label="快照数 / 有效快照数">
                  {{ formatCount(task.snapshotCount ?? 0) }} / {{ formatCount(task.validSnapshotCount ?? 0) }}
                </el-descriptions-item>
                <el-descriptions-item label="上一次备份时间">
                  <span class="fbt-mono">
                    {{ task.lastBackupTime ? formatDateTime(task.lastBackupTime) : '—' }}
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="上一次备份结果">
                  <template v-if="task.lastBackupResult">
                    <el-tag
                      :type="
                        task.lastBackupResult.status === 'Success'
                          ? 'success'
                          : task.lastBackupResult.status === 'PartialSuccess'
                            ? 'warning'
                            : 'danger'
                      "
                      size="small"
                    >
                      {{
                        task.lastBackupResult.status === 'Success'
                          ? '成功'
                          : task.lastBackupResult.status === 'PartialSuccess'
                            ? `成功（${task.lastBackupResult.failedFileCount} 个文件失败）`
                            : task.lastBackupResult.status === 'Aborted'
                              ? '已中止'
                              : '失败'
                      }}
                    </el-tag>
                    <!-- 失败计数必须显眼（07 §4.1） -->
                    <span
                      v-if="task.lastBackupResult.failedFileCount > 0"
                      class="fbt-warn-count detail__failed"
                    >
                      {{ formatCount(task.lastBackupResult.failedFileCount) }} 个文件失败
                    </span>
                    <div v-if="task.lastBackupResult.message" class="fbt-footnote">
                      {{ task.lastBackupResult.message }}
                    </div>
                  </template>
                  <span v-else class="fbt-muted">尚未备份</span>
                </el-descriptions-item>
                <el-descriptions-item label="上一次全量备份时间">
                  <span class="fbt-mono">
                    {{ task.lastFullBackupTime ? formatDateTime(task.lastFullBackupTime) : '—' }}
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="下次计划备份时间">
                  <span class="fbt-mono">
                    {{ task.nextScheduledTime ? formatDateTime(task.nextScheduledTime) : '—' }}
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="信息">
                  <span v-if="task.message" class="fbt-warn-count">{{ task.message }}</span>
                  <span v-else class="fbt-muted">—</span>
                </el-descriptions-item>
              </el-descriptions>

              <div class="fbt-pane-title" style="margin-top: 14px">整理文件</div>
              <div class="detail__actions">
                <el-button size="small" :icon="Search" @click="openVerify">完整校验</el-button>
                <el-button size="small" :loading="collecting" @click="runCollect">回收多余文件</el-button>
                <el-button size="small" @click="openBin">回收站</el-button>
              </div>
              <div v-if="collectResult" class="fbt-footnote detail__note">
                上次回收：移入 {{ formatCount(collectResult.movedBlobCount) }} 个 blob（{{
                  formatBytes(collectResult.movedBytes)
                }}），回收站当前占用 {{ formatBytes(collectResult.recycleBinBytes) }}
              </div>
            </div>
          </div>

          <!-- 旧界面的「最后的日志」并入概览（07 §5 对等清单） -->
          <div class="fbt-pane fbt-section">
            <div class="fbt-pane-title">
              最后的日志
              <el-button
                size="small"
                text
                type="primary"
                @click="router.push({ name: 'task-logs', params: { id } })"
              >
                查看全部
              </el-button>
            </div>
            <ErrorState v-if="lastLogsError" :error="lastLogsError" compact @retry="loadLastLogs" />
            <StatePlaceholder v-else-if="lastLogs.length === 0" empty description="还没有日志" />
            <el-table v-else :data="lastLogs" size="small" border max-height="240">
              <el-table-column label="类型" width="90">
                <template #default="{ row }">
                  <span :style="{ color: logColor(row.level), fontWeight: 600 }">
                    {{ logLabel(row.level) }}
                  </span>
                </template>
              </el-table-column>
              <el-table-column label="时间" width="160">
                <template #default="{ row }">
                  <span class="fbt-mono">{{ formatDateTime(row.time) }}</span>
                </template>
              </el-table-column>
              <el-table-column label="信息" min-width="300">
                <template #default="{ row }">{{ row.message }}</template>
              </el-table-column>
            </el-table>
          </div>
        </el-tab-pane>

        <!-- ============================ 快照 ============================ -->
        <el-tab-pane label="快照" name="snapshots">
          <SnapshotListPanel
            :task-id="id"
            @browse="(sid: number) => router.push({ name: 'snapshot-browser', params: { id, sid: String(sid) } })"
            @changes="openChanges"
          />

          <div class="fbt-pane fbt-section">
            <div class="fbt-pane-title">
              变更内容
              <span v-if="changesSnapshotId !== null" class="fbt-mono fbt-footnote">
                （快照 #{{ changesSnapshotId }}）
              </span>
            </div>
            <div class="fbt-footnote" style="margin-bottom: 8px">
              选中上方任意快照的「变更内容」查看该快照新增 / 修改 / 删除了哪些文件。
              （镜像里不显示已删除的文件 —— 镜像等于该快照的物化状态，07 §4.2）
            </div>
            <SnapshotChangesPanel :snapshot-id="changesSnapshotId" />
          </div>
        </el-tab-pane>

        <!-- ============================ 日志 ============================ -->
        <el-tab-pane label="日志" name="logs">
          <div class="detail__tabbar">
            <el-button
              size="small"
              text
              type="primary"
              @click="router.push({ name: 'task-logs', params: { id } })"
            >
              在新页面打开（可带筛选参数分享链接）
            </el-button>
          </div>
          <TaskLogsPanel :task-id="id" compact />
        </el-tab-pane>

        <!-- ============================ 监控 ============================ -->
        <el-tab-pane label="监控" name="monitor">
          <ErrorState v-if="monitorError" :error="monitorError" @retry="loadMonitor" />
          <StatePlaceholder v-else-if="monitorLoading && !monitor" text="正在加载监控状态…" />
          <StatePlaceholder
            v-else-if="!rootStatus"
            empty
            description="该任务没有监控根（可能未开启「变更监控」）"
          />

          <template v-else>
            <div class="fbt-pane">
              <div class="fbt-pane-title">
                监控状态
                <el-button size="small" :loading="monitorLoading" :icon="Refresh" @click="loadMonitor">
                  刷新
                </el-button>
              </div>
              <el-descriptions :column="2" size="small" border>
                <el-descriptions-item label="状态">
                  <el-tag :type="monitorStateTagType[rootStatus.state]" size="small">
                    {{ monitorStateLabels[rootStatus.state] }}
                  </el-tag>
                </el-descriptions-item>
                <el-descriptions-item label="待处理变更">
                  <span :class="rootStatus.pendingCount > 0 ? 'fbt-warn-count' : 'fbt-muted'">
                    {{ rootStatus.pendingCount }} 个目录脏了
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="源目录">
                  <PathText :path="rootStatus.sourceDir" :max-length="60" />
                </el-descriptions-item>
                <el-descriptions-item label="合并窗口剩余">
                  {{
                    rootStatus.coalesceSecondsRemaining != null
                      ? `${rootStatus.coalesceSecondsRemaining} 秒`
                      : '—'
                  }}
                </el-descriptions-item>
                <el-descriptions-item label="上次扫描">
                  <span class="fbt-mono">
                    {{ rootStatus.lastScanTime ? formatDateTime(rootStatus.lastScanTime) : '—' }}
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="下次扫描">
                  <span class="fbt-mono">
                    {{ rootStatus.nextScanTime ? formatDateTime(rootStatus.nextScanTime) : '—' }}
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="最近触发">
                  <span class="fbt-mono">
                    {{ rootStatus.lastTriggerTime ? formatDateTime(rootStatus.lastTriggerTime) : '—' }}
                  </span>
                  <span v-if="rootStatus.lastTriggerReason" class="fbt-footnote">
                    （{{ rootStatus.lastTriggerReason }}）
                  </span>
                </el-descriptions-item>
                <el-descriptions-item label="监控异常">
                  <span v-if="rootStatus.error" class="fbt-danger-count">{{ rootStatus.error }}</span>
                  <span v-else class="fbt-muted">—</span>
                </el-descriptions-item>
              </el-descriptions>

              <div class="detail__actions">
                <el-button size="small" :loading="scanning" @click="scan(false)">触发局部扫描</el-button>
                <el-button size="small" :loading="scanning" @click="scan(true)">触发全量扫描</el-button>
              </div>
            </div>

            <div class="fbt-pane fbt-section">
              <div class="fbt-pane-title">待处理变更队列</div>
              <StatePlaceholder
                v-if="rootStatus.pendingDirs.length === 0"
                empty
                description="没有待处理的变更（目录都是干净的）"
              />
              <el-table v-else :data="rootStatus.pendingDirs" size="small" border max-height="300">
                <el-table-column label="目录" min-width="240">
                  <template #default="{ row }">
                    <PathText :path="row.relativePath || '（根目录）'" :max-length="60" />
                  </template>
                </el-table-column>
                <el-table-column label="变更文件数" width="110" align="right">
                  <template #default="{ row }">
                    <span class="fbt-warn-count">{{ formatCount(row.changedFileCount) }}</span>
                  </template>
                </el-table-column>
                <el-table-column label="首次变更" width="160">
                  <template #default="{ row }">
                    <span class="fbt-mono">{{ formatDateTime(row.firstChangeTime) }}</span>
                  </template>
                </el-table-column>
                <el-table-column label="静默期倒计时" width="130" align="right">
                  <template #default="{ row }">
                    <span class="fbt-mono">{{ row.settleSecondsRemaining }} 秒</span>
                  </template>
                </el-table-column>
              </el-table>
            </div>
          </template>
        </el-tab-pane>
      </el-tabs>
    </template>

    <!-- 完整校验 -->
    <el-dialog v-model="verifyVisible" title="完整校验" width="760px">
      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="校验会交叉比对 blob 池与数据库记录，找出多余文件（无引用）与丢失文件（有记录无 blob）、链断裂。"
      />
      <el-checkbox v-model="deepHashCheck" class="detail__deep">
        重算哈希（深校验）—— TB 级数据极慢，只在怀疑文件损坏时勾选
      </el-checkbox>

      <div class="detail__actions">
        <el-button type="primary" :loading="verifying" @click="runVerify">开始校验</el-button>
      </div>

      <ErrorState v-if="verifyError" :error="verifyError" compact class="fbt-section" />
      <template v-else-if="verifyReport">
        <el-descriptions :column="2" size="small" border class="fbt-section">
          <el-descriptions-item label="校验时间">
            <span class="fbt-mono">{{ formatDateTime(verifyReport.checkedAt) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="深校验">{{ verifyReport.deepHashChecked ? '是' : '否' }}</el-descriptions-item>
          <el-descriptions-item label="检查的记录数">
            {{ formatCount(verifyReport.checkedRecordCount) }}
          </el-descriptions-item>
          <el-descriptions-item label="结论">{{ verifyReport.summary }}</el-descriptions-item>
        </el-descriptions>

        <el-descriptions :column="2" size="small" border class="fbt-section">
          <el-descriptions-item label="多余文件（blob）">
            <span :class="verifyReport.orphanBlobs.length > 0 ? 'fbt-warn-count' : 'fbt-muted'">
              {{ formatCount(verifyReport.orphanBlobs.length) }} 个 / {{ formatBytes(verifyReport.orphanBytes) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="丢失文件">
            <span :class="verifyReport.missingBlobs.length > 0 ? 'fbt-danger-count' : 'fbt-muted'">
              {{ formatCount(verifyReport.missingBlobs.length) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="链断裂">
            <span :class="verifyReport.brokenChains.length > 0 ? 'fbt-danger-count' : 'fbt-muted'">
              {{ formatCount(verifyReport.brokenChains.length) }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="哈希不匹配">
            <span :class="verifyReport.corruptBlobs.length > 0 ? 'fbt-danger-count' : 'fbt-muted'">
              {{ formatCount(verifyReport.corruptBlobs.length) }}
            </span>
          </el-descriptions-item>
        </el-descriptions>

        <div v-if="verifyReport.missingBlobs.length > 0" class="fbt-section">
          <div class="fbt-pane-title">丢失文件（前 50 条）</div>
          <el-table :data="verifyReport.missingBlobs.slice(0, 50)" size="small" border max-height="200">
            <el-table-column label="哈希" width="140">
              <template #default="{ row }">
                <span class="fbt-mono">{{ row.hash.slice(0, 12) }}…</span>
              </template>
            </el-table-column>
            <el-table-column label="引用路径" min-width="240">
              <template #default="{ row }">
                <PathText :path="row.referencedPath" :max-length="50" />
              </template>
            </el-table-column>
            <el-table-column label="快照" width="90">
              <template #default="{ row }">
                <span class="fbt-mono">{{ row.snapshotId ? `#${row.snapshotId}` : '—' }}</span>
              </template>
            </el-table-column>
          </el-table>
        </div>

        <div v-if="verifyReport.brokenChains.length > 0" class="fbt-section">
          <div class="fbt-pane-title">链断裂</div>
          <el-table :data="verifyReport.brokenChains" size="small" border max-height="200">
            <el-table-column label="快照" width="90">
              <template #default="{ row }">
                <span class="fbt-mono">#{{ row.snapshotId }}</span>
              </template>
            </el-table-column>
            <el-table-column label="类型" width="110">
              <template #default="{ row }">
                <SnapshotTypeTag :type="row.type" />
              </template>
            </el-table-column>
            <el-table-column label="原因" min-width="200">
              <template #default="{ row }">{{ row.reason }}</template>
            </el-table-column>
            <el-table-column label="期望的前一快照" width="140">
              <template #default="{ row }">
                <span class="fbt-mono">
                  {{ row.expectedPreviousSnapshotId ? `#${row.expectedPreviousSnapshotId}` : '—' }}
                </span>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </template>
    </el-dialog>

    <!-- 回收站 -->
    <el-dialog v-model="binVisible" title="回收站" width="820px">
      <ErrorState v-if="binError" :error="binError" @retry="loadBin" />
      <StatePlaceholder v-else-if="binLoading && !bin" text="正在读取回收站…" />
      <template v-else-if="bin">
        <el-descriptions :column="3" size="small" border>
          <el-descriptions-item label="blob 数量">{{ formatCount(bin.blobCount) }}</el-descriptions-item>
          <el-descriptions-item label="占用空间">{{ formatBytes(bin.totalBytes) }}</el-descriptions-item>
          <el-descriptions-item label="账本文件">
            <PathText :path="bin.ledgerPath" :max-length="30" />
          </el-descriptions-item>
        </el-descriptions>

        <div class="fbt-pane-title" style="margin-top: 12px">
          账本（{{ formatCount(bin.entriesTotal) }} 条）
        </div>
        <StatePlaceholder v-if="bin.entries.length === 0" empty description="回收站是空的" />
        <el-table v-else :data="bin.entries" size="small" border max-height="320">
          <el-table-column label="哈希" width="150">
            <template #default="{ row }">
              <el-tooltip :content="row.hash" placement="top">
                <span class="fbt-mono">{{ row.hash.slice(0, 12) }}…</span>
              </el-tooltip>
            </template>
          </el-table-column>
          <el-table-column label="大小" width="110" align="right">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatBytes(row.length) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="移入时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.movedAt) }}</span>
            </template>
          </el-table-column>
          <!-- 没有「最后路径」用户只能盲删（02 §5.2） -->
          <el-table-column label="移入前的最后路径" min-width="240">
            <template #default="{ row }">
              <PathText v-if="row.lastPath" :path="row.lastPath" :max-length="44" />
              <span v-else class="fbt-muted">（未记录）</span>
            </template>
          </el-table-column>
        </el-table>

        <el-pagination
          v-if="bin.entriesTotal > 100"
          v-model:current-page="binPage"
          :page-size="100"
          :total="bin.entriesTotal"
          layout="prev, pager, next"
          small
          class="fbt-section"
        />
      </template>

      <template #footer>
        <el-button @click="binVisible = false">关闭</el-button>
        <el-button
          type="danger"
          :icon="Delete"
          :loading="emptying"
          :disabled="!bin || bin.blobCount === 0"
          @click="emptyBin"
        >
          清空回收站（不可逆）
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.detail__heading {
  min-width: 0;
}

.detail__status {
  margin-left: 8px;
  vertical-align: middle;
}

.detail__tabs {
  margin-top: 4px;
}

.detail__grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(380px, 1fr));
  gap: 12px;
}

.detail__actions {
  display: flex;
  gap: 8px;
  margin-top: 10px;
  flex-wrap: wrap;
  align-items: center;
}

.detail__failed {
  margin-left: 8px;
}

.detail__note {
  margin-top: 8px;
}

.detail__deep {
  margin-top: 12px;
}

.detail__tabbar {
  display: flex;
  justify-content: flex-end;
  margin-bottom: 8px;
}
</style>
