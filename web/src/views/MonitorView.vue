<script setup lang="ts">
/**
 * 全局实时监控 `/monitor`（07 §4.6）。
 *
 * 旧界面**完全没有这个页面** —— 而它恰好是「备份到底有没有在工作」最需要可见的地方。
 *
 * 数据源只有 `GET /api/monitoring` 一个端点；刷新靠：
 *   - SignalR `monitor.changed` 事件
 *   - resync 信号（重连后 / 降级为每 5 秒轮询时）—— 降级后本页自动变成 REST 轮询
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { QuestionFilled, Refresh } from '@element-plus/icons-vue'
import { asApiError, monitoringApi } from '@/api'
import type {
  JobQueueSnapshot,
  JobState,
  MonitoringSnapshot,
  MonitorRootState,
  NormalizedApiError,
  QueuedJob,
  RootMonitorStatus,
} from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import JobProgressBar from '@/components/JobProgressBar.vue'
import PathText from '@/components/PathText.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import {
  jobPhaseLabels,
  jobStateLabels,
  jobTypeLabels,
  monitorStateLabels,
  monitorStateTagType,
} from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const router = useRouter()
const realtime = useRealtimeStore()

const snapshot = ref<MonitoringSnapshot | null>(null)
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

async function load(quiet = false): Promise<void> {
  if (!quiet) loading.value = true
  try {
    snapshot.value = await monitoringApi.getMonitoring()
    error.value = null
  } catch (err) {
    // 静默刷新失败不覆盖已显示的数据，但要暴露错误，避免「看起来一切正常」
    error.value = asApiError(err)
  } finally {
    loading.value = false
  }
}

onMounted(() => void load())

// resync：重连后拉一次全量；降级为轮询时每次 tick 都会触发 → 每 5 秒刷新
watch(
  () => realtime.resyncTick,
  () => {
    void load(true)
  },
)

realtime.on('monitor.changed', () => {
  void load(true)
})

/* ------------------------------- 派生数据 ------------------------------- */

const roots = computed<RootMonitorStatus[]>(() => snapshot.value?.roots ?? [])
const jobs = computed<JobQueueSnapshot | null>(() => snapshot.value?.jobs ?? null)
const triggers = computed(() => snapshot.value?.recentTriggers ?? [])

/** 待处理变更队列：把各根的脏目录摊平，便于一眼看全局 */
const pendingDirs = computed(() =>
  roots.value.flatMap((r) =>
    r.pendingDirs.map((d) => ({
      taskId: r.taskId,
      taskName: r.taskName,
      state: r.state,
      ...d,
    })),
  ),
)

const abnormalRoots = computed(() => roots.value.filter((r) => r.state === 'error'))

/** 队列全局串行：只有一个作业在跑（01 §4.1） */
const queueSummary = computed(() => {
  const running = jobs.value?.running ?? null
  const queued = jobs.value?.queued ?? []
  return { running, queued }
})

/* 表格插槽里的 row 是 any，用带类型的辅助函数取文案，避免隐式 any 索引报错 */

function monitorStateLabel(state: MonitorRootState): string {
  return monitorStateLabels[state]
}

function monitorStateTag(state: MonitorRootState): 'success' | 'warning' | 'danger' {
  return monitorStateTagType[state]
}

