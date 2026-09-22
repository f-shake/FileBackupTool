<script setup lang="ts">
/**
 * 审计日志 `/settings/audit`（07 §2、06 §4.7）。
 *
 * 与业务日志分开：审计日志记录的是**谁在什么时候做了什么**（登录、任务变更、快照删除、还原执行…）。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { Refresh, Search } from '@element-plus/icons-vue'
import { asApiError, logsApi } from '@/api'
import type { AuditLogEntry, AuditQuery, NormalizedApiError } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import StatePlaceholder from '@/components/StatePlaceholder.vue'
import { auditEventLabels } from '@/locales/zh-CN'
import { formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const PAGE_SIZE = 100

const entries = ref<AuditLogEntry[]>([])
const total = ref(0)
const page = ref(1)
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

const eventType = ref<string>('All')
const range = ref<[string, string] | null>(null)
const keyword = ref('')

const query = computed<AuditQuery>(() => ({
  eventType: eventType.value === 'All' ? undefined : eventType.value,
  from: range.value?.[0],
  to: range.value?.[1],
  keyword: keyword.value.trim() || undefined,
  page: page.value,
  pageSize: PAGE_SIZE,
}))

/** 事件类型下拉：用已知映射，同时兼容后端返回的未知类型 */
const eventOptions = computed(() => {
  const known = Object.entries(auditEventLabels).map(([value, label]) => ({ value, label }))
  const seen = new Set(Object.keys(auditEventLabels))
  for (const e of entries.value) {
    if (!seen.has(e.eventType)) {
      known.push({ value: e.eventType, label: e.eventType })
      seen.add(e.eventType)
    }
  }
  return [{ value: 'All', label: '全部事件' }, ...known]
})

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const result = await logsApi.listAudit(query.value)
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
  eventType.value = 'All'
  range.value = null
  keyword.value = ''
  page.value = 1
  void load()
}

onMounted(load)
watch(page, load)

const detailVisible = ref(false)
const detail = ref<AuditLogEntry | null>(null)

function openDetail(row: AuditLogEntry): void {
  detail.value = row
  detailVisible.value = true
}

function eventLabel(type: string): string {
  return auditEventLabels[type] ?? type
}
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div>
        <h1 class="fbt-page-title">审计日志</h1>
        <div class="fbt-page-desc">登录、任务配置变更、快照删除、还原执行等关键操作的留痕</div>
      </div>
      <div class="fbt-toolbar">
        <el-button size="small" :icon="Refresh" :loading="loading" @click="load">刷新</el-button>
      </div>
    </div>

    <div class="audit__filters">
      <el-select v-model="eventType" size="small" style="width: 190px" @change="search">
        <el-option v-for="o in eventOptions" :key="o.value" :label="o.label" :value="o.value" />
      </el-select>

      <el-date-picker
        v-model="range"
        type="datetimerange"
        size="small"
        range-separator="至"
        start-placeholder="开始时间"
        end-placeholder="结束时间"
        format="YYYY-MM-DD HH:mm:ss"
        value-format="YYYY-MM-DDTHH:mm:ss"
        @change="search"
      />

      <el-input
        v-model="keyword"
        size="small"
        style="width: 220px"
        placeholder="关键词（操作者 / 目标 / 详情）"
        clearable
        @keyup.enter="search"
        @clear="search"
      />

      <el-button size="small" type="primary" :icon="Search" @click="search">查询</el-button>
      <el-button size="small" @click="reset">重置</el-button>
    </div>

    <ErrorState v-if="error" :error="error" @retry="load" />
    <StatePlaceholder v-else-if="loading && entries.length === 0" text="正在加载审计日志…" />
    <StatePlaceholder v-else-if="entries.length === 0" empty description="没有符合条件的审计记录" />

    <template v-else>
      <el-table :data="entries" size="small" border row-key="id" max-height="64vh">
        <el-table-column label="时间" width="160">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatDateTime(row.time) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="事件" width="150">
          <template #default="{ row }">
            <el-tooltip :content="row.eventType" placement="top" :show-after="300">
              <el-tag size="small" effect="plain">{{ eventLabel(row.eventType) }}</el-tag>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="操作者" width="130">
          <template #default="{ row }">
            <span class="fbt-mono">{{ row.actor }}</span>
          </template>
        </el-table-column>
        <el-table-column label="来源 IP" width="140">
          <template #default="{ row }">
            <span class="fbt-mono">{{ row.sourceIp }}</span>
          </template>
        </el-table-column>
        <el-table-column label="目标" min-width="200">
          <template #default="{ row }">
            <span class="fbt-ellipsis" :title="row.target">{{ row.target }}</span>
          </template>
        </el-table-column>
        <el-table-column label="客户端" min-width="180">
          <template #default="{ row }">
            <span class="fbt-ellipsis" :title="row.userAgent">{{ row.userAgent }}</span>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="90">
          <template #default="{ row }">
            <el-button size="small" text type="primary" :disabled="!row.detail" @click="openDetail(row)">
              详情
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="audit__foot">
        <span class="fbt-footnote">共 {{ formatCount(total) }} 条（每页 {{ PAGE_SIZE }} 条）</span>
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

    <el-dialog v-model="detailVisible" title="审计详情" width="680px">
      <el-descriptions v-if="detail" :column="1" size="small" border>
        <el-descriptions-item label="时间">
          <span class="fbt-mono">{{ formatDateTime(detail.time) }}</span>
        </el-descriptions-item>
        <el-descriptions-item label="事件">
          {{ eventLabel(detail.eventType) }}
          <span class="fbt-mono fbt-footnote">（{{ detail.eventType }}）</span>
        </el-descriptions-item>
        <el-descriptions-item label="操作者">
          <span class="fbt-mono">{{ detail.actor }}</span>
        </el-descriptions-item>
        <el-descriptions-item label="来源 IP">
          <span class="fbt-mono">{{ detail.sourceIp }}</span>
        </el-descriptions-item>
        <el-descriptions-item label="客户端">{{ detail.userAgent }}</el-descriptions-item>
        <el-descriptions-item label="目标">{{ detail.target }}</el-descriptions-item>
      </el-descriptions>
      <div v-if="detail?.detail" class="fbt-pane-title" style="margin-top: 12px">Detail</div>
      <pre v-if="detail?.detail" class="fbt-pre">{{ detail.detail }}</pre>
    </el-dialog>
  </div>
</template>

<style scoped>
.audit__filters {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}

.audit__foot {
  display: flex;
  align-items: center;
  margin-top: 8px;
}
</style>
