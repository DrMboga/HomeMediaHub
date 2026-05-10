import type { MediaListItem } from '../types/api'
import client from './client'

export async function semanticSearch(
  q: string,
  from?: string,
  to?: string,
  limit = 50
): Promise<MediaListItem[]> {
  const { data } = await client.get<MediaListItem[]>('/search/semantic', {
    params: { q, limit, ...(from && { from }), ...(to && { to }) },
  })
  return data
}

export async function tagSearch(
  q: string,
  limit = 100
): Promise<MediaListItem[]> {
  const { data } = await client.get<MediaListItem[]>('/search/tags', {
    params: { q, limit },
  })
  return data
}
