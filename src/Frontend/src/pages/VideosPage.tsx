import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getMediaList } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'

export function VideosPage() {
  const [page, setPage] = useState(1)
  const pageSize = 50

  const { data, isFetching } = useQuery({
    queryKey: ['videos', page],
    queryFn: () => getMediaList({ page, pageSize }),
    staleTime: 30_000,
    select: result => ({
      ...result,
      items: result.items.filter(i => i.isVideo),
    }),
  })

  // Re-fetch all without filter for a real videos endpoint (API doesn't filter by isVideo yet)
  // We filter client-side for now since there's no isVideo query param
  const totalPages = data ? Math.ceil(data.total / pageSize) : 1

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 0 }}>
        <span className="sec-title">Videos</span>
      </div>

      {isFetching && <div className="loading">Loading…</div>}

      {!isFetching && data?.items.length === 0 && (
        <div className="empty-state">
          <span className="mi">videocam</span>
          <span>No videos in your library</span>
        </div>
      )}

      {data && data.items.length > 0 && (
        <>
          <div className="videos-grid">
            {data.items.map(item => (
              <PhotoCard key={item.id} item={item} className="video-card" />
            ))}
          </div>

          <div className="gallery-pager">
            <button
              className="pager-btn"
              disabled={page === 1}
              onClick={() => setPage(p => p - 1)}
            >
              <span className="mi" style={{ fontSize: 16 }}>chevron_left</span>Previous
            </button>
            <span className="pager-info">Page {page} of {totalPages}</span>
            <button
              className="pager-btn"
              disabled={page >= totalPages}
              onClick={() => setPage(p => p + 1)}
            >
              Next<span className="mi" style={{ fontSize: 16 }}>chevron_right</span>
            </button>
          </div>
        </>
      )}
    </div>
  )
}
