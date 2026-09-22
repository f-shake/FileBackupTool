<script setup lang="ts">
/**
 * 任务日志 `/tasks/:id/logs`（07 §2、§4.4）。
 *
 * **独立路由**（不是任务详情里的 Tab），便于从别处跳转并带上筛选参数分享链接。
 * 例如 `/tasks/abc/logs?level=Error&keyword=blob&from=2026-09-01T00:00:00`。
 */
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeft } from '@element-plus/icons-vue'
import { asApiError, tasksApi } from '@/api'
import type { BackupTask, LogLevel, NormalizedApiError } from '@/api/types'
import ErrorState from '@/components/ErrorState.vue'
import PathText from '@/components/PathText.vue'
import TaskLogsPanel from '@/components/TaskLogsPanel.vue'

const route = useRoute()
const router = useRouter()

const taskId = computed(() => String(route.params.id ?? ''))

/** 从 query 带入的初始筛选（可分享的链接） */
const initialLevel = computed<LogLevel | 'All'>(() => {
  const raw = route.query.level
  const value = Array.isArray(raw) ? raw[0] : raw
  const allowed: (LogLevel | 'All')[] = [
    'All',
    'Trace',
    'Debug',
    'Information',
    'Warning',
    'Error',
    'Critical',
    'None',
  ]
  return allowed.includes(value as LogLevel | 'All') ? (value as LogLevel | 'All') : 'All'
})

const initialKeyword = computed(() => {
  const raw = route.query.keyword
  return (Array.isArray(raw) ? raw[0] : raw) ?? ''
})

const initialRange = computed<[string, string] | null>(() => {
  const from = route.query.from
  const to = route.query.to
  const f = Array.isArray(from) ? from[0] : from
  const t = Array.isArray(to) ? to[0] : to
  return f && t ? [f, t] : null
})

const task = ref<BackupTask | null>(null)
const taskError = ref<NormalizedApiError | null>(null)

async function loadTask(): Promise<void> {
  try {
    task.value = await tasksApi.getTask(taskId.value)
    taskError.value = null
  } catch (err) {
    taskError.value = asApiError(err)
    task.value = null
  }
}

onMounted(loadTask)
</script>

<template>
  <div class="fbt-page">
    <div class="fbt-page-header">
      <div>
        <el-button text :icon="ArrowLeft" @click="router.push({ name: 'task-detail', params: { id: taskId } })">
          返回任务
        </el-button>
        <h1 class="fbt-page-title">
          任务日志
          <span class="fbt-muted">· {{ task?.name ?? taskId }}</span>
        </h1>
        <div class="fbt-page-desc">
          <PathText :path="task?.sourceDir" :max-length="60" />
          <span class="fbt-muted"> 的备份日志（筛选条件写在 URL 里，可直接分享）</span>
        </div>
      </div>
    </div>

    <ErrorState v-if="taskError" :error="taskError" compact class="fbt-section" @retry="loadTask" />

    <div class="fbt-pane">
      <TaskLogsPanel
        :key="taskId"
        :task-id="taskId"
        :initial-level="initialLevel"
        :initial-keyword="initialKeyword"
        :initial-range="initialRange"
      />
    </div>
  </div>
</template>
