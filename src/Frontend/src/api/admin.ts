import type { JobDto, PipelineStatusDto, PotdSendDto, PotdSendResult } from '../types/api'
import client from './client'

export async function triggerScan(path?: string): Promise<{ scanId: string }> {
  const { data } = await client.post<{ scanId: string }>('/admin/scan', path ? { path } : {})
  return data
}

export async function getPipelineStatus(): Promise<PipelineStatusDto> {
  const { data } = await client.get<PipelineStatusDto>('/admin/pipeline/status')
  return data
}

export async function getJobs(params: {
  status?: string
  page?: number
  pageSize?: number
}): Promise<JobDto[]> {
  const { data } = await client.get<JobDto[]>('/admin/jobs', { params })
  return data
}

export async function retryJob(id: string): Promise<void> {
  await client.post(`/admin/jobs/${id}/retry`)
}

export async function retryFailedJobs(): Promise<{ retried: number }> {
  const { data } = await client.post<{ retried: number }>('/admin/jobs/retry-failed')
  return data
}

/**
 * Runs the Photo of the Day pick-and-send once, now. Unlike the scheduler this ignores
 * `Potd:Enabled` and the already-sent-today guard, and never writes `potd_sends` — so a test
 * send at 11:00 cannot make the real 15:00 run skip the day.
 */
export async function sendPhotoOfTheDay(body: {
  month?: number
  day?: number
  mediaFileId?: string
  dryRun?: boolean
}): Promise<PotdSendResult> {
  const { data } = await client.post<PotdSendResult>('/admin/potd/send', body)
  return data
}

export async function getLastPotdSends(limit = 5): Promise<PotdSendDto[]> {
  const { data } = await client.get<PotdSendDto[]>('/admin/potd/last', { params: { limit } })
  return data
}
