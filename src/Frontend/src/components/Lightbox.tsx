import { useEffect, useRef, useState } from 'react'
import ReactDOM from 'react-dom'
import { MapContainer, TileLayer, CircleMarker, useMap } from 'react-leaflet'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { X, Minus, Plus, Crosshair, Trash2, Check, Download, ChevronLeft, ChevronRight } from 'lucide-react'
import { getMediaDetail, displayUrl, streamUrl, thumbnailUrl, softDeleteMedia, originalUrl } from '../api/media'
import { useLightbox } from '../context/LightboxContext'
import { formatDetailDate } from '../utils/mediaDate'
import { MAP_TILE_URL, MAP_TILE_ATTRIBUTION, MAP_TILE_CLASS } from '../utils/mapTiles'

function Resizer() {
  const map = useMap()
  useEffect(() => { setTimeout(() => map.invalidateSize(), 80) }, [map])
  return null
}

export function Lightbox() {
  const { mediaId, close, prev, next, hasPrev, hasNext } = useLightbox()
  const queryClient = useQueryClient()
  const [zoom, setZoom] = useState(1)
  const [tab, setTab] = useState<'info' | 'map'>('info')
  const [imgFailed, setImgFailed] = useState(false)
  const [deleteConfirm, setDeleteConfirm] = useState(false)
  // A video that has never been streamed is transcoded on demand. An H.264 .MOV remuxes in
  // seconds, but a real re-encode outlives the element's patience — it used to fail once and
  // sit on a grey box forever, even though the server finished and cached moments later
  // (reopening the lightbox then played it instantly). Retry until the cache is warm.
  const [videoAttempt, setVideoAttempt] = useState(0)
  const [videoWaiting, setVideoWaiting] = useState(false)
  const [videoGaveUp, setVideoGaveUp] = useState(false)
  const retryTimer = useRef<number | undefined>(undefined)
  const videoRef = useRef<HTMLVideoElement>(null)
  const photoAreaRef = useRef<HTMLDivElement>(null)

  const { data: detail } = useQuery({
    queryKey: ['media', mediaId],
    queryFn: () => getMediaDetail(mediaId!),
    enabled: !!mediaId,
    staleTime: Infinity,
  })

  const { mutate: doDelete } = useMutation({
    mutationFn: () => softDeleteMedia(mediaId!),
    onSuccess: () => {
      close()
      queryClient.invalidateQueries({ queryKey: ['gallery'] })
      queryClient.invalidateQueries({ queryKey: ['search'] })
      queryClient.invalidateQueries({ queryKey: ['otd'] })
    },
  })

  // Body scroll lock + reset per-photo state
  useEffect(() => {
    if (mediaId) {
      document.body.classList.add('lb-open')
      setZoom(1)
      setTab('info')
      setImgFailed(false)
      setDeleteConfirm(false)
      setVideoAttempt(0)
      setVideoWaiting(false)
      setVideoGaveUp(false)
    } else {
      document.body.classList.remove('lb-open')
    }
    return () => document.body.classList.remove('lb-open')
  }, [mediaId])

  // Drop any pending retry when the lightbox closes or moves to another item.
  useEffect(() => () => window.clearTimeout(retryTimer.current), [mediaId])

  // Poll roughly as long as the server will keep working on a first-time transcode, then hand
  // over to a manual retry rather than hammering it forever.
  const VIDEO_RETRY_MS = 10_000
  const VIDEO_MAX_ATTEMPTS = 30

  function scheduleVideoRetry() {
    setVideoWaiting(true)
    retryTimer.current = window.setTimeout(() => setVideoAttempt(a => a + 1), VIDEO_RETRY_MS)
  }

  function handleVideoError() {
    if (videoAttempt >= VIDEO_MAX_ATTEMPTS) {
      setVideoWaiting(false)
      setVideoGaveUp(true)
      return
    }
    scheduleVideoRetry()
  }

  function retryVideoNow() {
    setVideoGaveUp(false)
    setVideoAttempt(0)
  }

  // Keyboard shortcuts
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if (!mediaId) return
      if (e.key === 'Escape') close()
      if (e.key === '+') setZoom(z => Math.min(4, z + 0.25))
      if (e.key === '-') setZoom(z => Math.max(0.5, z - 0.25))
      // A focused <video> uses the arrows to seek and a focused map uses them to pan — leave
      // them to the player / map there; the on-screen buttons still step.
      if (e.target instanceof HTMLVideoElement) return
      if (e.target instanceof Element && e.target.closest('.leaflet-container')) return
      if (e.key === 'ArrowLeft') prev()
      if (e.key === 'ArrowRight') next()
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [mediaId, close, prev, next])

  // Stop video on close
  useEffect(() => {
    if (!mediaId && videoRef.current) {
      videoRef.current.pause()
      videoRef.current.src = ''
    }
  }, [mediaId])

  // Non-passive wheel listener for zoom (passive:false required to call preventDefault)
  useEffect(() => {
    const el = photoAreaRef.current
    if (!el) return
    const onWheel = (e: WheelEvent) => {
      if (!mediaId) return
      e.preventDefault()
      setZoom(z => Math.max(0.5, Math.min(4, z + (e.deltaY < 0 ? 0.1 : -0.1))))
    }
    el.addEventListener('wheel', onWheel, { passive: false })
    return () => el.removeEventListener('wheel', onWheel)
  }, [mediaId])

  if (!mediaId) return null

  const handleBackdrop = (e: React.MouseEvent) => {
    if (e.target === e.currentTarget) close()
  }

  const isVideo = detail?.durationSec != null
  const imgSrc = imgFailed ? thumbnailUrl(mediaId) : displayUrl(mediaId)
  const camera = [detail?.cameraMake, detail?.cameraModel].filter(Boolean).join(' ')
  const dims = detail?.width && detail?.height ? `${detail.width}×${detail.height}` : ''
  const hasGps = !!(detail?.latitude && detail?.longitude)

  return ReactDOM.createPortal(
    <div className="lb open" onClick={handleBackdrop}>
      <div className="lb-panel">

        {/* Photo / Video / Map area */}
        <div className="lb-photo" ref={photoAreaRef}>
          {tab === 'map' && hasGps ? (
            <div className="lb-map-area">
              <MapContainer
                center={[detail!.latitude!, detail!.longitude!]}
                zoom={13}
                style={{ height: '100%', width: '100%' }}
              >
                <TileLayer
                  url={MAP_TILE_URL}
                  attribution={MAP_TILE_ATTRIBUTION}
                  className={MAP_TILE_CLASS}
                  maxZoom={19}
                />
                <CircleMarker
                  center={[detail!.latitude!, detail!.longitude!]}
                  radius={10}
                  pathOptions={{ fillColor: '#D98E2B', color: 'rgba(217,142,43,.3)', weight: 8, fillOpacity: 1 }}
                />
                <Resizer />
              </MapContainer>
            </div>
          ) : isVideo ? (
            <>
              {/* key forces a remount so the element re-requests; the failed response is not
                  cached, so the same URL is fetched again. */}
              <video
                key={`${mediaId}-${videoAttempt}`}
                ref={videoRef}
                className="lb-video"
                src={streamUrl(mediaId)}
                controls
                autoPlay
                onError={handleVideoError}
                onLoadedData={() => { setVideoWaiting(false); setVideoGaveUp(false) }}
              />
              {(videoWaiting || videoGaveUp) && (
                <div className="lb-video-status">
                  {videoWaiting ? (
                    <>Preparing video… the first play of a large file can take a few minutes.</>
                  ) : (
                    <>
                      Video is still not ready.{' '}
                      <button className="lb-retry-btn" onClick={retryVideoNow}>Retry</button>
                    </>
                  )}
                </div>
              )}
            </>
          ) : (
            <img
              className="lb-img"
              src={imgSrc}
              alt=""
              style={{ transform: `scale(${zoom})` }}
              onError={() => setImgFailed(true)}
            />
          )}

          <button className="lb-close" onClick={close} aria-label="Close">
            <X size={16} />
          </button>

          {/* Hidden at the ends of the list — no wrap-around. */}
          {hasPrev && (
            <button className="lb-nav lb-nav-prev" onClick={prev} aria-label="Previous photo">
              <ChevronLeft size={20} />
            </button>
          )}
          {hasNext && (
            <button className="lb-nav lb-nav-next" onClick={next} aria-label="Next photo">
              <ChevronRight size={20} />
            </button>
          )}
        </div>

        {/* Info bar — contact-sheet edge markings */}
        <div className="lb-info">
          <div className="lb-info-row">
            <div className="lb-meta">
              <span className="lb-meta-date">{formatDetailDate(detail?.takenAt ?? null)}</span>
              {camera && (<><span className="lb-dot">·</span><span>{camera}</span></>)}
              {dims && (<><span className="lb-dot">·</span><span>{dims}</span></>)}
              {detail?.address && (<><span className="lb-dot">·</span><span className="lb-place">{detail.address}</span></>)}
            </div>

            <div className="lb-controls">
              {!isVideo && (
                <div className="lb-zoom">
                  <button className="lb-icon-btn" onClick={() => setZoom(z => Math.max(0.5, z - 0.15))} aria-label="Zoom out"><Minus size={13} /></button>
                  <span className="lb-zoom-pct">{Math.round(zoom * 100)}%</span>
                  <button className="lb-icon-btn" onClick={() => setZoom(z => Math.min(4, z + 0.15))} aria-label="Zoom in"><Plus size={13} /></button>
                  <button className="lb-icon-btn" onClick={() => setZoom(1)} title="Reset zoom"><Crosshair size={13} /></button>
                </div>
              )}

              <div className="lb-tabs">
                <button
                  className={`lb-tab-btn${tab === 'info' ? ' active' : ''}`}
                  onClick={() => setTab('info')}
                >INFO</button>
                {hasGps && (
                  <button
                    className={`lb-tab-btn${tab === 'map' ? ' active' : ''}`}
                    onClick={() => setTab('map')}
                  >MAP</button>
                )}
              </div>

              {/* Native anchor, not a fetch — /original is public by design, so the browser
                  streams it straight to disk with no JS and no memory copy of a 155 MB video. */}
              {mediaId && (
                <a
                  className="lb-icon-btn"
                  href={originalUrl(mediaId, true)}
                  download
                  title="Download original"
                  onClick={e => e.stopPropagation()}
                ><Download size={14} /></a>
              )}

              {deleteConfirm ? (
                <div className="lb-del-row">
                  <span className="lb-del-prompt">Delete?</span>
                  <button className="lb-icon-btn lb-del-confirm" onClick={() => doDelete()} title="Confirm delete"><Check size={14} /></button>
                  <button className="lb-icon-btn" onClick={() => setDeleteConfirm(false)} title="Cancel"><X size={14} /></button>
                </div>
              ) : (
                <button className="lb-icon-btn lb-del-btn" onClick={() => setDeleteConfirm(true)} title="Delete (soft)"><Trash2 size={14} /></button>
              )}
            </div>
          </div>

          <div className="lb-info-row lb-info-sub">
            {detail?.filePath && (
              <span className="lb-filepath" title={detail.filePath}>{detail.filePath}</span>
            )}
            {detail?.originClass && detail.originClass !== 'photo' && (
              <><span className="lb-dot">·</span><span className="lb-origin">{detail.originClass}
                {detail.originConfidence != null && ` (${detail.originConfidence.toFixed(2)})`}
              </span></>
            )}
            {detail?.tags && detail.tags.length > 0 && (
              <><span className="lb-dot">·</span><span className="lb-taglist">{detail.tags.map(t => t.tag).join(' · ')}</span></>
            )}
          </div>
        </div>

      </div>
    </div>,
    document.body
  )
}
