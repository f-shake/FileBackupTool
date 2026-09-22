<script setup lang="ts">
/**
 * 任务日志面板（07 §4.4）。
 *
 * 独立路由 `/tasks/:id/logs` 与任务详情的「日志」Tab 共用本组件。
 *
 * 三处刻意偏离旧实现：
 *   1. 时间范围用 **日期时间范围选择器**（补齐项 2），旧界面是两个裸文本框绑 `DateTime`。
 *   2. 分页**固定每页 100**，旧实现是 DEBUG 下 10、Release 下 100 —— 构建相关不可接受。
 *   3. 订阅 `log.appended` **实时追加**，并提供「跟随」开关；旧界面完全没有自动刷新。
 *      另加**级别着色**（旧界面无着色）。
 */
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { Search } from '@element-plus/icons-vue'
import { asApiError, logsApi } from '@/api'
import type { BackupLogDetail, BackupLogEntry, LogLevel, LogQuery, NormalizedApiError } from '@/api/types'
import ErrorState from './ErrorState.vue'
import StatePlaceholder from './StatePlaceholder.vue'
import { useRealtimeStore } from '@/stores/realtime'
import { logLevelColor, logLevelLabels, logLevelOptions } from '@/locales/zh-CN'
import { formatDateTime } from '@/utils/datetime'

const props = withDefaults(
  defineProps<{
    taskId?: string | null
    /** 嵌入在 Tab 里时高度更矮 */
    compact?: boolean
    /** 初始筛选（独立路由可从 query 带入，便于分享链接） */
    initialLevel?: LogLevel | 'All'
    initialKeyword?: string
    initialRange?: [string, string] | null
  }>(),
  {
    taskId: null,
    compact: false,
    initialLevel: 'All',
    initialKeyword: '',
    initialRange: null,
  },
)

const realtime = useRealtimeStore()

/** 固定 100 —— 与构建配置无关 */
const PAGE_SIZE = 100

const entries = ref<BackupLogEntry[]>([])
const total = ref(0)
const page = ref(1)
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

const level = ref<LogLevel | 'All'>(props.initialLevel)
const range = ref<[string, string] | null>(props.initialRange)
const keyword = ref(props.initialKeyword)

/** 「跟随」：新日志自动插入并滚到底部 */
const following = ref(true)
const liveCount = ref(0)
const scrollbar = ref<{ setScrollTop: (v: number) => void } | null>(null)

