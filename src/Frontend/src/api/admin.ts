import type { JobDto, PipelineStatusDto } from '../types/api'
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
