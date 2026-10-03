import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getMediaList } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'
import { LightboxList } from '../context/LightboxContext'

export function VideosPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const pageSize = 50

  const page = parseInt(searchParams.get('page') ?? '1')
  const setPage = (p: number) => setSearchParams({ page: String(p) })

  const { data, isFetching } = useQuery({
    queryKey: ['videos', page],
    // Newest first, matching Gallery — the handler already defaults SortAsc to false.
    queryFn: () => getMediaList({ page, pageSize, isVideo: true }),
    staleTime: 30_000,
  })

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
          <LightboxList ids={data.items.map(m => m.id)}>
            <div className="videos-grid">
              {data.items.map(item => (
                <PhotoCard key={item.id} item={item} className="video-card" />
              ))}
            </div>
          </LightboxList>

          <div className="gallery-pager">
            <button
              className="pager-btn"
              disabled={page === 1}
              onClick={() => setPage(page - 1)}
            >
              <span className="mi" style={{ fontSize: 16 }}>chevron_left</span>Previous
            </button>
            <span className="pager-info">Page {page} of {totalPages} · {data.total} videos</span>
            <button
              className="pager-btn"
              disabled={page >= totalPages}
              onClick={() => setPage(page + 1)}
            >
              Next<span className="mi" style={{ fontSize: 16 }}>chevron_right</span>
            </button>
          </div>
        </>
      )}
    </div>
  )
}
