import { Play } from 'lucide-react'
import type { MediaListItem } from '../types/api'
import { thumbnailUrl } from '../api/media'
import { useLightbox, useLightboxList } from '../context/LightboxContext'
import { formatGridDate } from '../utils/mediaDate'

function formatDuration(sec: number): string {
  const m = Math.floor(sec / 60)
  const s = Math.floor(sec % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

interface Props {
  item: MediaListItem
  className?: string
  durationSec?: number | null
}

export function PhotoCard({ item, className = 'gallery-card', durationSec }: Props) {
  const { open } = useLightbox()
  const listIds = useLightboxList()

  return (
    <div className={`photo-card ${className}`} onClick={() => open(item.id, listIds)}>
      {/* loading="lazy" only works because every card class is a fixed-size box — the layout
          is stable before any image loads, so the browser fetches just what is on screen.
          On This Day renders up to ~2,250 of these on one page. Do not make them intrinsic. */}
      <img src={thumbnailUrl(item.id)} alt="" loading="lazy" decoding="async" />
      <div className="photo-card-grad" />
      {(item.isVideo || durationSec != null) && (
        <div className="video-badge">
          <Play fill="currentColor" strokeWidth={0} />
          {durationSec != null ? formatDuration(durationSec) : ''}
        </div>
      )}
      {item.distance != null && (
        <span className="distance-badge" title="Cosine distance to the query — lower is closer">
          {item.distance.toFixed(3)}
        </span>
      )}
      {item.originClass && item.originClass !== 'photo' && (
        <span className={`origin-badge origin-badge--${item.originClass}`}>
          {item.originClass}
        </span>
      )}
      <div className="photo-card-date">{formatGridDate(item.takenAt)}</div>
    </div>
  )
}
