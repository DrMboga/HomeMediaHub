import type { MediaListItem, PagedResult, TagCount } from '../types/api'
import client from './client'

/**
 * Tag vocabulary with photo counts. With `selected` empty this is the whole vocabulary;
 * with tags selected the counts are those remaining *alongside* that selection, so a tag
 * missing from the response is one that co-occurs with nothing selected — a dead end.
 */
export async function getTags(selected: string[] = []): Promise<TagCount[]> {
  const { data } = await client.get<TagCount[]>('/tags', {
    // Repeat the key per value (?selected=a&selected=b) — axios' default bracket
    // syntax would arrive as `selected[]` and bind to nothing on the .NET side.
    params: { selected },
    paramsSerializer: { indexes: null },
  })
  return data
}

/** Photos carrying *all* of `tags` (AND), newest first. */
export async function getTagMedia(
  tags: string[],
  page = 1,
  pageSize = 50
): Promise<PagedResult<MediaListItem>> {
  const { data } = await client.get<PagedResult<MediaListItem>>('/tags/media', {
    params: { tags, page, pageSize },
    paramsSerializer: { indexes: null },
  })
  return data
}
