<script setup lang="ts">
/** 加载态与空态：与 ErrorState 配套，保证任何分支都有明确 UI。 */
import { DocumentRemove } from '@element-plus/icons-vue'

withDefaults(
  defineProps<{
    text?: string
    empty?: boolean
    description?: string
  }>(),
  { text: '加载中…', empty: false, description: '暂无数据' },
)
</script>

<template>
  <div v-if="!empty" class="fbt-state">
    <el-skeleton :rows="4" animated />
    <div class="fbt-state__text">{{ text }}</div>
  </div>
  <div v-else class="fbt-state fbt-state--empty">
    <el-icon class="fbt-state__icon"><DocumentRemove /></el-icon>
    <div class="fbt-state__text">{{ description }}</div>
    <slot />
  </div>
</template>

<style scoped>
.fbt-state {
  padding: 24px;
}

.fbt-state--empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  color: var(--el-text-color-secondary);
}

.fbt-state__icon {
  font-size: 32px;
  color: var(--el-text-color-placeholder);
}

.fbt-state__text {
  margin-top: 8px;
  font-size: 13px;
  color: var(--el-text-color-secondary);
}
</style>
