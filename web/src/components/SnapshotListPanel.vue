<script setup lang="ts">
/**
 * 快照列表（07 §4.1 / §5 对等清单），任务详情「快照」Tab 使用。
 *
 * 相比旧界面补齐（07 §3 补齐项 1）：
 *   - **占用空间列**（旧界面只有文件计数）
 *   - **类型 / 日期筛选 + 分页**（旧界面无筛选、无分页）
 *   - 补 `失败` 计数（07 §7「成功但不完整必须醒目」）
 *
 * 删除走「先看影响清单再确认」（07 §7 破坏性操作二次确认）。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { Delete, FolderOpened, Refresh, VideoPlay } from '@element-plus/icons-vue'
import { asApiError, snapshotsApi, tasksApi } from '@/api'
import type {
  DeleteImpact,
  NormalizedApiError,
  Snapshot,
  SnapshotGroup,
  SnapshotStatus,
  SnapshotType,
} from '@/api/types'
import ErrorState from './ErrorState.vue'
import SnapshotTypeTag from './SnapshotTypeTag.vue'
import StatePlaceholder from './StatePlaceholder.vue'
import { snapshotStatusLabels, snapshotStatusTagType } from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const props = defineProps<{ taskId: string }>()
const emit = defineEmits<{
  browse: [snapshotId: number]
  changes: [snapshotId: number]
}>()

const realtime = useRealtimeStore()

const groups = ref<SnapshotGroup[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)

const typeFilter = ref<SnapshotType | 'All'>('All')
const range = ref<[string, string] | null>(null)
/** 选中的全量分组（对等清单：下拉选全量 + 表格列该组快照）；`null` = 全部分组 */
const selectedGroupId = ref<number | null>(null)

const typeOptions: { value: SnapshotType | 'All'; label: string }[] = [
  { value: 'All', label: '全部类型' },
  { value: 'Full', label: '全量' },
  { value: 'VirtualFull', label: '虚拟全量' },
  { value: 'Increment', label: '增量' },
]

/** 当前展示的组（选了具体分组就只留那一组） */
const visibleGroups = computed(() =>
  selectedGroupId.value === null
    ? groups.value
    : groups.value.filter((g) => g.fullSnapshot.id === selectedGroupId.value),
)

/** 平铺成表格行，带上所属分组信息 */
const rows = computed(() => {
  const out: (Snapshot & { groupTime: string; groupType: SnapshotType })[] = []
  for (const g of visibleGroups.value) {
    const list = [...g.snapshots].sort(
      (a, b) => new Date(b.beginTime).getTime() - new Date(a.beginTime).getTime(),
    )
    for (const s of list) {
      out.push({ ...s, groupTime: g.fullSnapshot.beginTime, groupType: g.fullSnapshot.type })
    }
  }
  return out
})

async function load(): Promise<void> {
  loading.value = true
  error.value = null
  try {
    const result = await snapshotsApi.listSnapshots(props.taskId, {
      page: page.value,
      pageSize: pageSize.value,
      type: typeFilter.value,
      from: range.value?.[0],
      to: range.value?.[1],
    })
    groups.value = result.items
    total.value = result.total
    // 选中的分组被筛掉了就回到「全部分组」
    if (selectedGroupId.value !== null && !result.items.some((g) => g.fullSnapshot.id === selectedGroupId.value)) {
      selectedGroupId.value = null
    }
  } catch (err) {
    error.value = asApiError(err)
    groups.value = []
    total.value = 0
  } finally {
    loading.value = false
  }
}

function search(): void {
  page.value = 1
  void load()
}

onMounted(load)
watch(page, load)
watch(() => props.taskId, search)

watch(
  () => realtime.resyncTick,
  () => {
    void load()
  },
)

/* ------------------------------- 删除（先看影响清单） ------------------------------- */

const impactVisible = ref(false)
const impactLoading = ref(false)
const impactError = ref<NormalizedApiError | null>(null)
const impact = ref<DeleteImpact | null>(null)
const impactTarget = ref<Snapshot | null>(null)
const deleting = ref(false)

async function askDelete(snapshot: Snapshot): Promise<void> {
  impactTarget.value = snapshot
  impactVisible.value = true
  impactLoading.value = true
  impactError.value = null
  impact.value = null
  try {
    impact.value = await snapshotsApi.getDeleteImpact(snapshot.id)
  } catch (err) {
    impactError.value = asApiError(err)
  } finally {
    impactLoading.value = false
  }
}

async function confirmDelete(): Promise<void> {
  const snapshot = impactTarget.value
  if (!snapshot) return
  deleting.value = true
  try {
    await snapshotsApi.deleteSnapshot(snapshot.id)
    ElMessage.success('快照已删除')
    impactVisible.value = false
    await load()
  } catch (err) {
    ElMessage.error(asApiError(err).message || '删除失败')
  } finally {
    deleting.value = false
  }
}

