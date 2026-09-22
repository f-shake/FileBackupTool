<script setup lang="ts">
/**
 * 还原向导（07 §4.5 / 04 §1）。
 *
 * 流程：**先预演后执行**。
 *   1. 选范围（整快照 / 目录 / 多选文件）
 *   2. 选目标（白名单还原根 —— 不接受任意路径）
 *   3. 预演（`/api/restore/plan`）→ 将写入的文件数、总大小、**冲突明细**
 *   4. 定策略（跳过/覆盖/保留两者/较新者优先），可"应用到全部"，可逐条覆盖
 *   5. 执行 → 入队 → 进度 → 结果汇总（成功/跳过/失败 + 失败清单）
 *
 * 两处必须做到的行为（新版相对旧版的能力底线）：
 *   - 目标已存在时**绝不静默覆盖**（旧版用 FileMode.Create 静默覆盖）
 *   - 有不可恢复的文件时**先列出再让用户决定**是否还原其余文件（04 §1.6）
 */
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { asApiError, restoreApi } from '@/api'
import type {
  ConflictStrategy,
  NormalizedApiError,
  RestorePlan,
  RestorePlanItem,
  RestoreResult,
  RestoreRoot,
  RestoreScope,
} from '@/api/types'
import ErrorState from './ErrorState.vue'
import StatePlaceholder from './StatePlaceholder.vue'
import { conflictStrategyHelp, conflictStrategyLabels } from '@/locales/zh-CN'
import { useRealtimeStore } from '@/stores/realtime'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDateTime } from '@/utils/datetime'

const props = withDefaults(
  defineProps<{
    modelValue: boolean
    snapshotId: number
    /** 入口给出的初始范围 */
    initialScope?: RestoreScope
    initialDirectory?: string | null
    initialPaths?: string[]
    /** 用于展示 */
    taskName?: string
  }>(),
  { initialScope: 'Snapshot', initialDirectory: null, initialPaths: () => [], taskName: '' },
)

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()

const realtime = useRealtimeStore()

const step = ref(0)
const scope = ref<RestoreScope>('Snapshot')
const targetRoot = ref('')
const targetSubPath = ref('')
const defaultStrategy = ref<ConflictStrategy>('Skip')
const overrides = ref<Record<string, ConflictStrategy>>({})
const acknowledgedBlocked = ref(false)

const roots = ref<RestoreRoot[]>([])
const rootsLoading = ref(false)
const rootsError = ref<NormalizedApiError | null>(null)

const plan = ref<RestorePlan | null>(null)
const planLoading = ref(false)
const planError = ref<NormalizedApiError | null>(null)

const executing = ref(false)
const executeError = ref<NormalizedApiError | null>(null)
const jobId = ref<string | null>(null)
const queuePosition = ref<number | null>(null)
const progress = ref<{ processed: number; total: number; currentPath: string } | null>(null)
const result = ref<RestoreResult | null>(null)

const strategies: ConflictStrategy[] = ['Skip', 'Overwrite', 'KeepBoth', 'NewerWins']

const title = computed(() => (props.taskName ? `还原 · ${props.taskName}` : '还原'))

const selectedRoot = computed(() => roots.value.find((r) => r.path === targetRoot.value) ?? null)

const conflictItems = computed(() => (plan.value?.items ?? []).filter((i) => i.conflict))

const blockedItems = computed(() => plan.value?.blockedItems ?? [])

const canExecute = computed(() => {
  if (!plan.value) return false
  if (blockedItems.value.length > 0 && !acknowledgedBlocked.value) return false
  return plan.value.writeCount > 0 || plan.value.skipCount > 0
})

const progressPercent = computed(() => {
  if (!progress.value || progress.value.total <= 0) return 0
  return Math.min(100, Math.round((progress.value.processed / progress.value.total) * 1000) / 10)
})

watch(
  () => props.modelValue,
  async (visible) => {
    if (!visible) return
    step.value = 0
    scope.value = props.initialScope
    targetSubPath.value = ''
    overrides.value = {}
    acknowledgedBlocked.value = false
    plan.value = null
    planError.value = null
    executeError.value = null
    result.value = null
    jobId.value = null
    progress.value = null
    queuePosition.value = null
    await loadRoots()
  },
)