function jobStateLabel(state: JobState): string {
  return jobStateLabels[state]
}
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div>
        <h1 class="fbt-page-title">全局实时监控</h1>
        <div class="fbt-page-desc">
          所有任务的监控状态、待处理变更队列、全局作业队列与最近触发记录
          <span class="fbt-muted">· 全局 IO 队列串行执行，同一时刻只有一个作业在跑</span>
        </div>
      </div>

      <div class="fbt-toolbar">
        <el-button size="small" :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
      </div>
    </div>

    <ErrorState v-if="error && !snapshot" :error="error" @retry="load()" />
    <StatePlaceholder v-else-if="loading && !snapshot" text="正在读取监控状态…" />

    <template v-else-if="snapshot">
      <el-alert
        v-if="error"
        type="warning"
        :closable="false"
        show-icon
        class="fbt-section"
        title="最近一次刷新失败，下面的数据可能已过期"
        :description="error.message"
      />

      <!-- 监控异常必须醒目（07 §7「成功但不完整必须醒目」） -->
      <el-alert
        v-if="abnormalRoots.length > 0"
        type="error"
        :closable="false"
        show-icon
        class="fbt-section"
        :title="`${abnormalRoots.length} 个源的监控异常`"
        :description="abnormalRoots.map((r) => `${r.taskName}：${r.error ?? '未知原因'}`).join('；')"
      />

      <!-- 运行中作业 -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">正在运行</div>
        <StatePlaceholder v-if="!queueSummary.running" empty description="当前没有正在运行的作业" />
        <template v-else>
          <div class="monitor__job">
            <el-tag type="primary" size="small">{{ jobTypeLabels[queueSummary.running.type] ?? queueSummary.running.type }}</el-tag>
            <span>{{ queueSummary.running.taskName ?? queueSummary.running.taskId ?? '（全局作业）' }}</span>
            <el-tag :type="queueSummary.running.state === 'Running' ? 'primary' : 'info'" size="small">
              {{ jobStateLabels[queueSummary.running.state] }}
            </el-tag>
            <span class="fbt-footnote">
              开始于
              <span class="fbt-mono">
                {{ queueSummary.running.startedAt ? formatDateTime(queueSummary.running.startedAt) : '—' }}
              </span>
            </span>
            <div class="fbt-spacer" />
            <el-button
              v-if="queueSummary.running.taskId"
              size="small"
              text
              type="primary"
              @click="router.push({ name: 'task-detail', params: { id: queueSummary.running.taskId } })"
            >
              查看任务
            </el-button>
          </div>
          <JobProgressBar :progress="queueSummary.running.progress ?? null" />
          <div v-if="queueSummary.running.progress" class="fbt-footnote">
            阶段：{{ jobPhaseLabels[queueSummary.running.progress.phase] }}
            <template v-if="queueSummary.running.progress.currentPath">
              · <PathText :path="queueSummary.running.progress.currentPath" :max-length="60" />
            </template>
          </div>
          <div v-if="queueSummary.running.message" class="fbt-footnote">
            {{ queueSummary.running.message }}
          </div>
        </template>
      </div>

      <!-- 全局作业队列 -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">
          排队中（{{ queueSummary.queued.length }}）
        </div>
        <StatePlaceholder v-if="queueSummary.queued.length === 0" empty description="队列为空" />
        <el-table v-else :data="queueSummary.queued" size="small" border max-height="260">
          <el-table-column label="位置" width="70" align="center">
            <template #default="{ row }">
              <el-tag size="small" type="info">{{ row.position ?? '—' }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column label="类型" width="110">
            <template #default="{ row }">{{ jobTypeLabels[row.type] ?? row.type }}</template>
          </el-table-column>
          <el-table-column label="任务" min-width="200">
            <template #default="{ row }">{{ row.taskName ?? row.taskId ?? '（全局作业）' }}</template>
          </el-table-column>
          <el-table-column label="状态" width="100">
            <template #default="{ row }">
              <el-tag size="small" :type="row.state === 'Running' ? 'primary' : 'info'">
                {{ jobStateLabel(row.state) }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="入队时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.enqueuedAt) }}</span>
            </template>
          </el-table-column>
        </el-table>
      </div>

      <!-- 各源监控状态 -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">各任务监控状态（{{ roots.length }}）</div>
        <StatePlaceholder v-if="roots.length === 0" empty description="没有开启变更监控的任务" />
        <el-table v-else :data="roots" size="small" border max-height="340">
          <el-table-column label="任务" min-width="160">
            <template #default="{ row }">
              <el-link :underline="false" @click="router.push({ name: 'task-detail', params: { id: row.taskId } })">
                {{ row.taskName }}
              </el-link>
            </template>
          </el-table-column>
          <el-table-column label="源目录" min-width="220">
            <template #default="{ row }">
              <PathText :path="row.sourceDir" :max-length="42" />
            </template>
          </el-table-column>
          <el-table-column label="监控状态" width="120">
            <template #default="{ row }">
              <el-tag :type="monitorStateTag(row.state)" size="small">
                {{ monitorStateLabel(row.state) }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="待处理变更" width="120" align="right">
            <template #default="{ row }">
              <span :class="row.pendingCount > 0 ? 'fbt-warn-count' : 'fbt-muted'">
                {{ row.pendingCount }} 个目录
              </span>
            </template>
          </el-table-column>
          <el-table-column label="合并窗口剩余" width="130" align="right">
            <template #default="{ row }">
              <span v-if="row.coalesceSecondsRemaining != null" class="fbt-mono">
                {{ row.coalesceSecondsRemaining }} 秒
              </span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
          <el-table-column label="上次扫描" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ row.lastScanTime ? formatDateTime(row.lastScanTime) : '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column label="下次扫描" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ row.nextScanTime ? formatDateTime(row.nextScanTime) : '—' }}</span>
            </template>
          </el-table-column>
          <el-table-column label="最近触发原因" min-width="160">
            <template #default="{ row }">
              <span v-if="row.lastTriggerReason">{{ row.lastTriggerReason }}</span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
          <el-table-column label="异常" min-width="160">
            <template #default="{ row }">
              <span v-if="row.error" class="fbt-danger-count">{{ row.error }}</span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
        </el-table>
      </div>

      <!-- 待处理变更队列 -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">
          待处理变更队列（{{ pendingDirs.length }} 个目录）
          <el-tooltip placement="top" :show-after="200">
            <template #content>
              目录变脏后要等静默期结束才触发备份，避免边写边备。
            </template>
            <el-icon><QuestionFilled /></el-icon>
          </el-tooltip>
        </div>
        <StatePlaceholder v-if="pendingDirs.length === 0" empty description="所有目录都是干净的" />
        <el-table v-else :data="pendingDirs" size="small" border max-height="300">
          <el-table-column label="任务" min-width="150">
            <template #default="{ row }">{{ row.taskName }}</template>
          </el-table-column>
          <el-table-column label="目录" min-width="240">
            <template #default="{ row }">
              <PathText :path="row.relativePath || '（根目录）'" :max-length="50" />
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

      <!-- 最近触发记录 -->
      <div class="fbt-pane fbt-section">
        <div class="fbt-pane-title">最近触发记录（{{ triggers.length }}）</div>
        <StatePlaceholder v-if="triggers.length === 0" empty description="还没有触发记录" />
        <el-table v-else :data="triggers" size="small" border max-height="300">
          <el-table-column label="时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.time) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="任务" min-width="160">
            <template #default="{ row }">{{ row.taskName }}</template>
          </el-table-column>
          <el-table-column label="原因" min-width="180">
            <template #default="{ row }">{{ row.reason }}</template>
          </el-table-column>
          <el-table-column label="是否触发" width="100">
            <template #default="{ row }">
              <el-tag :type="row.triggered ? 'success' : 'info'" size="small">
                {{ row.triggered ? '已触发' : '未触发' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="未触发原因" min-width="180">
            <template #default="{ row }">
              <span v-if="row.skippedReason" class="fbt-warn-count">{{ row.skippedReason }}</span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
          <el-table-column label="快照" width="90">
            <template #default="{ row }">
              <span v-if="row.snapshotId" class="fbt-mono">#{{ row.snapshotId }}</span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
        </el-table>
      </div>

      <div class="fbt-footnote">
        服务端时间：<span class="fbt-mono">{{ formatDateTime(snapshot.serverTime) }}</span>
        · 本页数据来自 <span class="fbt-mono">GET /api/monitoring</span>
      </div>
    </template>
  </div>
</template>

<style scoped>
.monitor__job {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  margin-bottom: 8px;
}
</style>
