import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { semanticSearch } from '../api/search'
import { PhotoCard } from '../components/PhotoCard'
import { LightboxList } from '../context/LightboxContext'

export function SearchPage() {
  const [searchParams, setSearchParams] = useSearchParams()
  const [query, setQuery] = useState(searchParams.get('q') ?? '')
  const [dateOpen, setDateOpen] = useState(false)
  const [dateFrom, setDateFrom] = useState(searchParams.get('from') ?? '')
  const [dateTo, setDateTo] = useState(searchParams.get('to') ?? '')
  const [originClass, setOriginClass] = useState(searchParams.get('originClass') ?? '')
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => { inputRef.current?.focus() }, [])

  // Keep URL in sync with filter state
  useEffect(() => {
    const params: Record<string, string> = {}
    if (query.trim()) params.q = query.trim()
    if (dateFrom) params.from = dateFrom
    if (dateTo) params.to = dateTo
    if (originClass) params.originClass = originClass
    setSearchParams(params, { replace: true })
  }, [query, dateFrom, dateTo, originClass, setSearchParams])

  const debouncedQuery = query.trim()

  const { data: results, isFetching } = useQuery({
    queryKey: ['search', debouncedQuery, dateFrom, dateTo, originClass],
    queryFn: () => semanticSearch(debouncedQuery, dateFrom || undefined, dateTo || undefined, 50, originClass || undefined),
    enabled: debouncedQuery.length >= 2,
    staleTime: 30_000,
  })

  const showHint  = debouncedQuery.length < 2
  const showEmpty = !showHint && !isFetching && results?.length === 0

  return (
    <div className="page">
      <div className="search-wrap">
        <div className="search-row">
          <span className="search-icon"><span className="mi">search</span></span>
          <input
            ref={inputRef}
            className="search-input"
            type="text"
            placeholder="People, places, events…"
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
          <div className="search-filters">
            <div
              className={`chip${dateOpen ? ' active' : ''}`}
              onClick={() => setDateOpen(o => !o)}
            >
              <span className="mi">calendar_today</span>Date range
            </div>
            <select
              className="year-select"
              value={originClass}
              onChange={e => setOriginClass(e.target.value)}
              title="Filter by type"
            >
              <option value="">All types</option>
              <option value="photo">Photo</option>
              <option value="people">People</option>
              <option value="screenshot">Screenshot</option>
              <option value="artwork">Artwork</option>
              <option value="document">Document</option>
              <option value="reference">Reference</option>
            </select>
          </div>
        </div>

        {dateOpen && (
          <div className="date-filter-row">
            <input className="date-input" type="date" value={dateFrom} onChange={e => setDateFrom(e.target.value)} />
            <span className="to-lbl">to</span>
            <input className="date-input" type="date" value={dateTo} onChange={e => setDateTo(e.target.value)} />
          </div>
        )}

      </div>

      {showHint && (
        <div className="search-hint">Type something to search your photos…</div>
      )}

      {isFetching && <div className="loading">Searching…</div>}

      {showEmpty && (
        <div className="search-hint">No results for "{debouncedQuery}"</div>
      )}

      {results && results.length > 0 && (
        <>
          <div className="search-hint" style={{ padding: '4px 0', textAlign: 'left' }}>
            Found {results.length} result{results.length !== 1 ? 's' : ''} for "{debouncedQuery}"
          </div>
          <LightboxList ids={results.map(m => m.id)}>
            <div className="gallery-grid">
              {results.map(item => (
                <PhotoCard key={item.id} item={item} className="gallery-card" />
              ))}
            </div>
          </LightboxList>
        </>
      )}
    </div>
  )
}
