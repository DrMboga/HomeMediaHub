import { useEffect, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getReviewQueue, confirmReview, bulkDeleteMedia, thumbnailUrl } from '../api/media'
import type { MediaListItem } from '../types/api'
import { formatShortDate } from '../utils/mediaDate'

const PAGE_SIZE = 100

export function ReviewPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const page = Math.max(1, parseInt(searchParams.get('page') ?? '1'))
  const queryClient = useQueryClient()

  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirmDelete, setConfirmDelete] = useState(false)

  const { data, isLoading } = useQuery({
    queryKey: ['review-queue', page],
    queryFn: () => getReviewQueue(page, PAGE_SIZE),
    staleTime: 60_000,
  })

  const items = data?.items ?? []
  const total = data?.total ?? 0
  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE))

  // IDs in `selected` that are still on this page (items removed by Meme/Real drop out automatically)
  const activeSelected = new Set(items.map(i => i.id).filter(id => selected.has(id)))
  const allOnPageSelected = items.length > 0 && activeSelected.size === items.length

  // Clear selection on page change
  useEffect(() => {
    setSelected(new Set())
    setConfirmDelete(false)
  }, [page])

  const setPage = (p: number) => setSearchParams({ page: String(p) }, { replace: true })

  const toggleSelect = (id: string) => {
    setSelected(prev => {
      const next = new Set(prev)
      next.has(id) ? next.delete(id) : next.add(id)
      return next
    })
    setConfirmDelete(false)
  }

  const toggleSelectAll = () => {
    if (allOnPageSelected) {
      setSelected(new Set())
    } else {
      setSelected(new Set(items.map(i => i.id)))
    }
    setConfirmDelete(false)
  }

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

  const { mutate: bulkDelete, isPending: isDeleting } = useMutation({
    mutationFn: (ids: string[]) => bulkDeleteMedia(ids),
    onMutate: async (ids) => {
      const idSet = new Set(ids)
      queryClient.setQueryData<{ items: MediaListItem[]; total: number }>(
        ['review-queue', page],
        old => old
          ? { items: old.items.filter(i => !idSet.has(i.id)), total: Math.max(0, old.total - ids.length) }
          : { items: [], total: 0 }
      )
      setSelected(new Set())
      setConfirmDelete(false)
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

      {/* Bulk-select toolbar */}
      {activeSelected.size > 0 && (
        <div className="review-toolbar">
          <label className="review-sel-all">
            <input
              type="checkbox"
              checked={allOnPageSelected}
              onChange={toggleSelectAll}
            />
            Select all on page
          </label>
          <span className="review-sel-count">{activeSelected.size} selected</span>
          {confirmDelete ? (
            <>
              <span className="review-del-prompt">
                Permanently delete {activeSelected.size} {activeSelected.size === 1 ? 'file' : 'files'}?
              </span>
              <button
                className="review-btn-confirm"
                onClick={() => bulkDelete([...activeSelected])}
                disabled={isDeleting}
              >
                <span className="mi">delete_forever</span>Yes, delete
              </button>
              <button className="review-btn-cancel" onClick={() => setConfirmDelete(false)}>
                Cancel
              </button>
            </>
          ) : (
            <button className="review-btn-delete" onClick={() => setConfirmDelete(true)}>
              <span className="mi">delete_forever</span>Delete ({activeSelected.size})
            </button>
          )}
        </div>
      )}

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && total === 0 && (
        <div className="empty-state">
          <span className="mi">check_circle</span>
          <span>Nothing to review — all caught up</span>
        </div>
      )}

      {items.length > 0 && (
        <div className="review-grid">
          {items.map((item: MediaListItem) => {
            const isSel = activeSelected.has(item.id)
            return (
              <div key={item.id} className={`review-card${isSel ? ' selected' : ''}`}>
                <div
                  className="photo-card gallery-card"
                  style={{ cursor: 'pointer' }}
                  onClick={() => toggleSelect(item.id)}
                >
                  <img src={thumbnailUrl(item.id)} alt="" loading="lazy" />
                  <div className="photo-card-grad" />
                  <div className="review-card-check" />
                  {item.originClass && (
                    <span className="review-badge">{item.originClass}</span>
                  )}
                  <div className="photo-card-date">{formatShortDate(item.takenAt)}</div>
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
            )
          })}
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
