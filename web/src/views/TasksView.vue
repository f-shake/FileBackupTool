<script setup lang="ts">
/**
 * 任务列表 `/tasks`（07 §4.1）。
 *
 * 卡片式而非表格 —— 需要展示的信息（状态、进度、监控模式、下次计划）比表格列更丰富。
 */
import { computed, onMounted, ref } from 'vue'
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
import { asApiError } from '@/api'
import type { BackupTask } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import JobProgressBar from '@/components/JobProgressBar.vue'
import PathText from '@/components/PathText.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import TaskEditorDialog from '@/components/TaskEditorDialog.vue'
import { monitorStateLabels, monitorStateTagType, taskStatusLabels, taskStatusTagType } from '@/locales/zh-CN'
import { useTasksStore } from '@/stores/tasks'
import { formatDateTime } from '@/utils/datetime'
import { formatCount } from '@/utils/format'

const router = useRouter()
const store = useTasksStore()

const editorVisible = ref(false)
const editing = ref<BackupTask | null>(null)
const busy = ref<Record<string, boolean>>({})

const listError = computed(() => store.error)

onMounted(async () => {
  await store.load()
})

// 本页原先自己拉 /api/settings 只为渲染"自动备份总开关"和实时徽章。
// 两者都是全局状态，已移到顶栏（AppLayout），这里不再需要 settings。

// 重连/轮询信号后的重新拉取由 **App.vue 统一负责**（它 watch `realtime.resyncTick`
// 后调 `tasks.onResync()`，也就是本 store 的 `refreshQuietly()`）。
// 这里曾经也 watch 同一个 `resyncTick` 再拉一次，于是每个心跳发出**两条完全一样**的
// `GET /api/tasks`（列表越大重复开销越大），而停在设置页/审计页时根组件那次也照样在刷任务列表。
// 根组件那处是全局需要的，所以删掉这里这条。

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
        <el-button :icon="Refresh" :loading="store.loading" @click="store.load()">刷新</el-button>
        <el-button type="primary" :icon="Plus" @click="openCreate">新建任务</el-button>
      </div>
    </div>

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

</style>