/* ------------------------------- 立即备份 ------------------------------- */

const backingUp = ref(false)

async function startBackup(type: SnapshotType): Promise<void> {
  backingUp.value = true
  try {
    const result = await tasksApi.startBackup(props.taskId, type)
    ElMessage.success(
      result.queuePosition
        ? `已排队（前面还有 ${result.queuePosition} 个作业）`
        : '备份已开始',
    )
  } catch (err) {
    ElMessage.error(asApiError(err).message || '启动备份失败')
  } finally {
    backingUp.value = false
  }
}

function backupTypeLabel(t: SnapshotType): string {
  return t === 'Full' ? '全量备份' : t === 'VirtualFull' ? '虚拟全量备份' : '增量备份'
}

/* 表格插槽里的 row 是 any，用带类型的辅助函数取文案，避免隐式 any 索引报错 */

function statusLabel(status: SnapshotStatus): string {
  return snapshotStatusLabels[status]
}

function statusTagType(status: SnapshotStatus): 'success' | 'primary' | 'danger' {
  return snapshotStatusTagType[status]
}

const groupOptions = computed(() =>
  groups.value.map((g) => ({
    id: g.fullSnapshot.id,
    label: `${formatDateTime(g.fullSnapshot.beginTime)} · ${backupTypeLabel(g.fullSnapshot.type)} · ${g.snapshots.length} 个快照`,
  })),
)
</script>

