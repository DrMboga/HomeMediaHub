import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getReviewQueue, confirmReview, thumbnailUrl } from '../api/media'
import type { MediaListItem } from '../types/api'

function formatCardDate(takenAt: string | null): string {
  if (!takenAt) return '—'
  return new Date(takenAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric' })
}

export function ReviewPage() {
  const queryClient = useQueryClient()

  const { data: items = [], isLoading } = useQuery({
    queryKey: ['review-queue'],
    queryFn: getReviewQueue,
    staleTime: 60_000,
  })

  const { mutate: confirm } = useMutation({
    mutationFn: ({ id, action }: { id: string; action: 'meme' | 'real' }) =>
      confirmReview(id, action),
    onMutate: async ({ id }) => {
      queryClient.setQueryData<MediaListItem[]>(['review-queue'], old =>
        old ? old.filter(item => item.id !== id) : []
      )
    },
    onError: () => {
      queryClient.invalidateQueries({ queryKey: ['review-queue'] })
    },
  })

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 16 }}>
        <span className="sec-title">Review</span>
        {items.length > 0 && (
          <span className="sec-count">{items.length} to review</span>
        )}
      </div>

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && items.length === 0 && (
        <div className="empty-state">
          <span className="mi">check_circle</span>
          <span>Nothing to review — all caught up</span>
        </div>
      )}

      {items.length > 0 && (
        <div className="review-grid">
          {items.map(item => (
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
    </div>
  )
}
