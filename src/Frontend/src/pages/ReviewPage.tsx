import { useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getReviewQueue, confirmReview, thumbnailUrl } from '../api/media'
import type { MediaListItem } from '../types/api'

const PAGE_SIZE = 50

function formatCardDate(takenAt: string | null): string {
  if (!takenAt) return '—'
  return new Date(takenAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
}

export function ReviewPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const page = Math.max(1, parseInt(searchParams.get('page') ?? '1'))
  const queryClient = useQueryClient()

  const { data, isLoading } = useQuery({
    queryKey: ['review-queue', page],
    queryFn: () => getReviewQueue(page, PAGE_SIZE),
    staleTime: 60_000,
  })

  const items = data?.items ?? []
  const total = data?.total ?? 0
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE))

  const setPage = (p: number) => setSearchParams({ page: String(p) }, { replace: true })

  const { mutate: confirm } = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'meme' | 'real' }) =>
      confirmReview(id, action),
    onMutate: async ({ id }) => {
      queryClient.setQueryData<{ items: MediaListItem[]; total: number }>(
        ['review-queue', page],
        old => old
          ? { items: old.items.filter(item => item.id !== id), total: old.total - 1 }
          : { items: [], total: 0 }
      )
    },
    onSettled: () => {
      queryClient.invalidateQueries({ queryKey: ['review-queue'] })
    },
  })

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 16 }}>
        <span className="sec-title">Review</span>
        {total > 0 && (
          <span className="sec-count">{total} to review</span>
        )}
      </div>

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && total === 0 && (
        <div className="empty-state">
          <span className="mi">check_circle</span>
          <span>Nothing to review — all caught up</span>
        </div>
      )}

      {items.length > 0 && (
        <div className="review-grid">
          {items.map((item: MediaListItem) => (
            <div key={item.id} className="review-card">
              <div className="photo-card gallery-card" style={{ cursor: 'default' }}>
                <img src={thumbnailUrl(item.id)} alt="" loading="lazy" />
                <div className="photo-card-grad" />
                {item.originClass && (
                  <span className="review-badge">{item.originClass}</span>
                )}
                <div className="photo-card-date">{formatCardDate(item.takenAt)}</div>
              </div>
              <div className="review-actions">
                <button
                  className="review-btn review-btn-meme"
                  onClick={() => confirm({ id: item.id, action: 'meme' })}
                >
                  <span className="mi">block</span>Meme
                </button>
                <button
                  className="review-btn review-btn-real"
                  onClick={() => confirm({ id: item.id, action: 'real' })}
                >
                  <span className="mi">check_circle</span>Real Photo
                </button>
              </div>
            </div>
          ))}
        </div>
      )}

      {totalPages > 1 && (
        <div className="pagination">
          <button
            className="pager-btn"
            disabled={page <= 1}
            onClick={() => setPage(page - 1)}
          >
            <span className="mi" style={{ fontSize: 16 }}>chevron_left</span>Previous
          </button>
          <span className="pager-info">
            Page {page} of {totalPages} · {total} items
          </span>
          <button
            className="pager-btn"
            disabled={page >= totalPages}
            onClick={() => setPage(page + 1)}
          >
            Next<span className="mi" style={{ fontSize: 16 }}>chevron_right</span>
          </button>
        </div>
      )}
    </div>
  )
}
