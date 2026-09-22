<script setup lang="ts">
/**
 * 明确的错误态 UI。
 *
 * 要求（任务书）：接口 404 / 网络错误时**不能白屏、不能静默**。
 * 因此这里把 `code`、`message`、`detail` 全部展示出来，并给出可点的重试。
 */
import { computed } from 'vue'
import { Refresh, WarningFilled } from '@element-plus/icons-vue'
import type { NormalizedApiError } from '@/api/types'

const props = withDefaults(
  defineProps<{
    error: NormalizedApiError | null
    /** 覆盖标题 */
    title?: string
    /** 覆盖描述 */
    description?: string
    /** 是否显示重试按钮 */
    retryable?: boolean
    /** 紧凑模式（用于面板内） */
    compact?: boolean
  }>(),
  { title: '', description: '', retryable: true, compact: false },
)

const emit = defineEmits<{ retry: [] }>()

const heading = computed(() => {
  if (props.title) return props.title
  if (!props.error) return '出错了'
  if (props.error.status === 404) return '接口不存在'
  if (props.error.status === 401) return '登录状态已失效'
  if (props.error.status === 403) return '没有权限'
  if (props.error.backendUnavailable) return '无法连接后端'
  return '请求失败'
})

const hint = computed(() => {
  if (props.description) return props.description
  if (!props.error) return ''
  if (props.error.backendUnavailable) {
    return '后端服务可能尚未实现该端点或尚未启动。开发期请确认后端监听在 http://localhost:5000，并已由 Vite 代理 /api 与 /hubs。'
  }
  return ''
})
</script>

<template>
  <div class="error-state" :class="{ 'is-compact': compact }">
    <el-icon class="error-state__icon"><WarningFilled /></el-icon>
    <div class="error-state__body">
      <div class="error-state__title">{{ heading }}</div>
      <div v-if="error" class="error-state__message">{{ error.message }}</div>
      <div v-if="error?.code" class="error-state__meta">
        <el-tag size="small" type="danger" effect="plain">{{ error.code }}</el-tag>
        <span v-if="error.status" class="fbt-muted">HTTP {{ error.status }}</span>
      </div>
      <div v-if="hint" class="error-state__hint">{{ hint }}</div>
      <el-collapse v-if="error?.detail" class="error-state__detail">
        <el-collapse-item title="技术细节" name="detail">
          <pre class="fbt-pre">{{ error.detail }}</pre>
        </el-collapse-item>
      </el-collapse>
      <div v-if="retryable || $slots.actions" class="error-state__actions">
        <el-button v-if="retryable" size="small" type="primary" :icon="Refresh" @click="emit('retry')">
          重试
        </el-button>
        <slot name="actions" />
      </div>
    </div>
  </div>
</template>

<style scoped>
.error-state {
  display: flex;
  gap: 12px;
  padding: 24px;
  border: 1px solid var(--el-color-danger-light-5);
  border-radius: 6px;
  background-color: var(--el-color-danger-light-9);
  color: var(--el-text-color-primary);
}

.error-state.is-compact {
  padding: 12px 14px;
}

.error-state__icon {
  color: var(--el-color-danger);
  font-size: 22px;
  flex: 0 0 auto;
  margin-top: 2px;
}

.error-state__body {
  min-width: 0;
  flex: 1 1 auto;
}

.error-state__title {
  font-size: 15px;
  font-weight: 600;
  margin-bottom: 4px;
}

.error-state__message {
  font-size: 13px;
  line-height: 1.6;
  word-break: break-word;
}

.error-state__meta {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
  font-size: 12px;
}

.error-state__hint {
  margin-top: 8px;
  font-size: 12px;
  line-height: 1.7;
  color: var(--el-text-color-secondary);
}

.error-state__detail {
  margin-top: 8px;
  --el-collapse-header-height: 32px;
}

.error-state__actions {
  margin-top: 12px;
  display: flex;
  gap: 8px;
}
</style>