// 还原进度走 SignalR（07 §6）。这里始终监听，用 jobId 过滤掉别的作业。
realtime.on('restore.progress', (e) => {
  if (jobId.value && e.jobId !== jobId.value) return
  progress.value = { processed: e.processed, total: e.total, currentPath: e.currentPath }
})
async function loadRoots(): Promise<void> {
  rootsLoading.value = true
  rootsError.value = null
  try {
    roots.value = await restoreApi.listRestoreRoots()
    if (!targetRoot.value && roots.value.length > 0) {
      targetRoot.value = roots.value.find((r) => r.exists)?.path ?? roots.value[0]!.path
    }
  } catch (err) {
    rootsError.value = asApiError(err)
  } finally {
    rootsLoading.value = false
  }
}

function buildRequest() {
  return {
    scope: scope.value,
    snapshotId: props.snapshotId,
    directory: scope.value === 'Directory' ? props.initialDirectory : null,
    paths: scope.value === 'Files' ? props.initialPaths : [],
    targetRoot: targetRoot.value,
    targetSubPath: targetSubPath.value || null,
    defaultStrategy: defaultStrategy.value,
    strategies: overrides.value,
  }
}

async function runPlan(): Promise<void> {
  planLoading.value = true
  planError.value = null
  try {
    plan.value = await restoreApi.planRestore(buildRequest())
  } catch (err) {
    planError.value = asApiError(err)
    plan.value = null
  } finally {
    planLoading.value = false
  }
}

/** 改策略后需要重新预演：动作序列（跳过/覆盖/重命名）由后端按策略计算 */
async function applyStrategy(strategy: ConflictStrategy): Promise<void> {
  defaultStrategy.value = strategy
  overrides.value = {}
  if (step.value >= 2) await runPlan()
}

async function overrideOne(item: RestorePlanItem, strategy: ConflictStrategy): Promise<void> {
  overrides.value = { ...overrides.value, [item.relativePath]: strategy }
  await runPlan()
}

async function execute(): Promise<void> {
  executing.value = true
  executeError.value = null
  result.value = null
  progress.value = null

  try {
    const handle = await restoreApi.executeRestore({
      ...buildRequest(),
      planToken: plan.value?.planToken ?? null,
    })
    jobId.value = handle.jobId
    queuePosition.value = handle.queuePosition ?? null
    ElMessage.success(
      handle.queuePosition && handle.queuePosition > 1
        ? `已排队，前面还有 ${handle.queuePosition - 1} 个作业`
        : '已加入队列',
    )
    await pollUntilDone(handle.jobId)
  } catch (err) {
    executeError.value = asApiError(err)
  } finally {
    executing.value = false
  }
}

/**
 * 轮询作业直到结束。
 * 这里不依赖 SignalR —— 它在降级为轮询模式时正是唯一可用的通道（07 §6）。
 */
async function pollUntilDone(id: string): Promise<void> {
  const { monitoringApi } = await import('@/api')
  const deadline = Date.now() + 6 * 60 * 60 * 1000
  while (Date.now() < deadline) {
    await new Promise((r) => setTimeout(r, 1500))
    let job: Awaited<ReturnType<typeof monitoringApi.getJob>>
    try {
      job = await monitoringApi.getJob(id)
    } catch {
      // 作业查询失败不终止：可能只是端点尚未实现，让用户看到进度停在原地
      return
    }

    if (job.progress) {
      progress.value = {
        processed: job.progress.processed,
        total: job.progress.total ?? job.progress.discovered,
        currentPath: job.progress.currentPath ?? '',
      }
    }

    if (job.state === 'Completed' || job.state === 'Failed' || job.state === 'Aborted') {
      const r = job.result
      result.value = {
        jobId: id,
        succeeded: r?.succeeded ?? progress.value?.processed ?? 0,
        skipped: r?.skipped ?? 0,
        failed: r?.failed ?? (job.state === 'Failed' ? 1 : 0),
        totalBytes: r?.totalBytes ?? 0,
        elapsedMs: null,
        failures: r?.failures ?? (job.error ? [{ relativePath: '', targetPath: '', error: job.error }] : []),
      }
      return
    }
  }
}

function close(): void {
  emit('update:modelValue', false)
}

