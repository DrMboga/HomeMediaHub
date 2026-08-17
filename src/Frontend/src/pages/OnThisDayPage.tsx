import { useMemo } from 'react'
import { useSearchParams } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { ChevronLeft, ChevronRight } from 'lucide-react'
import { getOnThisDay } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'

interface CalendarDay { year: number; month: number; day: number }

function pad(n: number): string {
  return n.toString().padStart(2, '0')
}

function toParam(d: CalendarDay): string {
  return `${d.year}-${pad(d.month)}-${pad(d.day)}`
}

/**
 * Parsed by hand rather than with `new Date('2026-08-16')`, which the spec reads as UTC
 * midnight — `getDate()` on the result then answers in local time and can name the day before.
 */
function parseParam(value: string | null): CalendarDay | null {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value ?? '')
  if (!m) return null
  const [year, month, day] = [Number(m[1]), Number(m[2]), Number(m[3])]
  if (month < 1 || month > 12 || day < 1 || day > 31) return null
  return { year, month, day }
}

function todayCalendarDay(): CalendarDay {
  const now = new Date()
  return { year: now.getFullYear(), month: now.getMonth() + 1, day: now.getDate() }
}

/** Steps a calendar day, letting Date handle month/year rollover. */
function shiftDay(d: CalendarDay, delta: number): CalendarDay {
  const shifted = new Date(d.year, d.month - 1, d.day + delta)
  return { year: shifted.getFullYear(), month: shifted.getMonth() + 1, day: shifted.getDate() }
}

/**
 * "16 August" — the year is deliberately absent: the picker chooses a month and day, and the
 * page shows every year for it. Formatted through a leap year so 29 February survives.
 */
function monthDayLabel(month: number, day: number): string {
  return new Date(2024, month - 1, day).toLocaleDateString('en-GB', { day: 'numeric', month: 'long' })
}

/**
 * "Friday 16 August 2024". `new Date(2023, 1, 29)` silently rolls to 1 March, so the weekday is
 * only trusted when the constructed date round-trips — otherwise the day never happened in that
 * year and the heading drops the weekday rather than naming the wrong one.
 */
function sectionLabel(year: number, month: number, day: number): string {
  const d = new Date(year, month - 1, day)
  if (d.getMonth() === month - 1 && d.getDate() === day) {
    return d.toLocaleDateString('en-GB', { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
  }
  return `${monthDayLabel(month, day)} ${year}`
}

export function OnThisDayPage() {
  const [searchParams, setSearchParams] = useSearchParams()

  // The selection lives in the URL so it survives reload, back-button and sharing.
  const selected = parseParam(searchParams.get('d')) ?? todayCalendarDay()

  const setSelected = (d: CalendarDay) => setSearchParams({ d: toParam(d) })

  // No limit — this page shows everything for the date. A hardcoded 200-row cap used to hide
  // twelve of the fifteen year sections of a busy anniversary without saying so.
  const { data: groups, isLoading } = useQuery({
    queryKey: ['otd', selected.month, selected.day, 0],
    queryFn: () => getOnThisDay(selected.month, selected.day),
    staleTime: 60_000,
  })

  const total = useMemo(
    () => groups?.reduce((sum, g) => sum + g.items.length, 0) ?? 0,
    [groups],
  )

  // Year blocks stay newest-first; only the photos inside a block run forward through the day.
  // Sorted explicitly on takenAt rather than reversed — reversing would bake in an assumption
  // about the API's order that nothing enforces. Done here and not in the handler because the
  // dashboard slices the API's first four per year and must keep getting the most recent ones.
  const sortedGroups = useMemo(
    () => groups?.map(g => ({
      year: g.year,
      items: [...g.items].sort(
        (a, b) => new Date(a.takenAt ?? 0).getTime() - new Date(b.takenAt ?? 0).getTime(),
      ),
    })),
    [groups],
  )

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 0 }}>
        <span className="sec-title">On This Day · {monthDayLabel(selected.month, selected.day)}</span>
        {!isLoading && total > 0 && (
          <span className="otd-total">
            {total.toLocaleString()} photo{total !== 1 ? 's' : ''} across {sortedGroups?.length} year
            {sortedGroups?.length !== 1 ? 's' : ''}
          </span>
        )}
      </div>

      <div className="otd-toolbar">
        <button
          className="pager-btn"
          aria-label="Previous day"
          onClick={() => setSelected(shiftDay(selected, -1))}
        >
          <ChevronLeft size={14} />
        </button>
        <input
          type="date"
          className="otd-date-input"
          value={toParam(selected)}
          onChange={e => {
            const picked = parseParam(e.target.value)
            if (picked) setSelected(picked)
          }}
        />
        <button
          className="pager-btn"
          aria-label="Next day"
          onClick={() => setSelected(shiftDay(selected, 1))}
        >
          <ChevronRight size={14} />
        </button>
      </div>

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && sortedGroups?.length === 0 && (
        <div className="empty-state">
          <span className="mi">today</span>
          <span>No photos from this day in previous years</span>
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: 32 }}>
        {sortedGroups?.map(group => (
          <div key={group.year} className="otd-section">
            <div className="otd-section-title">
              {sectionLabel(group.year, selected.month, selected.day)}
              <span className="otd-count">
                {group.items.length} photo{group.items.length !== 1 ? 's' : ''}
              </span>
            </div>
            <div className="otd-section-row">
              {group.items.map(item => (
                <PhotoCard key={item.id} item={item} className="otd-full-photo" />
              ))}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}
