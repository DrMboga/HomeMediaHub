import type { MediaListItem } from '../types/api'
import client from './client'

export async function semanticSearch(
  q: string,
  from?: string,
  to?: string,
  limit = 15,
  originClass?: string
): Promise<MediaListItem[]> {
  const { data } = await client.get<MediaListItem[]>('/search/semantic', {
    params: { q, limit, ...(from && { from }), ...(to && { to }), ...(originClass && { originClass }) },
  })
  return data
}

// tagSearch() removed in Phase 10 — browse-by-tag lives in api/tags.ts, against
// /api/tags, which pages and narrows facets. The old /api/search/tags is gone.