function next(): void {
  if (step.value === 1 && !targetRoot.value) {
    ElMessage.warning('请选择一个白名单还原根目录')
    return
  }
  if (step.value === 2) {
    void runPlan().then(() => {
      step.value = 3
    })
    return
  }
  if (step.value === 3) {
    step.value = 4
    void execute()
    return
  }
  step.value += 1
}
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="title"
    width="960px"
    top="5vh"
    append-to-body
    :close-on-click-modal="false"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <el-steps :active="step" finish-status="success" align-center class="fbt-section">
      <el-step title="选范围" />
      <el-step title="选目标" />
      <el-step title="预演" />
      <el-step title="定策略" />
      <el-step title="执行" />
    </el-steps>

    <!-- 1. 选范围 -->
    <div v-if="step === 0">
      <el-radio-group v-model="scope">
        <el-radio-button value="Snapshot">整个快照</el-radio-button>
        <el-radio-button value="Directory" :disabled="!initialDirectory">该目录及其子目录</el-radio-button>
        <el-radio-button value="Files" :disabled="initialPaths.length === 0">已选中的文件</el-radio-button>
      </el-radio-group>

      <el-descriptions :column="1" border size="small" class="fbt-section" style="margin-top: 12px">
        <el-descriptions-item label="快照 ID">{{ snapshotId }}</el-descriptions-item>
        <el-descriptions-item v-if="scope === 'Directory'" label="目录">
          <span class="fbt-mono">{{ initialDirectory }}</span>
        </el-descriptions-item>
        <el-descriptions-item v-if="scope === 'Files'" label="已选文件">
          共 {{ formatCount(initialPaths.length) }} 个
        </el-descriptions-item>
      </el-descriptions>

      <el-alert
        type="info"
        :closable="false"
        show-icon
        title="只还原图片里存在的文件"
        description="镜像 = 该快照的物化状态，已被删除的文件不在镜像中，因此不会被还原。要看某个快照删了什么，请用「变更内容」。"
      />
    </div>

    <!-- 2. 选目标 -->
    <div v-else-if="step === 1">
      <ErrorState v-if="rootsError" :error="rootsError" @retry="loadRoots" />
      <StatePlaceholder v-else-if="rootsLoading" text="正在加载还原根白名单…" />

      <template v-else>
        <el-alert
          type="warning"
          :closable="false"
          show-icon
          class="fbt-section"
          title="还原目标必须落在白名单还原根之内"
          description="这是硬约束：还原是写文件操作，白名单是最重要的缓解手段。白名单在「设置 → 还原根白名单」里维护。"
        />

        <el-empty v-if="roots.length === 0" description="白名单为空。请先到「设置」里配置至少一个还原根目录，否则无法还原。" />

        <el-form v-else label-width="120px">
          <el-form-item label="还原根" required>
            <el-select v-model="targetRoot" placeholder="选择白名单中的还原根" style="width: 100%">
              <el-option
                v-for="root in roots"
                :key="root.path"
                :value="root.path"
                :disabled="!root.exists"
                :label="root.path"
              >
                <span class="fbt-mono">{{ root.path }}</span>
                <span v-if="root.label" class="fbt-muted"> · {{ root.label }}</span>
                <span v-if="!root.exists" class="fbt-danger-count"> · 不存在</span>
              </el-option>
            </el-select>
          </el-form-item>

          <el-form-item label="目标子路径">
            <el-input
              v-model="targetSubPath"
              placeholder="可选。留空则直接还原到还原根下"
            />
          </el-form-item>

          <el-descriptions v-if="selectedRoot" :column="2" border size="small">
            <el-descriptions-item label="可用空间">
              {{ selectedRoot.freeBytes === null ? '未知' : formatBytes(selectedRoot.freeBytes) }}
            </el-descriptions-item>
            <el-descriptions-item label="总容量">
              {{ selectedRoot.totalBytes === null ? '未知' : formatBytes(selectedRoot.totalBytes) }}
            </el-descriptions-item>
          </el-descriptions>
        </el-form>
      </template>
    </div>

    <!-- 3. 预演 -->
    <div v-else-if="step === 2">
      <ErrorState v-if="planError" :error="planError" @retry="runPlan" />
      <StatePlaceholder v-else-if="planLoading" text="正在预演（列出将写入的文件与冲突）…" />

      <template v-else-if="plan">
        <el-descriptions :column="4" border size="small" class="fbt-section">
          <el-descriptions-item label="将写入">{{ formatCount(plan.writeCount) }} 个文件</el-descriptions-item>
          <el-descriptions-item label="总大小">{{ formatBytes(plan.totalBytes) }}</el-descriptions-item>
          <el-descriptions-item label="冲突">
            <span :class="plan.conflictCount > 0 ? 'fbt-warn-count' : ''">
              {{ formatCount(plan.conflictCount) }} 个
            </span>
          </el-descriptions-item>
          <el-descriptions-item label="不可还原">
            <span :class="plan.blockedCount > 0 ? 'fbt-danger-count' : ''">
              {{ formatCount(plan.blockedCount) }} 个
            </span>
          </el-descriptions-item>
        </el-descriptions>

        <el-alert
          v-if="blockedItems.length > 0"
          type="error"
          :closable="false"
          show-icon
          class="fbt-section"
          :title="`有 ${blockedItems.length} 个文件不可还原`"
          description="下面列出受影响的文件。它们可能是虚拟全量生成的（没有实际备份文件），或 blob 已丢失。请确认后再决定是否继续还原其余文件 —— 不会静默跳过。"
        />

        <el-table
          v-if="blockedItems.length > 0"
          :data="blockedItems"
          size="small"
          border
          max-height="180"
          class="fbt-section"
        >
          <el-table-column label="文件" min-width="260">
            <template #default="{ row }"><span class="fbt-mono">{{ row.relativePath }}</span></template>
          </el-table-column>
          <el-table-column label="原因" min-width="300">
            <template #default="{ row }"><span class="fbt-danger-count">{{ row.blockedReason }}</span></template>
          </el-table-column>
        </el-table>

        <el-alert
          v-if="plan.conflictCount > 0"
          type="warning"
          :closable="false"
          show-icon
          class="fbt-section"
          title="存在目标已存在的文件"
          description="默认策略是「跳过」，不会静默覆盖（旧版用 FileMode.Create 会静默覆盖目标文件，这是必须修掉的）。"
        />

        <div class="fbt-pane-title">将写入的清单（预览前 200 条）</div>
        <el-table :data="plan.items.slice(0, 200)" size="small" border max-height="300">
          <el-table-column label="相对路径" min-width="240">
            <template #default="{ row }"><span class="fbt-mono">{{ row.relativePath }}</span></template>
          </el-table-column>
          <el-table-column label="目标路径" min-width="280">
            <template #default="{ row }"><span class="fbt-mono">{{ row.targetPath }}</span></template>
          </el-table-column>
          <el-table-column label="大小" width="100" align="right">
            <template #default="{ row }">{{ formatBytes(row.length) }}</template>
          </el-table-column>
          <el-table-column label="动作" width="110">
            <template #default="{ row }">
              <el-tag
                size="small"
                :type="row.action === 'Skip' ? 'info' : row.action === 'Blocked' ? 'danger' : row.action === 'Overwrite' ? 'warning' : 'success'"
                effect="plain"
              >
                {{ row.action === 'Create' ? '新建' : row.action === 'Overwrite' ? '覆盖' : row.action === 'Skip' ? '跳过' : row.action === 'Rename' ? '重命名保留' : '不可还原' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column label="冲突" width="90">
            <template #default="{ row }">
              <span v-if="row.conflict" class="fbt-warn-count">是</span>
              <span v-else class="fbt-muted">否</span>
            </template>
          </el-table-column>
        </el-table>
      </template>
    </div>

    <!-- 4. 定策略 -->
    <div v-else-if="step === 3">
      <el-form label-width="140px">
        <el-form-item label="默认冲突策略">
          <el-select :model-value="defaultStrategy" style="width: 220px" @update:model-value="applyStrategy">
            <el-option v-for="s in strategies" :key="s" :value="s" :label="conflictStrategyLabels[s]">
              <span>{{ conflictStrategyLabels[s] }}</span>
              <span class="fbt-muted"> — {{ conflictStrategyHelp[s] }}</span>
            </el-option>
          </el-select>
          <div class="fbt-footnote">默认「跳过」——选最不具破坏性的那个。改策略会重新预演。</div>
        </el-form-item>
      </el-form>

      <template v-if="conflictItems.length === 0">
        <el-alert type="success" :closable="false" show-icon title="没有冲突，可以直接执行" />
      </template>

      <template v-else>
        <div class="fbt-pane-title">
          冲突明细（共 {{ formatCount(conflictItems.length) }} 项，可逐条覆盖策略）
        </div>
        <el-table :data="conflictItems" size="small" border max-height="320">
          <el-table-column label="相对路径" min-width="220">
            <template #default="{ row }"><span class="fbt-mono">{{ row.relativePath }}</span></template>
          </el-table-column>
          <el-table-column label="备份中" width="110" align="right">
            <template #default="{ row }">{{ formatBytes(row.length) }}</template>
          </el-table-column>
          <el-table-column label="目标现有" width="150" align="right">
            <template #default="{ row }">
              <div>{{ formatBytes(row.targetLength) }}</div>
              <div class="fbt-muted">{{ formatDateTime(row.targetFileTimeUtc) }}</div>
            </template>
          </el-table-column>
          <el-table-column label="策略" width="170">
            <template #default="{ row }">
              <el-select
                :model-value="overrides[row.relativePath] ?? defaultStrategy"
                size="small"
                @update:model-value="(v: ConflictStrategy) => overrideOne(row, v)"
              >
                <el-option v-for="s in strategies" :key="s" :value="s" :label="conflictStrategyLabels[s]" />
              </el-select>
            </template>
          </el-table-column>
          <el-table-column label="将执行" width="110">
            <template #default="{ row }">
              <el-tag size="small" effect="plain">
                {{ row.action === 'Overwrite' ? '覆盖' : row.action === 'Skip' ? '跳过' : row.action === 'Rename' ? '重命名保留' : '新建' }}
              </el-tag>
            </template>
          </el-table-column>
        </el-table>
      </template>

      <el-checkbox v-if="blockedItems.length > 0" v-model="acknowledgedBlocked" class="fbt-section">
        我已了解上述 {{ blockedItems.length }} 个文件无法还原，继续还原其余文件
      </el-checkbox>
    </div>

    <!-- 5. 执行 -->
    <div v-else>
      <ErrorState v-if="executeError" :error="executeError" />
      <StatePlaceholder v-else-if="!result" text="正在执行还原…" />

      <div v-if="!result && !executeError" class="restore__progress">
        <el-progress :percentage="progressPercent" :indeterminate="!progress" :stroke-width="12" />
        <div class="fbt-mono">
          {{ progress ? `${formatCount(progress.processed)} / ${formatCount(progress.total)}` : '等待作业开始…' }}
        </div>
        <div v-if="queuePosition && queuePosition > 1" class="fbt-warn-count">
          已排队，前面还有 {{ queuePosition - 1 }} 个作业
        </div>
        <PathText v-if="progress?.currentPath" :path="progress.currentPath" :max-length="80" />
        <el-alert
          type="info"
          :closable="false"
          show-icon
          title="还原纳入全局 IO 作业队列，与备份互斥"
          description="排队时界面上会显示「已排队，前面还有 N 个作业」。"
        />
      </div>

      <template v-else-if="result">
        <el-result
          :icon="result.failed > 0 ? 'warning' : 'success'"
          :title="result.failed > 0 ? '还原完成，但有失败' : '还原完成'"
        >
          <template #sub-title>
            <div class="restore__summary">
              <el-statistic title="成功" :value="result.succeeded" />
              <el-statistic title="跳过" :value="result.skipped" />
              <el-statistic title="失败" :value="result.failed" />
            </div>
          </template>
        </el-result>

        <el-alert
          v-if="result.failures.length > 0"
          type="error"
          :closable="false"
          show-icon
          class="fbt-section"
          :title="`${result.failures.length} 个文件还原失败`"
          description="旧设计只把错误弹一次，不做聚合。这里是完整清单。"
        />
        <el-table v-if="result.failures.length > 0" :data="result.failures" size="small" border>
          <el-table-column label="文件" min-width="240">
            <template #default="{ row }"><span class="fbt-mono">{{ row.relativePath }}</span></template>
          </el-table-column>
          <el-table-column label="目标" min-width="260">
            <template #default="{ row }"><span class="fbt-mono">{{ row.targetPath }}</span></template>
          </el-table-column>
          <el-table-column label="错误" min-width="300">
            <template #default="{ row }"><span class="fbt-danger-count">{{ row.error }}</span></template>
          </el-table-column>
        </el-table>
        <div v-if="result.totalBytes > 0" class="fbt-footnote">
          共写入 {{ formatBytes(result.totalBytes) }}，文件时间戳已按记录恢复。
        </div>
      </template>
    </div>

    <template #footer>
      <el-button @click="close">{{ step === 4 ? '关闭' : '取消' }}</el-button>
      <el-button v-if="step > 0 && step < 4" @click="step -= 1">上一步</el-button>
      <el-button
        v-if="step < 4"
        type="primary"
        :disabled="step === 2 && !canExecute"
        :loading="planLoading"
        @click="next"
      >
        {{ step === 3 ? '开始还原' : step === 2 ? '重新预演并继续' : '下一步' }}
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.restore__progress {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 4px;
}

.restore__summary {
  display: flex;
  gap: 40px;
  justify-content: center;
}
</style>
