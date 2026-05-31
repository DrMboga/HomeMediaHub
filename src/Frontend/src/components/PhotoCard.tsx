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
  return d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
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
          <span className="mi">play_arrow</span>
          {durationSec != null ? formatDuration(durationSec) : ''}
        </div>
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
