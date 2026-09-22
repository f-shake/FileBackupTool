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
import PathText from './PathText.vue'
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
/** 已经发出取消请求、正在等作业真正停下来 */
const cancelling = ref(false)
const executeError = ref<NormalizedApiError | null>(null)
const jobId = ref<string | null>(null)
const queuePosition = ref<number | null>(null)
const progress = ref<{ processed: number; total: number; currentPath: string } | null>(null)
const result = ref<RestoreResult | null>(null)
/** 这次还原是否以"已取消"结束（`Aborted`）—— 终态卡据此换图标与标题 */
const canceled = ref(false)

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
    canceled.value = false
    jobId.value = null
    progress.value = null
    queuePosition.value = null
    cancelling.value = false
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

/**
 * 进入"预演"这一步就自动跑一次。
 *
 * ⚠️ 这**不是**可选的优化，是修一个死路：这一步底部的按钮是
 * `:disabled="step === 2 && !canExecute"`，而 `canExecute` 第一条就是
 * `if (!plan.value) return false` —— 于是"点按钮去预演"永远点不动：
 * 走到第 2 步时按钮已经禁用，而 `plan` 又只能由 `runPlan()` 赋值。
 * 曾经的表现是：用户走到"预演"看到一块空白面板 + 灰按钮，
 * 没有任何错误提示，只能取消退出——**整个还原功能都走不通**。
 */
watch(step, (value) => {
  if (value === 2 && !plan.value && !planLoading.value) void runPlan()
})

/**
 * 输入一变就让计划作废。
 *
 * 不加这条会有"预览的是一个东西、执行的是另一个东西"：`plan` 只在打开向导时被清空，
 * 所以"上一步改范围 / 改还原根 → 下一步"会**沿用旧计划**——面板上的"将写入 N 个文件"
 * 与清单里的目标路径全是旧输入的，而 `buildRequest()` 读的已经是新值。
 *
 * 服务端**不会**兜住这件事：`RestoreController` 明确写着"接受 planToken 字段并原样忽略"
 * （恒返回 `null`），所以没有"计划与执行不一致"的检查。引擎执行时逐条重判冲突，
 * 因此不会误覆盖文件，但**用户确认过的落点是错的**——这比没有预演更糟。
 *
 * 计划作废后，上面那个 `watch(step)` 的判据自然会重新触发一次预演。
 */
watch([scope, targetRoot, targetSubPath], () => {
  plan.value = null
  planError.value = null
})

async function overrideOne(item: RestorePlanItem, strategy: ConflictStrategy): Promise<void> {
  overrides.value = { ...overrides.value, [item.relativePath]: strategy }
  await runPlan()
}

async function execute(): Promise<void> {
  executing.value = true
  executeError.value = null
  result.value = null
  canceled.value = false
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

      // **取消要单独成一态。** 原来 `Aborted` 与 `Completed` 混在一个分支里，于是：
      //  ① 终态卡显示的是绿色的"还原完成"（对勾）——用户刚点了取消；
      //  ② 排队中取消时服务端写的 `Error = "已取消（尚未开始）"` 会被下面那句
      //     "有 error 就算一个失败文件"塞进 failures，于是同屏出现
      //     绿色"还原完成" + 红色"1 个文件还原失败" + 一行 relativePath 为空的表格。
      canceled.value = job.state === 'Aborted'

      result.value = {
        jobId: id,
        succeeded: r?.succeeded ?? progress.value?.processed ?? 0,
        skipped: r?.skipped ?? 0,
        failed: r?.failed ?? (job.state === 'Failed' ? 1 : 0),
        totalBytes: r?.totalBytes ?? 0,
        elapsedMs: null,
        // 只有**真失败**才把 job.error 当失败条目；取消原因是状态说明，不是文件失败。
        failures: r?.failures
          ?? (job.state === 'Failed' && job.error
            ? [{ relativePath: '', targetPath: '', error: job.error }]
            : []),
      }
      return
    }
  }
}

function close(): void {
  emit('update:modelValue', false)
}

/**
 * 取消正在执行的还原作业。
 *
 * 07 §7 明确要求「长任务一律有进度、可取消（备份、还原、校验、回收…）」——
 * 原来这一步只有「关闭」：弹窗关掉**后端作业照跑**，用户既看不到进度也没有任何办法停下，
 * 而 `restoreApi.cancelRestore` 其实早就实现好了，只是全仓库没有一个调用点（死代码）。
 *
 * 这里刻意**不**中断 `pollUntilDone`：让它一直跟到作业真正进入终态（服务端会把它报成
 * `Aborted`），这样用户看到的 "已取消" 是**服务端确认过的**，而不是前端的乐观假设。
 */
async function cancelExecution(): Promise<void> {
  if (!jobId.value || cancelling.value) return

  cancelling.value = true
  try {
    await restoreApi.cancelRestore(jobId.value)
    ElMessage.success('已发送取消请求，正在等待作业停止…')
  } catch (err) {
    ElMessage.error(asApiError(err).message || '取消还原作业失败')
    cancelling.value = false
  }
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

        <!-- 一个可执行条目都没有时按钮会是禁用的，必须说明**为什么**，
             否则用户面对的就是一块没有出口的面板（这正是原来第 2 步的症状）。 -->
        <el-alert
          v-if="plan.writeCount === 0 && plan.skipCount === 0"
          type="warning"
          :closable="false"
          show-icon
          class="fbt-section"
          title="这个范围内没有可还原的文件"
          description="所有条目都不可恢复（可能由虚拟快照生成，或 blob 已丢失），因此无法继续。请返回上一步改选范围，或换一个快照。"
        />

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

        <!-- 这个勾选框**必须**在第 2 步也能勾上。
             `canExecute` 的第二条判据是 `blockedItems.length > 0 && !acknowledgedBlocked`，
             而底部按钮的禁用条件是 `step === 2 && !canExecute` —— 只在第 3 步放这个勾选框的话，
             用户在"预演"这一步会看到一个**灰色且点不亮**的按钮，却找不到任何能勾的东西。
             这与修复前那个死路是同一类（没有出口的房间），而且它恰好发生在
             04 §1.6 明确要求支持的"有文件不可还原"场景上。 -->
        <el-checkbox v-if="blockedItems.length > 0" v-model="acknowledgedBlocked" class="fbt-section">
          我已了解上述 {{ blockedItems.length }} 个文件无法还原，继续还原其余文件
        </el-checkbox>

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
          :icon="canceled ? 'info' : result.failed > 0 ? 'warning' : 'success'"
          :title="canceled ? '还原已取消' : result.failed > 0 ? '还原完成，但有失败' : '还原完成'"
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
          v-if="canceled"
          type="info"
          :closable="false"
          show-icon
          class="fbt-section"
          title="这次还原被取消了"
          description="已经写入的文件保留在目标目录，其余文件未被处理。需要继续时重新发起一次还原即可。"
        />

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
      <!-- 执行中必须有取消入口（07 §7：长任务一律可取消）。
           作业在**服务端**跑，关掉这个弹窗并不会让它停下——用户只是再也看不到它，
           所以这个按钮不是可选项。 -->
      <el-button
        v-if="step === 4"
        type="danger"
        plain
        :loading="cancelling"
        :disabled="!jobId || !!result || !!executeError"
        @click="cancelExecution"
      >
        取消作业
      </el-button>
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
