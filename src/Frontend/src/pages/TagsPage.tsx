import { useMemo, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { keepPreviousData, useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, Tags, X } from 'lucide-react'
import { getTagMedia, getTags } from '../api/tags'
import { PhotoCard } from '../components/PhotoCard'

const PAGE_SIZE = 50

// A relative floor, not a constant count: "≥ 4 photos" would show 14 chips on stage's 276
// files and all 46 after the archive import, which defeats the point. Top-N by count holds
// at both scales — and the YOLO vocabulary is capped at COCO's 80 classes either way.
const CHIP_FLOOR = 24

export function TagsPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const [showAll, setShowAll] = useState(false)

  const selected = searchParams.getAll('tags')
  const page = parseInt(searchParams.get('page') ?? '1')

  const setSelection = (tags: string[], nextPage = 1) => {
    const next = new URLSearchParams()
    tags.forEach(t => next.append('tags', t))
    if (nextPage > 1) next.set('page', String(nextPage))
    setSearchParams(next)
  }

  const toggleTag = (tag: string) =>
    setSelection(
      selected.includes(tag) ? selected.filter(t => t !== tag) : [...selected, tag]
    )

  // Two queries, deliberately. The unfiltered vocabulary decides which chips exist and
  // fixes their order; the narrowed one decides each chip's live count and whether it is
  // a dead end. With nothing selected the keys coincide and react-query issues one request.
  const { data: vocabulary, isLoading: vocabLoading } = useQuery({
    queryKey: ['tags', []],
    queryFn: () => getTags([]),
    staleTime: 60_000,
  })

  // keepPreviousData on both: a query key changes on every toggle and every page step, and
  // without it react-query blanks `data` for the round trip. The chip row would flash fully
  // dashed-and-disabled between selections, and the grid would empty between pages.
  const { data: narrowed } = useQuery({
    queryKey: ['tags', selected],
    queryFn: () => getTags(selected),
    staleTime: 60_000,
    placeholderData: keepPreviousData,
  })

  const { data: media, isFetching: mediaFetching } = useQuery({
    queryKey: ['tag-media', selected, page],
    queryFn: () => getTagMedia(selected, page, PAGE_SIZE),
    enabled: selected.length > 0,
    staleTime: 30_000,
    placeholderData: keepPreviousData,
  })

  // Absent from the narrowed response means zero photos alongside the current selection.
  const narrowedCounts = useMemo(
    () => new Map((narrowed ?? []).map(t => [t.tag, t.count])),
    [narrowed]
  )

  // Chips keep the unfiltered order even as counts change — re-sorting on every toggle
  // would slide them out from under the cursor. Anything below the floor stays reachable
  // through "show all", and a selected chip is never hidden, however rare it is.
  const chips = useMemo(() => {
    if (!vocabulary) return []
    if (showAll) return vocabulary
    return vocabulary.filter((t, i) => i < CHIP_FLOOR || selected.includes(t.tag))
  }, [vocabulary, showAll, selected])

  const hiddenCount = (vocabulary?.length ?? 0) - chips.length
  const totalPages = media ? Math.max(1, Math.ceil(media.total / PAGE_SIZE)) : 1

  return (
    <div className="page">
      <div className="gallery-head">
        <div className="gallery-head-left">
          <div className="eyebrow">
            {vocabulary
              ? `${vocabulary.length} ${vocabulary.length === 1 ? 'tag' : 'tags'}`
              : 'Tags'}
          </div>
          <span className="bc-month">
            {selected.length > 0 ? selected.join(' AND ') : 'All tags'}
          </span>
        </div>

        {selected.length > 0 && (
          <button className="pager-btn" onClick={() => setSelection([])}>
            <X size={14} />Clear all
          </button>
        )}
      </div>

      {vocabLoading && <div className="loading">Loading tags…</div>}

      {vocabulary && vocabulary.length === 0 && (
        <div className="empty-state">
          <Tags size={40} strokeWidth={1.4} />
          <span>No tags yet — AI enrichment has not labelled anything.</span>
        </div>
      )}

      {chips.length > 0 && (
        <div className="tag-chip-row">
          {chips.map(({ tag, count }) => {
            const isSelected = selected.includes(tag)
            const liveCount = narrowedCounts.get(tag) ?? 0
            // A selected chip stays clickable no matter what, or the last one could
            // never be removed. Everything else with no co-occurrence is a dead end.
            const isDisabled = !isSelected && liveCount === 0

            return (
              <button
                key={tag}
                className={`tag-chip${isSelected ? ' selected' : ''}${isDisabled ? ' disabled' : ''}`}
                disabled={isDisabled}
                onClick={() => toggleTag(tag)}
                title={
                  isDisabled
                    ? `No photos combine ${tag} with the current selection`
                    : `${liveCount} photo${liveCount === 1 ? '' : 's'}`
                }
              >
                <span className="tag-chip-name">{tag}</span>
                {/* Every chip's number means one thing: photos you get with this chip in
                    the selection. Showing the unfiltered count on selected chips instead
                    made two selected chips disagree about the same result set — "person
                    AND bottle" returned 7 photos under a chip row reading 104 and 7. */}
                <span className="tag-chip-count">{selected.length === 0 ? count : liveCount}</span>
              </button>
            )
          })}

          {(hiddenCount > 0 || showAll) && (
            <button className="tag-chip tag-chip-more" onClick={() => setShowAll(s => !s)}>
              {showAll ? 'Show less' : `Show all ${vocabulary?.length}`}
            </button>
          )}
        </div>
      )}

      {selected.length === 0 && vocabulary && vocabulary.length > 0 && (
        <div className="empty-state">
          <Tags size={40} strokeWidth={1.4} />
          <span>Pick a tag to browse. Selecting several shows only photos carrying all of them.</span>
        </div>
      )}

      {selected.length > 0 && mediaFetching && <div className="loading">Loading…</div>}

      {/* Facet narrowing should make this unreachable, but it is the safety net for a
          stale chip row racing a selection — so it explains itself and offers a way out. */}
      {selected.length > 0 && !mediaFetching && media && media.items.length === 0 && (
        <div className="empty-state">
          <Tags size={40} strokeWidth={1.4} />
          <span>No photos carry {selected.join(' AND ')}.</span>
          {selected.length > 1 && (
            <div className="tag-chip-row" style={{ justifyContent: 'center', borderBottom: 'none' }}>
              {selected.map(tag => (
                <button
                  key={tag}
                  className="tag-chip"
                  onClick={() => setSelection(selected.filter(t => t !== tag))}
                >
                  <X size={12} />
                  <span className="tag-chip-name">Drop {tag}</span>
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {selected.length > 0 && media && media.items.length > 0 && (
        <>
          <div className="gallery-grid">
            {media.items.map(item => (
              <PhotoCard key={item.id} item={item} className="gallery-card" />
            ))}
          </div>

          {totalPages > 1 && (
            <div className="gallery-pager">
              <button
                className="pager-btn"
                disabled={page === 1}
                onClick={() => setSelection(selected, page - 1)}
              >
                <ChevronLeft size={14} />Previous
              </button>
              <span className="pager-info">
                Page {page} of {totalPages} · {media.total} photos
              </span>
              <button
                className="pager-btn"
                disabled={page >= totalPages}
                onClick={() => setSelection(selected, page + 1)}
              >
                Next<ChevronRight size={14} />
              </button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
