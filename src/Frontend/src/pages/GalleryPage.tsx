import { useEffect } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight, Images } from 'lucide-react'
import { getMediaList } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'

const MONTHS = [
  'January','February','March','April','May','June',
  'July','August','September','October','November','December',
]

const SS_YEAR  = 'gallery-year'
const SS_MONTH = 'gallery-month'
const UNDATED  = 0  // sentinel year value for photos with null taken_at

const ORIGIN_FILTERS = [
  { value: '',           label: 'All' },
  { value: 'photo',      label: 'Photo' },
  { value: 'people',     label: 'People' },
  { value: 'screenshot', label: 'Screenshot' },
  { value: 'artwork',    label: 'Artwork' },
  { value: 'document',   label: 'Document' },
  { value: 'reference',  label: 'Reference' },
  { value: 'meme',       label: 'Meme' },
]

function currentYear()  { return new Date().getFullYear() }
function currentMonth() { return new Date().getMonth() + 1 }

export function GalleryPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const pageSize = 50

  const year        = parseInt(searchParams.get('year')  ?? sessionStorage.getItem(SS_YEAR)  ?? String(currentYear()))
  const month       = parseInt(searchParams.get('month') ?? sessionStorage.getItem(SS_MONTH) ?? String(currentMonth()))
  const page        = parseInt(searchParams.get('page')  ?? '1')
  const originClass = searchParams.get('originClass') ?? ''

  const isUndated = year === UNDATED

  useEffect(() => {
    sessionStorage.setItem(SS_YEAR, String(year))
    sessionStorage.setItem(SS_MONTH, String(month))
  }, [year, month])

  const setParams = (updates: { year?: number; month?: number; page?: number; originClass?: string }) => {
    const next: Record<string, string> = {
      year:  String(updates.year  ?? year),
      month: String(updates.month ?? month),
      page:  String(updates.page  ?? page),
    }
    const oc = updates.originClass !== undefined ? updates.originClass : originClass
    if (oc) next.originClass = oc
    setSearchParams(next)
  }

  const from = new Date(Date.UTC(year, month - 1, 1)).toISOString()
  const to   = new Date(Date.UTC(year, month, 0, 23, 59, 59)).toISOString()

  const oc = originClass || undefined

  const { data, isFetching } = useQuery({
    queryKey: isUndated
      ? ['gallery', 'undated', page, originClass]
      : ['gallery', year, month, page, originClass],
    queryFn: () => isUndated
      ? getMediaList({ page, pageSize, noDate: true, originClass: oc, sortAsc: true })
      : getMediaList({ page, pageSize, from, to, originClass: oc, sortAsc: true }),
    staleTime: 30_000,
  })

  const totalPages = data ? Math.ceil(data.total / pageSize) : 1

  const shiftMonth = (delta: number) => {
    let m = month + delta
    let y = year
    if (m > 12) { m = 1; y++ }
    if (m < 1)  { m = 12; y-- }
    setParams({ year: y, month: m, page: 1 })
  }

  const jumpYear = (y: number) => setParams({ year: y, month: y === UNDATED ? month : 1, page: 1 })

  const years: number[] = []
  for (let y = currentYear(); y >= 1997; y--) years.push(y)

  return (
    <div className="page">
      <div className="gallery-head">
        <div className="gallery-head-left">
          <div className="eyebrow">
            {data
              ? `${data.total.toLocaleString()} ${data.total === 1 ? 'item' : 'items'}`
              : 'Gallery'}
          </div>
          {isUndated ? (
            <span className="bc-month">Undated</span>
          ) : (
            <div className="month-stepper">
              <button className="bc-arrow" onClick={() => shiftMonth(-1)} aria-label="Previous month">
                <ChevronLeft size={18} />
              </button>
              <span className="bc-month">{MONTHS[month - 1]} {year}</span>
              <button className="bc-arrow" onClick={() => shiftMonth(1)} aria-label="Next month">
                <ChevronRight size={18} />
              </button>
            </div>
          )}
        </div>

        <label className="year-pill-wrap" style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
          <span className="eyebrow" style={{ letterSpacing: '.14em' }}>Year</span>
          <select
            className="year-select"
            value={year}
            onChange={e => jumpYear(parseInt(e.target.value))}
          >
            {years.map(y => <option key={y} value={y}>{y}</option>)}
            <option value={UNDATED}>Undated</option>
          </select>
        </label>
      </div>

      <div className="origin-filter-row">
        {ORIGIN_FILTERS.map(f => (
          <button
            key={f.value}
            className={`origin-chip${originClass === f.value ? ' active' : ''}`}
            onClick={() => setParams({ originClass: f.value, page: 1 })}
          >
            {f.label}
          </button>
        ))}
      </div>

      {isFetching && <div className="loading">Loading…</div>}

      {!isFetching && data && data.items.length === 0 && (
        <div className="empty-state">
          <Images size={40} strokeWidth={1.4} />
          <span>{isUndated ? 'No undated photos' : `No photos in ${MONTHS[month - 1]} ${year}`}</span>
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
              onClick={() => setParams({ page: page - 1 })}
            >
              <ChevronLeft size={14} />Previous
            </button>
            <span className="pager-info">
              Page {page} of {totalPages} · {data.total} photos
            </span>
            <button
              className="pager-btn"
              disabled={page >= totalPages}
              onClick={() => setParams({ page: page + 1 })}
            >
              Next<ChevronRight size={14} />
            </button>
          </div>
        </>
      )}
    </div>
  )
}
