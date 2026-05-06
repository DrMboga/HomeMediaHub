import { useEffect, useRef, useState } from 'react'
import ReactDOM from 'react-dom'
import { MapContainer, TileLayer, CircleMarker, useMap } from 'react-leaflet'
import { useQuery } from '@tanstack/react-query'
import { getMediaDetail, originalUrl, streamUrl } from '../api/media'
import { useLightbox } from '../context/LightboxContext'

function formatDate(iso: string | null): string {
  if (!iso) return '—'
  return new Date(iso).toLocaleDateString('en-US', {
    weekday: 'short', year: 'numeric', month: 'short', day: 'numeric',
  }) + ' · ' + new Date(iso).toLocaleTimeString('en-US', { hour: '2-digit', minute: '2-digit' })
}

function Resizer() {
  const map = useMap()
  useEffect(() => { setTimeout(() => map.invalidateSize(), 80) }, [map])
  return null
}

export function Lightbox() {
  const { mediaId, close } = useLightbox()
  const [zoom, setZoom] = useState(1)
  const [tab, setTab] = useState<'info' | 'map'>('info')
  const videoRef = useRef<HTMLVideoElement>(null)

  const { data: detail } = useQuery({
    queryKey: ['media', mediaId],
    queryFn: () => getMediaDetail(mediaId!),
    enabled: !!mediaId,
    staleTime: Infinity,
  })

  // Body scroll lock
  useEffect(() => {
    if (mediaId) {
      document.body.classList.add('lb-open')
      setZoom(1)
      setTab('info')
    } else {
      document.body.classList.remove('lb-open')
    }
    return () => document.body.classList.remove('lb-open')
  }, [mediaId])

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (!mediaId) return
      if (e.key === 'Escape') close()
      if (e.key === '+') setZoom(z => Math.min(4, z + 0.25))
      if (e.key === '-') setZoom(z => Math.max(0.5, z - 0.25))
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [mediaId, close])

  // Stop video on close
  useEffect(() => {
    if (!mediaId && videoRef.current) {
      videoRef.current.pause()
      videoRef.current.src = ''
    }
  }, [mediaId])

  if (!mediaId) return null

  const handleBackdrop = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget) close()
  }

  const handleWheel = (e: React.WheelEvent) => {
    e.preventDefault()
    setZoom(z => Math.max(0.5, Math.min(4, z + (e.deltaY < 0 ? 0.1 : -0.1))))
  }

  const isVideo = detail?.durationSec != null
  const camera = [detail?.cameraMake, detail?.cameraModel].filter(Boolean).join(' ')
  const hasGps = !!(detail?.latitude && detail?.longitude)

  return ReactDOM.createPortal(
    <div className="lb open" onClick={handleBackdrop}>
      <div className="lb-panel">

        {/* Photo / Video / Map area */}
        <div className="lb-photo" onWheel={handleWheel}>
          {tab === 'map' && hasGps ? (
            <div className="lb-map-area">
              <MapContainer
                center={[detail!.latitude!, detail!.longitude!]}
                zoom={13}
                style={{ height: '100%', width: '100%' }}
              >
                <TileLayer
                  url="https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png"
                  attribution="© OpenStreetMap © CARTO"
                  maxZoom={19}
                />
                <CircleMarker
                  center={[detail!.latitude!, detail!.longitude!]}
                  radius={10}
                  pathOptions={{ fillColor: '#90caf9', color: 'rgba(144,202,249,.3)', weight: 8, fillOpacity: 1 }}
                />
                <Resizer />
              </MapContainer>
            </div>
          ) : isVideo ? (
            <video
              ref={videoRef}
              className="lb-video"
              src={streamUrl(mediaId)}
              controls
              autoPlay
            />
          ) : (
            <img
              className="lb-img"
              src={originalUrl(mediaId)}
              alt=""
              style={{ transform: `scale(${zoom})` }}
            />
          )}

          <button className="lb-close" onClick={close}>
            <span className="mi">close</span>
          </button>
        </div>

        {/* Info bar */}
        <div className="lb-info">
          <div className="lb-meta">
            <span className="lb-meta-date">{formatDate(detail?.takenAt ?? null)}</span>
            {camera && (
              <>
                <span className="lb-sep">·</span>
                <span className="lb-meta-item">
                  <span className="mi">photo_camera</span>{camera}
                </span>
              </>
            )}
            {detail?.address && (
              <>
                <span className="lb-sep">·</span>
                <span className="lb-meta-item">
                  <span className="mi">location_on</span>{detail.address}
                </span>
              </>
            )}
          </div>

          {detail?.tags && detail.tags.length > 0 && (
            <div className="lb-tags">
              {detail.tags.map(t => (
                <span key={t.tag} className="lb-tag">{t.tag}</span>
              ))}
            </div>
          )}

          <div className="lb-right">
            {!isVideo && (
              <>
                <button className="ib" onClick={() => setZoom(z => Math.max(0.5, z - 0.15))}>
                  <span className="mi">remove</span>
                </button>
                <span className="lb-zoom-pct">{Math.round(zoom * 100)}%</span>
                <button className="ib" onClick={() => setZoom(z => Math.min(4, z + 0.15))}>
                  <span className="mi">add</span>
                </button>
                <button className="ib" onClick={() => setZoom(1)} title="Reset zoom">
                  <span className="mi">center_focus_weak</span>
                </button>
                <div style={{ width: 8 }} />
              </>
            )}
            <div style={{ display: 'flex', gap: 4 }}>
              <button
                className={`lb-tab-btn${tab === 'info' ? ' active' : ''}`}
                onClick={() => setTab('info')}
              >Info</button>
              {hasGps && (
                <button
                  className={`lb-tab-btn${tab === 'map' ? ' active' : ''}`}
                  onClick={() => setTab('map')}
                >Map</button>
              )}
            </div>
          </div>
        </div>

      </div>
    </div>,
    document.body
  )
}
