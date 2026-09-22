/**
 * 实时通道状态（07 §6）。
 * 只做「通道状态 + 事件分发 + 断线后重载信号」三件事，不持有业务数据。
 */
import { defineStore } from 'pinia'
import { computed, onScopeDispose, ref } from 'vue'
import type { ChannelSnapshot } from '@/realtime/client'
import { MAX_CONSECUTIVE_FAILURES, realtime } from '@/realtime/client'
import type { RealtimeEventName, RealtimeEvents } from '@/realtime/client'

export const useRealtimeStore = defineStore('realtime', () => {
  const snapshot = ref<ChannelSnapshot>(realtime.snapshot())
  const started = ref(false)
  /** 每次收到重载信号递增，页面 watch 它即可重新拉数据 */
  const resyncTick = ref(0)
  const lastResyncReason = ref<'connected' | 'reconnected' | 'polling' | null>(null)

  const channel = computed(() => snapshot.value.channel)
  const channelLabel = computed(() => (snapshot.value.channel === 'realtime' ? '实时' : '轮询'))
  const stateLabel = computed(() => {
    switch (snapshot.value.state) {
      case 'realtime':
        return '实时'
      case 'polling':
        return '轮询'
      case 'connecting':
        return '连接中'
      default:
        return '未连接'
    }
  })
  const degraded = computed(() => snapshot.value.state === 'polling')

  let unsubscribeState: (() => void) | null = null
  let unsubscribeResync: (() => void) | null = null

  async function start(): Promise<void> {
    if (started.value) return
    started.value = true
    unsubscribeState = realtime.onStateChange((s) => {
      snapshot.value = s
    })
    unsubscribeResync = realtime.onResync((reason) => {
      lastResyncReason.value = reason
      resyncTick.value += 1
    })
    await realtime.start()
  }

  async function stop(): Promise<void> {
    unsubscribeState?.()
    unsubscribeResync?.()
    unsubscribeState = null
    unsubscribeResync = null
    started.value = false
    await realtime.stop()
  }

  /** 手动重试：从轮询恢复回实时 */
  async function retry(): Promise<void> {
    await realtime.retryNow()
  }

  function on<K extends RealtimeEventName>(event: K, handler: (payload: RealtimeEvents[K]) => void): void {
    const off = realtime.on(event, handler)
    onScopeDispose(off, true)
  }

  async function subscribeTask(taskId: string): Promise<void> {
    await realtime.subscribeTask(taskId)
  }

  async function unsubscribeTask(taskId: string): Promise<void> {
    await realtime.unsubscribeTask(taskId)
  }

  return {
    snapshot,
    started,
    resyncTick,
    lastResyncReason,
    channel,
    channelLabel,
    stateLabel,
    degraded,
    maxFailures: MAX_CONSECUTIVE_FAILURES,
    start,
    stop,
    retry,
    on,
    subscribeTask,
    unsubscribeTask,
  }
})
