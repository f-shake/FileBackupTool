<script setup lang="ts">
/**
 * 实时通道徽章：显示当前是「实时」（SignalR）还是「轮询」（降级）。
 * 07 §6：若 SignalR 完全不可用，自动降级为每 5 秒轮询，UI 上显示当前通道。
 */
import { computed } from 'vue'
import { Loading, Refresh, WarningFilled } from '@element-plus/icons-vue'
import { useRealtimeStore } from '@/stores/realtime'

const realtime = useRealtimeStore()

const type = computed(() => {
  switch (realtime.snapshot.state) {
    case 'realtime':
      return 'success' as const
    case 'polling':
      return 'warning' as const
    case 'connecting':
      return 'info' as const
    default:
      return 'info' as const
  }
})

const tooltip = computed(() => {
  const s = realtime.snapshot
  if (s.state === 'realtime') {
    return `SignalR 已连接（/hubs/events）${s.lastConnectedAt ? `\n最近连接：${s.lastConnectedAt}` : ''}`
  }
  if (s.state === 'polling') {
    return `SignalR 连续 ${realtime.maxFailures} 次重连失败，已降级为 REST 轮询。\n断线期间的事件已丢失，页面数据以轮询结果为准。\n${s.lastError ? `最后错误：${s.lastError}` : ''}`
  }
  if (s.state === 'connecting') {
    return `正在连接实时通道…（已失败 ${s.consecutiveFailures} 次，连续 ${realtime.maxFailures} 次失败后降级为轮询）`
  }
  return '实时通道未启动'
})
</script>

<template>
  <el-tooltip :content="tooltip" placement="bottom" :show-after="200">
    <div class="channel-badge">
      <el-tag :type="type" size="small" effect="dark" class="channel-badge__tag">
        <el-icon class="channel-badge__icon">
          <Loading v-if="realtime.snapshot.state === 'connecting'" />
          <WarningFilled v-else-if="realtime.snapshot.state === 'polling'" />
          <Refresh v-else />
        </el-icon>
        {{ realtime.stateLabel }}
      </el-tag>
      <el-button
        v-if="realtime.snapshot.state === 'polling'"
        size="small"
        text
        type="primary"
        @click="realtime.retry()"
      >
        恢复实时
      </el-button>
    </div>
  </el-tooltip>
</template>

<style scoped>
.channel-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.channel-badge__tag {
  display: inline-flex;
  align-items: center;
  gap: 4px;
}

.channel-badge__icon {
  margin-right: 2px;
}
</style>
