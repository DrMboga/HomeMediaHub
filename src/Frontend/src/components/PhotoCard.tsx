import { Play } from 'lucide-react'
import type { MediaListItem } from '../types/api'
import { thumbnailUrl } from '../api/media'
import { useLightbox } from '../context/LightboxContext'

function formatDuration(sec: number): string {
  const m = Math.floor(sec / 60)
  const s = Math.floor(sec % 60)
  return `${m}:${s.toString().padStart(2, '0')}`
}

function formatCardDate(takenAt: string | null): string {
  if (!takenAt) return ''
  const d = new Date(takenAt)
  const datePart = d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
  // Midnight UTC exactly means the time came from a path-year fallback, not real
  // EXIF — don't show a misleading 00:00 (mirrors the Lightbox rule).
  const isMidnightUtc = d.getUTCHours() === 0 && d.getUTCMinutes() === 0 &&
    d.getUTCSeconds() === 0 && d.getUTCMilliseconds() === 0
  if (isMidnightUtc) return datePart
  const hh = String(d.getHours()).padStart(2, '0')
  const mm = String(d.getMinutes()).padStart(2, '0')
  return `${datePart} · ${hh}:${mm}`
}

interface Props {
  item: MediaListItem
  className?: string
  durationSec?: number | null
}

export function PhotoCard({ item, className = 'gallery-card', durationSec }: Props) {
  const { open } = useLightbox()

  return (
    <div className={`photo-card ${className}`} onClick={() => open(item.id)}>
      <img src={thumbnailUrl(item.id)} alt="" loading="lazy" />
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
      <div className="photo-card-date">{formatCardDate(item.takenAt)}</div>
    </div>
  )
}
