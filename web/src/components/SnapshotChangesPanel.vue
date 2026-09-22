<script setup lang="ts">
/**
 * 变更内容（07 §4.3）—— 三列并排：`新增的文件` / `修改的文件` / `删除的文件`。
 *
 * 每列带 `共 N 项` 尾部统计（对等清单要求）。
 */
import { computed, ref, watch } from 'vue'
import { asApiError, snapshotsApi } from '@/api'
import type { ChangeEntry, NormalizedApiError, SnapshotChanges } from '@/api/types'
import ErrorState from './ErrorState.vue'
import PathText from './PathText.vue'
import StatePlaceholder from './StatePlaceholder.vue'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const props = defineProps<{ snapshotId: number | null }>()

const loading = ref(false)
const error = ref<NormalizedApiError | null>(null)
const changes = ref<SnapshotChanges | null>(null)

const columns = computed(() => {
  const c = changes.value
  return [
    { key: 'created' as const, title: '新增的文件', type: 'success' as const, page: c?.created },
    { key: 'modified' as const, title: '修改的文件', type: 'warning' as const, page: c?.modified },
    { key: 'deleted' as const, title: '删除的文件', type: 'danger' as const, page: c?.deleted },
  ]
})

async function load(): Promise<void> {
  if (props.snapshotId === null) {
    changes.value = null
    return
  }
  loading.value = true
  error.value = null
  try {
    changes.value = await snapshotsApi.getSnapshotChanges(props.snapshotId, { page: 1, pageSize: 200 })
  } catch (err) {
    error.value = asApiError(err)
    changes.value = null
  } finally {
    loading.value = false
  }
}

watch(() => props.snapshotId, load, { immediate: true })

defineExpose({ reload: load })

function nameOf(entry: ChangeEntry): string {
  return entry.name || entry.relativePath.split(/[\\/]/).pop() || entry.relativePath
}
</script>

<template>
  <ErrorState v-if="error" :error="error" compact @retry="load" />

  <StatePlaceholder
    v-else-if="snapshotId === null"
    empty
    description="请先在上方选择一个快照"
  />

  <StatePlaceholder v-else-if="loading" text="正在加载变更内容…" />

  <div v-else class="fbt-change-columns">
    <div v-for="col in columns" :key="col.key" class="fbt-pane">
      <div class="fbt-pane-title">
        <el-tag :type="col.type" size="small" effect="plain">{{ col.title }}</el-tag>
      </div>
      <el-table :data="col.page?.items ?? []" size="small" :show-header="true" max-height="360" border>
        <el-table-column label="文件名" min-width="180">
          <template #default="{ row }">
            <el-tooltip :content="row.relativePath" placement="top" :show-after="300">
              <span class="fbt-ellipsis change__name">{{ nameOf(row) }}</span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="路径" min-width="180">
          <template #default="{ row }">
            <PathText :path="row.relativePath" :max-length="34" />
          </template>
        </el-table-column>
        <el-table-column label="文件大小" width="110" align="right">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatBytes(row.length) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="修改时间" width="160">
          <template #default="{ row }">
            <span class="fbt-mono">{{ formatDateTime(row.fileTimeUtc) }}</span>
          </template>
        </el-table-column>
        <el-table-column label="信息" min-width="160">
          <template #default="{ row }">
            <span v-if="row.info" class="fbt-warn-count">{{ row.info }}</span>
            <span v-else class="fbt-muted">—</span>
          </template>
        </el-table-column>
      </el-table>
      <div class="fbt-footnote">
        共 {{ formatCount(col.page?.total ?? 0) }} 项
        <span v-if="col.page && col.page.total > col.page.items.length" class="fbt-muted">
          （当前显示前 {{ formatCount(col.page.items.length) }} 项）
        </span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.change__name {
  display: inline-block;
  max-width: 100%;
  vertical-align: bottom;
}
</style>