const query = computed<LogQuery>(() => ({
  taskId: props.taskId ?? undefined,
  level: level.value,
  from: range.value?.[0],
  to: range.value?.[1],
  keyword: keyword.value.trim() || undefined,
  page: page.value,
  pageSize: PAGE_SIZE,
}))

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const result = await logsApi.listLogs(query.value)
    entries.value = result.items
    total.value = result.total
  } catch (err) {
    error.value = asApiError(err)
    entries.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

function reset(): void {
  level.value = 'All'
  range.value = null
  keyword.value = ''
  page.value = 1
  void load()
}

onMounted(load)
watch(page, load)
watch(() => props.taskId, search)

/* ------------------------------- 实时追加 ------------------------------- */

realtime.on('log.appended', (payload) => {
  if (props.taskId && payload.taskId !== props.taskId) return
  // 当前页不是第 1 页、或有筛选条件时，新日志不一定属于当前结果集 —— 只计数不插入
  const onFirstPage = page.value === 1
  const filtered =
    level.value !== 'All' ||
    !!keyword.value.trim() ||
    !!range.value
  const matchesLevel = level.value === 'All' || payload.level === level.value

  if (onFirstPage && !filtered) {
    entries.value.push({
      id: -Date.now(), // 临时负数 id，刷新后由服务端真 id 取代
      taskId: payload.taskId,
      snapshotId: payload.snapshotId,
      level: payload.level,
      time: payload.time,
      message: payload.message,
      hasDetail: false,
    })
    if (entries.value.length > PAGE_SIZE) entries.value.splice(0, entries.value.length - PAGE_SIZE)
    total.value += 1
    if (following.value) {
      void nextTick(() => scrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
    }
  } else if (matchesLevel) {
    liveCount.value += 1
  }
})

watch(following, (on) => {
  if (on) {
    liveCount.value = 0
    void nextTick(() => scrollbar.value?.setScrollTop(Number.MAX_SAFE_INTEGER))
  }
})

// 重连/降级为轮询后重新拉一次（不能靠增量事件补齐）
watch(
  () => realtime.resyncTick,
  () => {
    liveCount.value = 0
    void load()
  },
)

/* ------------------------------- 详情弹窗 ------------------------------- */

const detailVisible = ref(false)
const detailLoading = ref(false)
const detailError = ref<NormalizedApiError | null>(null)
const detail = ref<BackupLogDetail | null>(null)

async function openDetail(row: BackupLogEntry): Promise<void> {
  if (!row.hasDetail) return
  detailVisible.value = true
  detailLoading.value = true
  detailError.value = null
  detail.value = null
  try {
    detail.value = await logsApi.getLogDetail(row.id)
  } catch (err) {
    detailError.value = asApiError(err)
  } finally {
    detailLoading.value = false
  }
}

function levelColor(l: LogLevel): string {
  return logLevelColor[l] ?? 'inherit'
}
</script>

<template>
  <div class="logs">
    <div class="logs__filters">
      <el-select v-model="level" size="small" style="width: 120px" @change="search">
        <el-option
          v-for="opt in logLevelOptions"
          :key="opt.value"
          :label="opt.label"
          :value="opt.value"
        />
      </el-select>

      <!-- 补齐项 2：日期时间范围选择器（带格式提示），不再是两个裸文本框 -->
      <el-date-picker
        v-model="range"
        type="datetimerange"
        size="small"
        range-separator="至"
        start-placeholder="开始时间"
        end-placeholder="结束时间"
        format="YYYY-MM-DD HH:mm:ss"
        value-format="YYYY-MM-DDTHH:mm:ss"
        :default-time="[new Date(2000, 0, 1, 0, 0, 0), new Date(2000, 0, 1, 23, 59, 59)]"
        @change="search"
      />

      <el-input
        v-model="keyword"
        size="small"
        style="width: 220px"
        placeholder="关键词（匹配信息）"
        clearable
        @keyup.enter="search"
        @clear="search"
      />

      <el-button size="small" type="primary" :icon="Search" @click="search">查询</el-button>
      <el-button size="small" @click="reset">重置</el-button>

      <div class="fbt-spacer" />

      <el-checkbox v-model="following">
        跟随最新
        <el-badge
          v-if="liveCount > 0 && !following"
          :value="liveCount"
          class="logs__badge"
        />
      </el-checkbox>
      <el-button size="small" :loading="loading" @click="load">刷新</el-button>
    </div>

    <ErrorState v-if="error" :error="error" @retry="load" />
    <StatePlaceholder v-else-if="loading && entries.length === 0" text="正在加载日志…" />
    <StatePlaceholder v-else-if="entries.length === 0" empty description="没有符合条件的日志" />

    <template v-else>
      <el-scrollbar ref="scrollbar" :height="compact ? '46vh' : '60vh'">
        <el-table
          :data="entries"
          size="small"
          border
          :show-header="true"
          row-key="id"
          :max-height="compact ? '46vh' : '60vh'"
        >
          <el-table-column label="类型" width="90">
            <template #default="{ row }">
              <!-- 级别着色（旧界面无着色） -->
              <span :style="{ color: levelColor(row.level), fontWeight: 600 }">
                {{ logLevelLabels[row.level as LogLevel] }}
              </span>
            </template>
          </el-table-column>
          <el-table-column label="时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.time) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="信息" min-width="360">
            <template #default="{ row }">
              <span :style="{ color: levelColor(row.level as LogLevel) }">{{ row.message }}</span>
            </template>
          </el-table-column>
          <el-table-column label="快照" width="90">
            <template #default="{ row }">
              <span v-if="row.snapshotId" class="fbt-mono">#{{ row.snapshotId }}</span>
              <span v-else class="fbt-muted">—</span>
            </template>
          </el-table-column>
          <el-table-column label="操作" width="90">
            <template #default="{ row }">
              <el-button
                size="small"
                text
                type="primary"
                :disabled="!row.hasDetail"
                @click="openDetail(row)"
              >
                详情
              </el-button>
            </template>
          </el-table-column>
        </el-table>
      </el-scrollbar>

      <div class="logs__foot">
        <span class="fbt-footnote">
          共 {{ total }} 条，第 {{ page }} / {{ Math.max(1, Math.ceil(total / PAGE_SIZE)) }} 页（每页固定 {{ PAGE_SIZE }} 条）
        </span>
        <div class="fbt-spacer" />
        <el-pagination
          v-model:current-page="page"
          :page-size="PAGE_SIZE"
          :total="total"
          layout="prev, pager, next"
          small
        />
      </div>
    </template>

    <el-dialog v-model="detailVisible" title="日志详情" width="720px">
      <ErrorState v-if="detailError" :error="detailError" compact />
      <StatePlaceholder v-else-if="detailLoading" text="正在加载…" />
      <template v-else-if="detail">
        <el-descriptions :column="1" size="small" border>
          <el-descriptions-item label="类型">
            <span :style="{ color: levelColor(detail.level), fontWeight: 600 }">
              {{ logLevelLabels[detail.level] }}
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="时间">
            <span class="fbt-mono">{{ formatDateTime(detail.time) }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="任务">
            <span class="fbt-mono">{{ detail.taskId }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="快照">
            <span v-if="detail.snapshotId" class="fbt-mono">#{{ detail.snapshotId }}</span>
            <span v-else class="fbt-muted">—</span>
          </el-descriptions-item>
          <el-descriptions-item label="信息">{{ detail.message }}</el-descriptions-item>
        </el-descriptions>
        <div class="fbt-pane-title" style="margin-top: 12px">Detail</div>
        <pre v-if="detail.detail" class="fbt-pre">{{ detail.detail }}</pre>
        <el-empty v-else description="本条日志没有 Detail" />
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.logs__filters {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}

.logs__foot {
  display: flex;
  align-items: center;
  margin-top: 8px;
}

.logs__badge {
  margin-left: 6px;
}
</style>
