<script setup lang="ts">
/**
 * 快照类型徽章 + 帮助文案（对等清单：三种快照类型的帮助文案原样保留）。
 */
import { QuestionFilled } from '@element-plus/icons-vue'
import type { SnapshotType } from '@/api/types'
import { snapshotTypeHelp, snapshotTypeLabels } from '@/locales/zh-CN'

defineProps<{ type: SnapshotType; showHelp?: boolean }>()
</script>

<template>
  <span class="snapshot-type">
    <el-tag
      size="small"
      :type="type === 'Increment' ? 'info' : type === 'VirtualFull' ? 'warning' : 'primary'"
      effect="plain"
    >
      {{ snapshotTypeLabels[type] }}
    </el-tag>
    <el-tooltip v-if="showHelp" placement="top" :show-after="150" :hide-after="0">
      <template #content>
        <div class="snapshot-type__help">{{ snapshotTypeHelp[type] }}</div>
      </template>
      <el-icon class="snapshot-type__icon"><QuestionFilled /></el-icon>
    </el-tooltip>
  </span>
</template>

<style scoped>
.snapshot-type {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.snapshot-type__icon {
  color: var(--el-text-color-placeholder);
  cursor: help;
}

.snapshot-type__help {
  max-width: 380px;
  line-height: 1.7;
  white-space: pre-wrap;
}
</style>
