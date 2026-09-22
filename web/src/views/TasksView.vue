<script setup lang="ts">
/**
 * 任务列表 `/tasks`（07 §4.1）。
 *
 * 卡片式而非表格 —— 需要展示的信息（状态、进度、监控模式、下次计划）比表格列更丰富。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  ArrowDown,
  Delete,
  Edit,
  Plus,
  Refresh,
  VideoPause,
  VideoPlay,
  View,
} from '@element-plus/icons-vue'
import { asApiError, settingsApi } from '@/api'
import type { AppSettings, BackupTask, NormalizedApiError } from '@/api/types'
import ChannelBadge from '@/components/ChannelBadge.vue'
import ErrorState from '@/components/ErrorState.vue'
import JobProgressBar from '@/components/JobProgressBar.vue'
import PathText from '@/components/PathText.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import TaskEditorDialog from '@/components/TaskEditorDialog.vue'
import { monitorStateLabels, monitorStateTagType, taskStatusLabels, taskStatusTagType } from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { useTasksStore } from '@/stores/tasks'
import { formatDateTime } from '@/utils/datetime'
import { formatCount } from '@/utils/format'

const router = useRouter()
const store = useTasksStore()
const realtime = useRealtimeStore()

const editorVisible = ref(false)
const editing = ref<BackupTask | null>(null)
const busy = ref<Record<string, boolean>>({})

const settings = ref<AppSettings | null>(null)
const settingsError = ref<NormalizedApiError | null>(null)
const togglingAuto = ref(false)

const listError = computed(() => store.error)

onMounted(async () => {
  await store.load()
  await loadSettings()
})

async function loadSettings(): Promise<void> {
  try {
    settings.value = await settingsApi.getSettings()
    settingsError.value = null
  } catch (err) {
    settingsError.value = asApiError(err)
  }
}

// 收到重连/轮询信号后重新拉取（07 §6：断线期间的事件已丢失，必须整体重拉）
watch(
  () => realtime.resyncTick,
  () => {
    if (!listError.value) void store.refreshQuietly()
  },
)

function openCreate(): void {
  editing.value = null
  editorVisible.value = true
}

function openEdit(task: BackupTask): void {
  editing.value = task
  editorVisible.value = true
}

async function toggleEnabled(task: BackupTask): Promise<void> {
  busy.value = { ...busy.value, [task.id]: true }
  try {
    await store.setEnabled(task.id, !task.isEnabled)
    ElMessage.success(task.isEnabled ? '已禁用' : '已启用')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
  } finally {
    busy.value = { ...busy.value, [task.id]: false }
  }
}

async function runBackup(task: BackupTask, type: 'Full' | 'VirtualFull' | 'Increment'): Promise<void> {
  busy.value = { ...busy.value, [task.id]: true }
  try {
    await store.startBackup(task.id, type)
    ElMessage.success(store.notices[task.id] ?? '已加入队列')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
  } finally {
    busy.value = { ...busy.value, [task.id]: false }
  }
}

async function cancel(task: BackupTask): Promise<void> {
  try {
    await ElMessageBox.confirm(`确定要取消「${task.name}」正在执行的备份吗？`, '取消备份', {
      confirmButtonText: '取消备份',
      cancelButtonText: '继续执行',
      type: 'warning',
    })
  } catch {
    return
  }
  try {
    await store.cancelBackup(task.id)
    ElMessage.success('已发送取消请求，作业会尽快停止')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
  }
}

async function removeTask(task: BackupTask): Promise<void> {
  try {
    await ElMessageBox.confirm(
      `确定要删除任务「${task.name}」吗？\n\n该任务将被软删除；它的快照会保留，blob 交由 GC 处理（可能因此被移入回收站）。备份目录不会被物理删除。`,
      '删除任务',
      { confirmButtonText: '删除', cancelButtonText: '取消', type: 'warning' },
    )
  } catch {
    return
  }
  try {
    await store.remove(task.id)
    ElMessage.success('已删除')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
  }
}

async function toggleAutoBackup(value: string | number | boolean): Promise<void> {
  if (!settings.value) return
  const next = Boolean(value)
  togglingAuto.value = true
  try {
    settings.value = await settingsApi.updateSettings({ ...settings.value, autoBackupEnabled: next })
    ElMessage.success(next ? '自动备份已开启' : '自动备份已关闭（手动备份仍可用）')
  } catch (err) {
    ElMessage.error(asApiError(err).message)
    await loadSettings()
  } finally {
    togglingAuto.value = false
  }
}

function resultText(task: BackupTask): string {
  const r = task.lastBackupResult
  if (!r) return '尚无备份记录'
  if (r.status === 'Aborted') return '已中止'
  if (r.failedFileCount > 0) return `成功（${r.failedFileCount} 个文件失败）`
  return '成功'
}

function resultType(task: BackupTask): 'success' | 'warning' | 'danger' | 'info' {
  const r = task.lastBackupResult
  if (!r) return 'info'
  if (r.status === 'Aborted') return 'danger'
  if (r.status === 'Failed') return 'danger'
  if (r.failedFileCount > 0) return 'warning'
  return 'success'
}

function openDetail(task: BackupTask): void {
  void router.push({ name: 'task-detail', params: { id: task.id } })
}
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div>
        <h1 class="fbt-page-title">任务</h1>
        <p class="fbt-page-desc">
          共 {{ formatCount(store.tasks.length) }} 个任务。备份在服务端全局串行执行，任意时刻最多只有一个作业在跑。
        </p>
      </div>
      <div class="fbt-toolbar">
        <ChannelBadge />
        <el-divider direction="vertical" />
        <el-tooltip content="关闭后，定时与监控触发的备份都会被暂停；手动「立即备份」仍可用" placement="bottom">
          <div class="tasks__auto">
            <span>自动备份总开关</span>
            <el-switch
              :model-value="settings?.autoBackupEnabled ?? false"
              :loading="togglingAuto"
              :disabled="!settings"
              inline-prompt
              active-text="开"
              inactive-text="关"
              @update:model-value="toggleAutoBackup"
            />
          </div>
        </el-tooltip>
        <el-button :icon="Refresh" :loading="store.loading" @click="store.load()">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openCreate">新建任务</el-button>
      </div>
    </div>

    <el-alert
      v-if="settingsError"
      type="warning"
      :closable="false"
      show-icon
      class="fbt-section"
      :title="`无法读取全局设置：${settingsError.message}`"
      description="自动备份总开关与还原根白名单都来自 /api/settings，此接口不可用时这些功能不可操作（不代表设置已关闭）。"
    />

    <ErrorState v-if="listError" :error="listError" @retry="store.load()" />

    <StatePlaceholder
      v-else-if="store.loading && store.tasks.length === 0"
      text="正在加载任务列表…"
    />

    <StatePlaceholder
      v-else-if="store.tasks.length === 0"
      empty
      description="还没有任务。点击右上角「新建任务」开始配置第一个备份。"
    >
      <el-button type="primary" :icon="Plus" @click="openCreate">新建任务</el-button>
    </StatePlaceholder>

    <div v-else class="tasks__grid">
      <el-card v-for="task in store.tasks" :key="task.id" shadow="hover" class="task-card">
        <template #header>
          <div class="task-card__head">
            <div class="task-card__name" @click="openDetail(task)">
              <span class="fbt-ellipsis" :title="task.name">{{ task.name }}</span>
            </div>
            <el-tag
              :type="taskStatusTagType[task.status ?? 'Ready']"
              size="small"
              effect="dark"
            >
              {{ taskStatusLabels[task.status ?? 'Ready'] }}
            </el-tag>
            <div class="fbt-spacer" />
            <el-tooltip :content="task.isEnabled ? '点击禁用' : '点击启用'" placement="top">
              <el-switch
                :model-value="task.isEnabled"
                :loading="busy[task.id]"
                @update:model-value="toggleEnabled(task)"
              />
            </el-tooltip>
          </div>
        </template>

        <div class="task-card__body">
          <div class="task-card__paths">
            <div class="task-card__path">
              <span class="task-card__label">源</span>
              <PathText :path="task.sourceDir" :max-length="42" />
            </div>
            <div class="task-card__path">
              <span class="task-card__label">目标</span>
              <PathText :path="task.backupDir" :max-length="42" />
            </div>
          </div>

          <div class="task-card__meta">
            <div class="task-card__row">
              <span class="fbt-muted">上次备份</span>
              <span class="fbt-mono">{{ formatDateTime(task.lastBackupTime) }}</span>
              <el-tag :type="resultType(task)" size="small" effect="plain">
                {{ resultText(task) }}
              </el-tag>
            </div>

            <div v-if="task.message" class="task-card__row">
              <span class="fbt-muted">信息</span>
              <span class="fbt-ellipsis" :title="task.message">{{ task.message }}</span>
            </div>

            <div class="task-card__row">
              <span class="fbt-muted">上次全量</span>
              <span class="fbt-mono">{{ formatDateTime(task.lastFullBackupTime) }}</span>
            </div>

            <div class="task-card__row">
              <span class="fbt-muted">快照</span>
              <span>
                {{ formatCount(task.snapshotCount ?? 0) }} 个
                <span class="fbt-muted">（有效 {{ formatCount(task.validSnapshotCount ?? 0) }}）</span>
              </span>
            </div>

            <div class="task-card__row">
              <span class="fbt-muted">下次计划</span>
              <span class="fbt-mono">{{ formatDateTime(task.nextScheduledTime) }}</span>
            </div>

            <div class="task-card__row">
              <span class="fbt-muted">监控</span>
              <el-tag
                v-if="task.monitorMode"
                :type="monitorStateTagType[task.monitorMode]"
                size="small"
                effect="plain"
              >
                {{ monitorStateLabels[task.monitorMode] }}
              </el-tag>
              <span v-else class="fbt-muted">未启用</span>
            </div>
          </div>

          <!-- 正在运行时：进度条（走 SignalR） -->
          <JobProgressBar
            v-if="store.liveProgress[task.id]"
            :progress="store.liveProgress[task.id]!.progress"
            compact
            class="task-card__progress"
          />
          <div v-else-if="store.notices[task.id]" class="fbt-footnote">
            {{ store.notices[task.id] }}
          </div>
        </div>

        <template #footer>
          <div class="task-card__actions">
            <el-dropdown
              trigger="click"
              :disabled="busy[task.id]"
              @command="(c: 'Full' | 'VirtualFull' | 'Increment') => runBackup(task, c)"
            >
              <el-button type="primary" size="small" :icon="VideoPlay" :loading="busy[task.id]">
                立即备份<el-icon class="task-card__caret"><ArrowDown /></el-icon>
              </el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="Full">全量备份</el-dropdown-item>
                  <el-dropdown-item command="VirtualFull">虚拟全量备份</el-dropdown-item>
                  <el-dropdown-item command="Increment">增量备份</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>

            <el-button
              size="small"
              :icon="VideoPause"
              :disabled="!store.isRunning(task.id)"
              @click="cancel(task)"
            >
              取消
            </el-button>
            <el-button size="small" :icon="Edit" @click="openEdit(task)">编辑</el-button>
            <el-button size="small" :icon="Delete" type="danger" plain @click="removeTask(task)">
              删除
            </el-button>
            <div class="fbt-spacer" />
            <el-button size="small" text type="primary" :icon="View" @click="openDetail(task)">
              详情
            </el-button>
          </div>
        </template>
      </el-card>
    </div>

    <TaskEditorDialog v-model="editorVisible" :task="editing" @saved="store.load()" />
  </div>
</template>

<style scoped>
.tasks__grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(430px, 1fr));
  gap: 14px;
}

.task-card__head {
  display: flex;
  align-items: center;
  gap: 8px;
}

.task-card__name {
  font-size: 15px;
  font-weight: 600;
  cursor: pointer;
  max-width: 46%;
  min-width: 0;
}

.task-card__body {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.task-card__paths {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 8px 10px;
  border-radius: 4px;
  background-color: var(--el-fill-color-light);
}

.task-card__path {
  display: flex;
  align-items: baseline;
  gap: 8px;
  min-width: 0;
}

.task-card__label {
  flex: 0 0 auto;
  font-size: 12px;
  color: var(--el-text-color-secondary);
  min-width: 28px;
}

.task-card__meta {
  display: flex;
  flex-direction: column;
  gap: 5px;
  font-size: 13px;
}

.task-card__row {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.task-card__row > .fbt-muted:first-child {
  min-width: 62px;
  flex: 0 0 auto;
  font-size: 12px;
}

.task-card__progress {
  margin-top: 4px;
}

.task-card__actions {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.task-card__caret {
  margin-left: 4px;
  font-size: 12px;
}

.tasks__auto {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 13px;
  padding: 0 4px;
}
</style>
