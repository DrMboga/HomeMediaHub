import { useQuery } from '@tanstack/react-query'
import { getOnThisDay } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'

function todayLabel(): string {
  return new Date().toLocaleDateString('en-US', { month: 'long', day: 'numeric' })
}

export function OnThisDayPage() {
  const today = new Date()

  const { data: groups, isLoading } = useQuery({
    queryKey: ['otd', today.getMonth() + 1, today.getDate()],
    queryFn: () => getOnThisDay(today.getMonth() + 1, today.getDate()),
    staleTime: 60_000,
  })

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 0 }}>
        <span className="sec-title">On This Day · {todayLabel()}</span>
      </div>

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && groups?.length === 0 && (
        <div className="empty-state">
          <span className="mi">today</span>
          <span>No photos from this day in previous years</span>
        </div>
      )}

      <div style={{ display: 'flex', flexDirection: 'column', gap: 32 }}>
        {groups?.map(group => {
          const dayLabel = new Date(group.year, today.getMonth(), today.getDate())
            .toLocaleDateString('en-US', { weekday: 'short', month: 'long', day: 'numeric', year: 'numeric' })
          return (
            <div key={group.year} className="otd-section">
              <div className="otd-section-title">
                {dayLabel}
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
          )
        })}
      </div>
    </div>
  )
}
