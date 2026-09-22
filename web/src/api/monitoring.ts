/**
 * 06 §4.5 作业与监控端点。
 */
import { get } from './http'
import type { JobQueueSnapshot, MonitoringSnapshot, QueuedJob } from './types'

/** `GET /api/jobs` —— 队列与运行中作业 */
export function listJobs(): Promise<JobQueueSnapshot> {
  return get<JobQueueSnapshot>('/jobs')
}

/** `GET /api/jobs/{jobId}` —— 作业详情与进度快照 */
export function getJob(jobId: string): Promise<QueuedJob> {
  return get<QueuedJob>(`/jobs/${encodeURIComponent(jobId)}`)
}

/** `GET /api/monitoring` —— 各根监控状态、待处理变更队列、最近触发记录 */
export function getMonitoring(): Promise<MonitoringSnapshot> {
  return get<MonitoringSnapshot>('/monitoring')
}
