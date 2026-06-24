import { useMemo, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { getMediaList, getStats, getOnThisDay, thumbnailUrl } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'
import { useLightbox } from '../context/LightboxContext'

function formatMonthYear(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleDateString('en-US', { month: 'short', year: 'numeric' })
}

function formatFullDate(iso: string): string {
  const d = new Date(iso)
  return d.toLocaleDateString('en-US', { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric' })
}

export function DashboardPage() {
  const { open } = useLightbox()
  const navigate = useNavigate()
  const today = new Date()
  const [dateOpen, setDateOpen] = useState(false)
  const [dateFrom, setDateFrom] = useState('')
  const [dateTo, setDateTo] = useState('')

  const { data: stats } = useQuery({
    queryKey: ['stats'],
    queryFn: getStats,
    staleTime: 60_000,
  })

  const { data: potdResult } = useQuery({
    queryKey: ['potd'],
    queryFn: () => getMediaList({ page: 1, pageSize: 1, favorite: true, from: '2000-01-01T00:00:00.000Z' }),
    staleTime: 60_000,
  })

  const { data: recentResult } = useQuery({
    queryKey: ['recent'],
    queryFn: () => getMediaList({ page: 1, pageSize: 1, from: '2000-01-01T00:00:00.000Z' }),
    staleTime: 60_000,
  })

  const { data: otdGroups } = useQuery({
    queryKey: ['otd', today.getMonth() + 1, today.getDate()],
    queryFn: () => getOnThisDay(today.getMonth() + 1, today.getDate()),
    staleTime: 60_000,
  })

  // Prefer a photo from "on this day" in a past year — more meaningful than most-recent.
  // Use a stable day-based index so the pick doesn't flicker on re-render.
  const potd = useMemo(() => {
    const otdItems = otdGroups?.flatMap(g => g.items) ?? []
    if (otdItems.length > 0) {
      const idx = (today.getDate() + today.getMonth() * 31) % otdItems.length
      return otdItems[idx]
    }
    return potdResult?.items?.[0] ?? recentResult?.items?.[0]
  }, [otdGroups, potdResult, recentResult])

  const handleDashSearch = (value: string) => {
    if (value.length < 2) return
    const params = new URLSearchParams({ q: value })
    if (dateFrom) params.set('from', dateFrom)
    if (dateTo) params.set('to', dateTo)
    navigate(`/search?${params.toString()}`)
  }

  return (
    <div className="page">
      {/* Search bar */}
      <div className="search-wrap">
        <div className="search-row">
          <span className="search-icon"><span className="mi">search</span></span>
          <input
            className="search-input"
            type="text"
            placeholder="Search your photos — people, places, events…"
            onKeyDown={e => {
              if (e.key === 'Enter') handleDashSearch((e.target as HTMLInputElement).value)
            }}
            onChange={e => handleDashSearch(e.target.value)}
          />
          <div className="search-filters">
            <div
              className={`chip${dateOpen ? ' active' : ''}`}
              onClick={() => setDateOpen(o => !o)}
            >
              <span className="mi">calendar_today</span>Date range
            </div>
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

      {/* Widgets */}
      <div className="widgets">
        {/* Photo of the Day */}
        <div className="card">
          <div className="card-head">
            <span className="card-title">Photo of the Day</span>
            <span className="card-action" style={{ cursor: 'default', color: 'var(--label3)' }}>
              {today.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' })}
            </span>
          </div>
          {potd ? (
            <>
              <div className="potd-img" onClick={() => open(potd.id)}>
                <img src={thumbnailUrl(potd.id)} alt="" />
              </div>
              <div className="potd-footer">
                <div>
                  <div className="potd-date-main">{formatFullDate(potd.takenAt ?? new Date().toISOString())}</div>
                  {potd.address && (
                    <div className="potd-date-sub">{potd.address}</div>
                  )}
                </div>
              </div>
            </>
          ) : (
            <div className="loading" style={{ padding: '40px 0' }}>Loading…</div>
          )}
        </div>

        {/* Library stats */}
        <div className="card">
          <div className="card-head">
            <span className="card-title">Library</span>
            <Link to="/gallery" className="card-action">All photos</Link>
          </div>
          {recentResult?.items?.[0] && (
            <div className="stats-banner">
              <img src={thumbnailUrl(recentResult.items[0].id)} alt="" />
              <div className="stats-banner-fade" />
            </div>
          )}
          <div className="stats-body">
            <div className="stats-row">
              <div className="stat-item">
                <span className="stat-num">{stats?.photos ?? '—'}</span>
                <span className="stat-lbl">Photos</span>
              </div>
              <div className="stats-divider" />
              <div className="stat-item">
                <span className="stat-num">{stats?.videos ?? '—'}</span>
                <span className="stat-lbl">Videos</span>
              </div>
            </div>
            <div className="stats-row" style={{ marginTop: 12 }}>
              <div className="stat-item">
                <span className="stat-num" style={{ fontSize: 18 }}>
                  {stats?.latestDate ? formatMonthYear(stats.latestDate) : '—'}
                </span>
                <span className="stat-lbl">Latest</span>
              </div>
              <div className="stats-divider" />
              <div className="stat-item">
                <span className="stat-num">{stats?.countries ?? '—'}</span>
                <span className="stat-lbl">Countries</span>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* On This Day strip */}
      {otdGroups && otdGroups.length > 0 && (
        <div>
          <div className="sec-head">
            <span className="sec-title">On This Day</span>
            <Link to="/on-this-day" className="sec-link">See all</Link>
          </div>
          <div className="otd-scroll">
            {otdGroups.map(group => (
              <div key={group.year} className="otd-group">
                <div className="otd-year-lbl">{group.year}</div>
                <div className="otd-photos">
                  {group.items.slice(0, 4).map(item => (
                    <PhotoCard
                      key={item.id}
                      item={item}
                      className="otd-photo"
                    />
                  ))}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}
