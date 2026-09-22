/**
 * 实时通道客户端（07 §6）。
 *
 * 契约：
 *   - **SignalR 为主通道**，连接 `/hubs/events`，订阅 `task:{taskId}` 分组。
 *   - 连续 **3** 次重连失败 → 切换为 **REST 轮询**（每 5 秒），UI 显示「轮询」。
 *   - 断线重连成功后，**必须**用 REST 拉一次全量状态——断线期间的事件已经丢了，
 *     不能用增量事件补齐（07 §6 原话）。
 *   - 提供手动重试，允许从轮询模式恢复回实时。
 */
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr'

import type {
  BackupProgressEvent,
  JobAbortedEvent,
  JobCompletedEvent,
  JobFailedEvent,
  JobQueuedEvent,
  JobStartedEvent,
  LogAppendedEvent,
  MonitorChangedEvent,
  RealtimeChannel,
  RestoreProgressEvent,
  SnapshotCommittedEvent,
} from '@/api/types'

/** 连续失败多少次后降级为轮询（08 §4.1 默认值，可否决） */
export const MAX_CONSECUTIVE_FAILURES = 3

/** 轮询间隔（毫秒） */
const POLL_INTERVAL_MS = Number(import.meta.env.VITE_POLL_INTERVAL_MS || 5000)

const HUB_PATH = import.meta.env.VITE_HUB_PATH || '/hubs/events'

/** 服务端订阅方法名。06 只说了「订阅 task:{taskId} 分组」，未约定方法名——集中在此处便于对齐。 */
const METHOD_SUBSCRIBE_TASK = 'SubscribeTask'
const METHOD_UNSUBSCRIBE_TASK = 'UnsubscribeTask'

export type ChannelState = 'idle' | 'connecting' | 'realtime' | 'polling'

export interface ChannelSnapshot {
  state: ChannelState
  /** UI 上显示的通道：`实时` / `轮询` */
  channel: RealtimeChannel
  consecutiveFailures: number
  lastError: string | null
  lastConnectedAt: string | null
  /** 轮询模式下最近一次轮询时间 */
  lastPollAt: string | null
}

/** 事件名 → 负载类型。与 06 §5 的表格一一对应。 */
export interface RealtimeEvents {
  'job.queued': JobQueuedEvent
  'job.started': JobStartedEvent
  'backup.progress': BackupProgressEvent
  'restore.progress': RestoreProgressEvent
  'job.completed': JobCompletedEvent
  'job.failed': JobFailedEvent
  'job.aborted': JobAbortedEvent
  'log.appended': LogAppendedEvent
  'monitor.changed': MonitorChangedEvent
  'snapshot.committed': SnapshotCommittedEvent
}

export type RealtimeEventName = keyof RealtimeEvents

/** 非 SignalR 的内部信号：要求订阅者用 REST 重新拉取（重连后 / 每次轮询）。 */
export type ResyncListener = (reason: 'connected' | 'reconnected' | 'polling') => void

type Handler<K extends RealtimeEventName> = (payload: RealtimeEvents[K]) => void

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

export class RealtimeClient {
  private connection: HubConnection | null = null
  private handlers = new Map<string, Set<(payload: unknown) => void>>()
  private resyncListeners = new Set<ResyncListener>()
  private stateListeners = new Set<(snapshot: ChannelSnapshot) => void>()

  private state: ChannelState = 'idle'
  private consecutiveFailures = 0
  private lastError: string | null = null
  private lastConnectedAt: string | null = null
  private lastPollAt: string | null = null

  private disposed = false
  private everConnected = false
  private subscribedTasks = new Set<string>()
  private pollTimer: ReturnType<typeof setInterval> | null = null

  /* ------------------------------ 生命周期 ------------------------------ */

  /** 启动。失败会内部计数并最终降级，不会抛出。 */
  async start(): Promise<void> {
    this.disposed = false
    this.connection = this.buildConnection()
    this.registerServerHandlers(this.connection)
    await this.connectLoop()
  }

  /** 停止并释放。 */
  async stop(): Promise<void> {
    this.disposed = true
    this.stopPolling()
    const conn = this.connection
    this.connection = null
    if (conn) {
      try {
        await conn.stop()
      } catch {
        /* 忽略：停止失败无补救手段 */
      }
    }
    this.setState('idle')
  }

  /** 手动重试：从轮询模式尝试恢复到实时通道。 */
  async retryNow(): Promise<void> {
    if (this.disposed) return
    this.stopPolling()
    this.consecutiveFailures = 0
    if (!this.connection) {
      this.connection = this.buildConnection()
      this.registerServerHandlers(this.connection)
    } else if (this.connection.state !== HubConnectionState.Disconnected) {
      try {
        await this.connection.stop()
      } catch {
        /* 忽略 */
      }
    }
    await this.connectLoop()
  }

  /* ------------------------------ 订阅接口 ------------------------------ */

  on<K extends RealtimeEventName>(event: K, handler: Handler<K>): () => void {
    const key = event as string
    let set = this.handlers.get(key)
    if (!set) {
      set = new Set()
      this.handlers.set(key, set)
    }
    set.add(handler as (payload: unknown) => void)
    return () => {
      set?.delete(handler as (payload: unknown) => void)
    }
  }

  /** 订阅「需要重新拉取全量状态」的信号。 */
  onResync(listener: ResyncListener): () => void {
    this.resyncListeners.add(listener)
    return () => this.resyncListeners.delete(listener)
  }

