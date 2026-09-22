<script setup lang="ts">
/**
 * 两段式进度条（06 §6）。
 *
 * 关键约束：文件数可能到百万级，**枚举阶段不能先数完再开工**，所以枚举阶段只能是不确定态
 * + 实时计数（「已发现 123,456 个文件」）；只有处理阶段才有确定的百分比。
 * 旧界面备份时完全没有进度条（07 §3 补齐项 7）。
 */
import { computed } from 'vue'
import { Loading } from '@element-plus/icons-vue'
import type { JobProgress } from '@/api/types'
import PathText from './PathText.vue'
import { formatBytes, formatCount } from '@/utils/format'
import { formatDuration } from '@/utils/datetime'
import { jobPhaseLabels } from '@/locales/zh-CN'

const props = withDefaults(
  defineProps<{
    progress: JobProgress | null | undefined
    /** 排队中的提示，例如「已排队，前面还有 2 个作业」 */
    queueNotice?: string | null
    /** 紧凑模式（卡片里） */
    compact?: boolean
  }>(),
  { queueNotice: null, compact: false },
)

const phase = computed(() => props.progress?.phase ?? null)

/** 处理阶段才有确定的百分比 */
const determinate = computed(() => {
  const p = props.progress
  return Boolean(p && p.phase === 'Processing' && p.total !== null && p.total > 0)
})

const percentage = computed(() => {
  const p = props.progress
  if (!p || p.total === null || p.total <= 0) return 0
  return Math.min(100, Math.round((p.processed / p.total) * 1000) / 10)
})

const phaseLabel = computed(() => (phase.value ? jobPhaseLabels[phase.value] : '准备中'))

const counterText = computed(() => {
  const p = props.progress
  if (!p) return ''
  if (p.phase === 'Enumerating') {
    return `已发现 ${formatCount(p.discovered)} 个文件`
  }
  if (p.phase === 'Processing') {
    const total = p.total === null ? '?' : formatCount(p.total)
    return `已备份 ${formatCount(p.processed)} / ${total}`
  }
  return ''
})

const bytesText = computed(() => {
  const p = props.progress
  if (!p || p.bytesTotal === null) {
    return p && p.bytesDone > 0 ? `已写入 ${formatBytes(p.bytesDone)}` : ''
  }
  return `${formatBytes(p.bytesDone)} / ${formatBytes(p.bytesTotal)}`
})

const remainingText = computed(() => {
  const p = props.progress
  if (!p || p.phase !== 'Processing' || p.total === null || p.total <= 0 || p.processed <= 0) {
    return ''
  }
  return `剩余约 ${formatDuration((p.total - p.processed) * 0.5)}`
})
</script>

<template>
  <div v-if="progress || queueNotice" class="job-progress" :class="{ 'is-compact': compact }">
    <div class="job-progress__head">
      <span class="job-progress__phase">
        <el-icon v-if="!determinate" class="is-loading"><Loading /></el-icon>
        {{ progress ? phaseLabel : '已排队' }}
      </span>
      <span v-if="counterText" class="job-progress__counter fbt-mono">{{ counterText }}</span>
      <span v-if="determinate" class="job-progress__percent fbt-mono">{{ percentage.toFixed(1) }}%</span>
    </div>

    <el-progress
      v-if="progress"
      :percentage="determinate ? percentage : 0"
      :indeterminate="!determinate"
      :stroke-width="compact ? 6 : 10"
      :duration="2"
      :show-text="false"
      :status="progress.phase === 'Committing' ? 'warning' : undefined"
    />

    <div class="job-progress__foot">
      <span v-if="queueNotice" class="job-progress__queue">{{ queueNotice }}</span>
      <span v-if="bytesText" class="fbt-mono">{{ bytesText }}</span>
      <span v-if="remainingText" class="fbt-muted">{{ remainingText }}</span>
      <PathText v-if="progress?.currentPath" :path="progress.currentPath" :max-length="60" />
    </div>
  </div>
</template>

<style scoped>
.job-progress {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.job-progress__head {
  display: flex;
  align-items: baseline;
  gap: 10px;
  font-size: 13px;
}

.job-progress__phase {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-weight: 600;
}

.job-progress__counter,
.job-progress__percent {
  color: var(--el-text-color-regular);
  font-size: 12px;
}

.job-progress__percent {
  margin-left: auto;
  font-weight: 600;
}

.job-progress__foot {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
  font-size: 12px;
  color: var(--el-text-color-secondary);
}

.job-progress__queue {
  color: var(--el-color-warning);
  font-weight: 600;
}
</style>
