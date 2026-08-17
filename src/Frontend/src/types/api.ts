export interface MediaListItem {
  id: string
  mimeType: string
  thumbnailPath: string | null
  takenAt: string | null
  isFavorite: boolean
  isVideo: boolean
  originClass: string | null
  originConfidence: number | null
  address: string | null
  /** Cosine distance to the query vector — semantic search only, absent elsewhere. Lower is closer. */
  distance?: number | null
}

export interface TagDto {
  tag: string
  confidence: number
  model: string
}

/** A tag and how many photos carry it, within whatever chip selection is active. */
export interface TagCount {
  tag: string
  count: number
}

export interface MediaDetail {
  id: string
  filePath: string
  mimeType: string
  fileSize: number | null
  width: number | null
  height: number | null
  durationSec: number | null
  takenAt: string | null
  isFavorite: boolean
  originClass: string | null
  originConfidence: number | null
  cameraMake: string | null
  cameraModel: string | null
  latitude: number | null
  longitude: number | null
  address: string | null
  tags: TagDto[]
}

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface StatsDto {
  photos: number
  videos: number
  latestDate: string | null
  countries: number
}

export interface OtdGroupDto {
  year: number
  items: MediaListItem[]
}

export interface GeoPinDto {
  id: string
  lat: number
  lng: number
  address: string | null
}

export interface AlbumDto {
  id: string
  name: string
  description: string | null
  mediaCount: number
  createdAt: string
}

export interface AlbumDetailDto {
  id: string
  name: string
  description: string | null
  media: MediaListItem[]
  createdAt: string
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  expiresIn: number
}

export interface JobStatusCounts {
  queued: number
  processing: number
  enriching: number
  completed: number
  failed: number
  skippedDuplicate: number
}

export interface PipelineStatusDto {
  scanId: string | null
  startedAt: string | null
  counts: JobStatusCounts
  total: number
  completedLastHour: number
  staleCount: number
}

export interface JobDto {
  id: string
  filePath: string
  source: string
  status: string
  scanId: string | null
  mediaFileId: string | null
  startedAt: string
  completedAt: string | null
  failedStep: string | null
  errorMessage: string | null
  retryCount: number
}

/** Response of POST /api/admin/potd/send. `sent` is false for a dryRun, a skip, or a failure. */
export interface PotdSendResult {
  sent: boolean
  tier: string | null
  mediaFileId: string | null
  filePath: string | null
  caption: string | null
  skippedReason: string | null
}

/** A row of `potd_sends` — written only by the scheduler, never by the manual trigger. */
export interface PotdSendDto {
  sentOn: string
  mediaFileId: string
  sentAt: string
}
