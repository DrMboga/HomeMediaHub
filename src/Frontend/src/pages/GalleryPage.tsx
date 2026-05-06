import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getMediaList } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'

const MONTHS = [
  'January','February','March','April','May','June',
  'July','August','September','October','November','December',
]

function currentYear() { return new Date().getFullYear() }
function currentMonth() { return new Date().getMonth() + 1 }

export function GalleryPage() {
  const [year, setYear] = useState(currentYear)
  const [month, setMonth] = useState(currentMonth)
  const [page, setPage] = useState(1)
  const pageSize = 50

  const from = new Date(Date.UTC(year, month - 1, 1)).toISOString()
  const to   = new Date(Date.UTC(year, month, 0, 23, 59, 59)).toISOString()

  const { data, isFetching } = useQuery({
    queryKey: ['gallery', year, month, page],
    queryFn: () => getMediaList({ page, pageSize, from, to }),
    staleTime: 30_000,
  })

  const totalPages = data ? Math.ceil(data.total / pageSize) : 1

  const shiftMonth = (delta: number) => {
    let m = month + delta
    let y = year
    if (m > 12) { m = 1; y++ }
    if (m < 1)  { m = 12; y-- }
    setMonth(m); setYear(y); setPage(1)
  }

  const jumpYear = (y: number) => { setYear(y); setMonth(1); setPage(1) }

  const years: number[] = []
  for (let y = currentYear(); y >= 1997; y--) years.push(y)

  return (
    <div className="page">
      <div className="gallery-nav">
        <span className="gallery-title">Gallery</span>
        <div className="breadcrumb">
          <button className="bc-arrow" onClick={() => shiftMonth(-1)}>
            <span className="mi">chevron_left</span>
          </button>
          <span className="bc-month">{MONTHS[month - 1]} {year}</span>
          <button className="bc-arrow" onClick={() => shiftMonth(1)}>
            <span className="mi">chevron_right</span>
          </button>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8, marginLeft: 'auto' }}>
          <span style={{ fontSize: 11, color: 'var(--label3)' }}>Year:</span>
          <select
            className="year-select"
            value={year}
            onChange={e => jumpYear(parseInt(e.target.value))}
          >
            {years.map(y => <option key={y} value={y}>{y}</option>)}
          </select>
        </div>
      </div>

      {isFetching && <div className="loading">Loading…</div>}

      {!isFetching && data && data.items.length === 0 && (
        <div className="empty-state">
          <span className="mi">photo_library</span>
          <span>No photos in {MONTHS[month - 1]} {year}</span>
        </div>
      )}

      {data && data.items.length > 0 && (
        <>
          <div className="gallery-grid">
            {data.items.map(item => (
              <PhotoCard key={item.id} item={item} className="gallery-card" />
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
            <span className="pager-info">
              Page {page} of {totalPages} · {data.total} photos
            </span>
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