  onStateChange(listener: (snapshot: ChannelSnapshot) => void): () => void {
    this.stateListeners.add(listener)
    listener(this.snapshot())
    return () => this.stateListeners.delete(listener)
  }

  snapshot(): ChannelSnapshot {
    return {
      state: this.state,
      channel: this.state === 'realtime' ? 'realtime' : 'polling',
      consecutiveFailures: this.consecutiveFailures,
      lastError: this.lastError,
      lastConnectedAt: this.lastConnectedAt,
      lastPollAt: this.lastPollAt,
    }
  }

  /** 订阅某个任务的分组（只想收到当前查看任务的进展）。 */
  async subscribeTask(taskId: string): Promise<void> {
    this.subscribedTasks.add(taskId)
    await this.invokeSubscribe(taskId)
  }

  async unsubscribeTask(taskId: string): Promise<void> {
    this.subscribedTasks.delete(taskId)
    const conn = this.connection
    if (conn && conn.state === HubConnectionState.Connected) {
      try {
        await conn.invoke(METHOD_UNSUBSCRIBE_TASK, taskId)
      } catch {
        /* 忽略：断线时会话自然失效 */
      }
    }
  }

  /* ------------------------------ 内部实现 ------------------------------ */

  private buildConnection(): HubConnection {
    return new HubConnectionBuilder()
      .withUrl(HUB_PATH, { withCredentials: true })
      .withKeepAliveInterval(15_000)
      .configureLogging(LogLevel.Warning)
      .build()
  }

  private registerServerHandlers(conn: HubConnection): void {
    for (const name of Object.keys(EVENT_NAMES) as RealtimeEventName[]) {
      conn.on(name, (payload: unknown) => {
        const set = this.handlers.get(name)
        if (!set) return
        for (const handler of set) {
          try {
            handler(payload)
          } catch (err) {
            console.error(`[realtime] 事件 ${name} 的处理函数抛出异常`, err)
          }
        }
      })
    }

    conn.onclose(() => {
      if (this.disposed) return
      this.lastError = '连接已断开'
      void this.connectLoop()
    })
    conn.onreconnecting(() => {
      if (this.disposed) return
      this.setState('connecting')
    })
  }

  /**
   * 带失败计数的连接循环。
   * 连续 `MAX_CONSECUTIVE_FAILURES` 次失败即降级为轮询（07 §6）。
   */
  private async connectLoop(): Promise<void> {
    while (!this.disposed) {
      const conn = this.connection
      if (!conn) return

      if (conn.state === HubConnectionState.Connected) {
        this.consecutiveFailures = 0
        this.setState('realtime')
        this.lastConnectedAt = new Date().toISOString()
        this.emitResync(this.everConnected ? 'reconnected' : 'connected')
        this.everConnected = true
        await this.resubscribeAll()
        return
      }

      this.setState('connecting')
      try {
        await conn.start()
        this.consecutiveFailures = 0
        this.lastError = null
        this.setState('realtime')
        this.lastConnectedAt = new Date().toISOString()
        this.emitResync(this.everConnected ? 'reconnected' : 'connected')
        this.everConnected = true
        await this.resubscribeAll()
        return
      } catch (err) {
        this.consecutiveFailures += 1
        this.lastError = err instanceof Error ? err.message : String(err)

        if (this.consecutiveFailures >= MAX_CONSECUTIVE_FAILURES) {
          // 降级：SignalR 完全不可用（代理配置错误、WebSocket 被阻断）
          this.startPolling()
          return
        }
        // 退避后重试：1s → 2s
        await delay(1000 * this.consecutiveFailures)
      }
    }
  }

  private async resubscribeAll(): Promise<void> {
    for (const taskId of this.subscribedTasks) {
      await this.invokeSubscribe(taskId)
    }
  }

  private async invokeSubscribe(taskId: string): Promise<void> {
    const conn = this.connection
    if (!conn || conn.state !== HubConnectionState.Connected) return
    try {
      await conn.invoke(METHOD_SUBSCRIBE_TASK, taskId)
    } catch (err) {
      console.warn('[realtime] 订阅任务分组失败', taskId, err)
    }
  }

  private startPolling(): void {
    if (this.pollTimer) return
    this.setState('polling')
    this.lastPollAt = new Date().toISOString()
    this.emitResync('polling')
    this.pollTimer = setInterval(() => {
      if (this.disposed) return
      this.lastPollAt = new Date().toISOString()
      this.notifyState()
      this.emitResync('polling')
    }, POLL_INTERVAL_MS)
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer)
      this.pollTimer = null
    }
  }

  private emitResync(reason: 'connected' | 'reconnected' | 'polling'): void {
    for (const listener of this.resyncListeners) {
      try {
        listener(reason)
      } catch (err) {
        console.error('[realtime] resync 处理函数抛出异常', err)
      }
    }
  }

  private setState(state: ChannelState): void {
    if (this.state === state) {
      this.notifyState()
      return
    }
    this.state = state
    this.notifyState()
  }

  private notifyState(): void {
    const snap = this.snapshot()
    for (const listener of this.stateListeners) listener(snap)
  }
}

/** 仅用于遍历事件名（保证与 RealtimeEvents 的键一致）。 */
const EVENT_NAMES: Record<RealtimeEventName, true> = {
  'job.queued': true,
  'job.started': true,
  'backup.progress': true,
  'restore.progress': true,
  'job.completed': true,
  'job.failed': true,
  'job.aborted': true,
  'log.appended': true,
  'monitor.changed': true,
  'snapshot.committed': true,
}

/** 全局单例：整个应用共享一条连接。 */
export const realtime = new RealtimeClient()