<template>
  <div class="snap-list">
    <div class="snap-list__filters">
      <el-select v-model="typeFilter" size="small" style="width: 130px" @change="search">
        <el-option v-for="o in typeOptions" :key="o.value" :label="o.label" :value="o.value" />
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

      <el-select v-model="selectedGroupId" size="small" style="width: 300px" placeholder="全部分组">
        <el-option :value="null" label="全部分组" />
        <el-option v-for="g in groupOptions" :key="g.id" :label="g.label" :value="g.id" />
      </el-select>

      <el-button size="small" :icon="Refresh" :loading="loading" @click="load">刷新</el-button>

      <div class="fbt-spacer" />

      <el-dropdown :disabled="backingUp" @command="(t: SnapshotType) => startBackup(t)">
        <el-button size="small" type="primary" :icon="VideoPlay" :loading="backingUp">
          立即备份
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="Full">全量备份</el-dropdown-item>
            <el-dropdown-item command="VirtualFull">虚拟全量备份</el-dropdown-item>
            <el-dropdown-item command="Increment">增量备份</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </div>

    <ErrorState v-if="error" :error="error" @retry="load" />
    <StatePlaceholder v-else-if="loading && rows.length === 0" text="正在加载快照…" />
    <StatePlaceholder v-else-if="rows.length === 0" empty description="没有符合条件的快照" />

    <template v-else>
      <el-table :data="rows" size="small" border row-key="id" max-height="52vh">
        <el-table-column label="类型" width="170">
          <template #default="{ row }">
            <SnapshotTypeTag :type="row.type" />
            <el-tag :type="statusTagType(row.status)" size="small" class="snap-list__status">
              {{ statusLabel(row.status) }}
            </el-tag>
          </template>
        </el-table-column>

        <el-table-column label="所属全量" width="160">
          <template #default="{ row }">
            <span class="fbt-mono fbt-footnote">{{ formatDateTime(row.groupTime) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="新增" width="72" align="right">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatCount(row.createdFileCount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="修改" width="72" align="right">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatCount(row.modifiedFileCount) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="删除" width="72" align="right">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatCount(row.deletedFileCount) }}</span>
          </template>
        </el-table-column>

        <!-- 07 §7：「成功但不完整」必须醒目 —— 非绿色提示 -->
        <el-table-column label="失败" width="72" align="right">
          <template #default="{ row }">
            <span v-if="row.failedFileCount > 0" class="fbt-warn-count">
              {{ formatCount(row.failedFileCount) }}
            </span>
            <span v-else class="fbt-muted">0</span>
          </template>
        </el-table-column>

        <!-- 补齐项 1：占用空间列（旧界面只有文件计数） -->
        <el-table-column label="占用空间" width="110" align="right">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatBytes(row.totalBytes) }}</span>
          </template>
        </el-table-column>

        <el-table-column label="开始时间" width="160">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatDateTime(row.beginTime) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="完成时间" width="160">
          <template #default="{ row }">
            <span class="fbt-mono">{{ row.endTime ? formatDateTime(row.endTime) : '—' }}</span>
          </template>
        </el-table-column>

        <el-table-column label="信息" min-width="140">
          <template #default="{ row }">
            <span v-if="row.abortReason" class="fbt-danger-count">{{ row.abortReason }}</span>
            <span v-else-if="row.failedFileCount > 0" class="fbt-warn-count">
              成功（{{ formatCount(row.failedFileCount) }} 个文件失败）
            </span>
            <span v-else class="fbt-muted">—</span>
          </template>
        </el-table-column>

        <el-table-column label="操作" width="230" fixed="right">
          <template #default="{ row }">
            <el-button
              size="small"
              text
              type="primary"
              :icon="FolderOpened"
              :disabled="row.status !== 'Completed'"
              @click="emit('browse', row.id)"
            >
              浏览文件
            </el-button>
            <el-button size="small" text @click="emit('changes', row.id)">变更内容</el-button>
            <el-button size="small" text type="danger" :icon="Delete" @click="askDelete(row)">
              删除
            </el-button>
          </template>
        </el-table-column>
      </el-table>

      <div class="snap-list__foot">
        <span class="fbt-footnote">
          共 {{ formatCount(total) }} 个分组（每页 {{ pageSize }}）
        </span>
        <div class="fbt-spacer" />
        <el-pagination
          v-model:current-page="page"
          :page-size="pageSize"
          :total="total"
          layout="prev, pager, next, sizes"
          :page-sizes="[10, 20, 50]"
          small
          @size-change="(s: number) => { pageSize = s; page = 1; load() }"
        />
      </div>
    </template>

    <!-- 删除影响清单：列出全部受影响的快照（07 §7） -->
    <el-dialog v-model="impactVisible" title="删除快照 —— 影响范围" width="760px">
      <ErrorState v-if="impactError" :error="impactError" compact />
      <StatePlaceholder v-else-if="impactLoading" text="正在计算影响范围…" />
      <template v-else-if="impact">
        <el-alert
          type="warning"
          :closable="false"
          show-icon
          title="删除快照会级联删除它到下一个全量/虚拟全量（不含）之间的所有快照，且不可撤销。"
        />
        <el-descriptions :column="2" size="small" border style="margin-top: 12px">
          <el-descriptions-item label="目标快照">
            <span class="fbt-mono">#{{ impact.snapshotId }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="将删除的快照数">
            <span class="fbt-danger-count">{{ impact.affectedCount }}</span>
          </el-descriptions-item>
          <el-descriptions-item label="涉及文件记录数">
            {{ formatCount(impact.affectedFileCount) }}
          </el-descriptions-item>
          <el-descriptions-item label="其中软删除">
            {{ formatCount(impact.softDeletedFileCount) }}
          </el-descriptions-item>
          <el-descriptions-item label="可能移入回收站">
            {{ formatBytes(impact.reclaimableBytes) }}
          </el-descriptions-item>
        </el-descriptions>

        <div v-if="impact.notices && impact.notices.length" class="snap-list__notices">
          <el-alert
            v-for="(n, i) in impact.notices"
            :key="i"
            type="info"
            :closable="false"
            :title="n"
          />
        </div>

        <div class="fbt-pane-title" style="margin-top: 12px">
          将被一并删除的全部快照（{{ impact.affectedSnapshots.length }}）
        </div>
        <el-table :data="impact.affectedSnapshots" size="small" border max-height="240">
          <el-table-column label="快照" width="90">
            <template #default="{ row }">
              <span class="fbt-mono">#{{ row.id }}</span>
            </template>
          </el-table-column>
          <el-table-column label="类型" width="110">
            <template #default="{ row }">
              <SnapshotTypeTag :type="row.type" />
            </template>
          </el-table-column>
          <el-table-column label="开始时间" width="160">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatDateTime(row.beginTime) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="占用空间" width="110" align="right">
            <template #default="{ row }">
              <span class="fbt-mono">{{ formatBytes(row.totalBytes) }}</span>
            </template>
          </el-table-column>
          <el-table-column label="失败" width="80" align="right">
            <template #default="{ row }">
              <span :class="row.failedFileCount > 0 ? 'fbt-warn-count' : 'fbt-muted'">
                {{ row.failedFileCount }}
              </span>
            </template>
          </el-table-column>
        </el-table>
      </template>

      <template #footer>
        <el-button @click="impactVisible = false">取消</el-button>
        <el-button
          type="danger"
          :loading="deleting"
          :disabled="!impact"
          @click="confirmDelete"
        >
          确认删除 {{ impact?.affectedCount ?? 0 }} 个快照
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.snap-list__filters {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}

.snap-list__status {
  margin-left: 4px;
}

.snap-list__foot {
  display: flex;
  align-items: center;
  margin-top: 8px;
}

.snap-list__notices {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-top: 10px;
}
</style>
