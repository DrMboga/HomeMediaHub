import type {
  AlbumDetailDto,
  AlbumDto,
  GeoPinDto,
  MediaDetail,
  MediaListItem,
  OtdGroupDto,
  PagedResult,
  StatsDto,
} from '../types/api'
import client from './client'

export function thumbnailUrl(id: string) {
  return `/api/media/${id}/thumbnail`
}

export function originalUrl(id: string) {
  return `/api/media/${id}/original`
}

export function streamUrl(id: string) {
  return `/api/media/${id}/stream`
}

export async function getMediaList(params: {
  page?: number
  pageSize?: number
  from?: string
  to?: string
  tag?: string
  favorite?: boolean
  isVideo?: boolean
  noDate?: boolean
}): Promise<PagedResult<MediaListItem>> {
  const { data } = await client.get<PagedResult<MediaListItem>>('/media', { params })
  return data
}

export async function getMediaDetail(id: string): Promise<MediaDetail> {
  const { data } = await client.get<MediaDetail>(`/media/${id}`)
  return data
}

export async function getStats(): Promise<StatsDto> {
  const { data } = await client.get<StatsDto>('/media/stats')
  return data
}

export async function getOnThisDay(month: number, day: number): Promise<OtdGroupDto[]> {
  const { data } = await client.get<OtdGroupDto[]>('/media/on-this-day', {
    params: { month, day },
  })
  return data
}

export async function getGeoPins(): Promise<GeoPinDto[]> {
  const { data } = await client.get<GeoPinDto[]>('/media/geo')
  return data
}

export async function getAlbums(): Promise<AlbumDto[]> {
  const { data } = await client.get<AlbumDto[]>('/albums')
  return data
}

export async function getAlbumDetail(id: string): Promise<AlbumDetailDto> {
  const { data } = await client.get<AlbumDetailDto>(`/albums/${id}`)
  return data
}

export async function patchMedia(id: string, body: { isFavorite?: boolean }): Promise<void> {
  await client.patch(`/media/${id}`, body)
}
