import { MapContainer, TileLayer, CircleMarker, Tooltip } from 'react-leaflet'
import { useQuery } from '@tanstack/react-query'
import { getGeoPins, getStats } from '../api/media'
import { useLightbox } from '../context/LightboxContext'

export function GeoPage() {
  const { open } = useLightbox()

  const { data: pins } = useQuery({
    queryKey: ['geo'],
    queryFn: getGeoPins,
    staleTime: 60_000,
  })

  const { data: stats } = useQuery({
    queryKey: ['stats'],
    queryFn: getStats,
    staleTime: 60_000,
  })

  // Group pins by address to count photos per location
  const grouped = pins ? Object.values(
    pins.reduce<Record<string, { lat: number; lng: number; address: string | null; ids: string[] }>>(
      (acc, pin) => {
        const key = `${pin.lat.toFixed(2)},${pin.lng.toFixed(2)}`
        if (!acc[key]) acc[key] = { lat: pin.lat, lng: pin.lng, address: pin.address, ids: [] }
        acc[key].ids.push(pin.id)
        return acc
      },
      {}
    )
  ) : []

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 0 }}>
        <span className="sec-title">Geo</span>
        {stats && (
          <span style={{ fontSize: 13, color: 'var(--label3)' }}>
            {stats.countries} {stats.countries === 1 ? 'country' : 'countries'} · {pins?.length ?? 0} geotagged photos
          </span>
        )}
      </div>

      <div className="geo-map-wrap">
        <MapContainer
          center={[48, 15]}
          zoom={3}
          style={{ height: '100%', width: '100%' }}
        >
          <TileLayer
            url="https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png"
            attribution="© OpenStreetMap © CARTO"
            maxZoom={19}
          />
          {grouped.map((group, i) => (
            <CircleMarker
              key={i}
              center={[group.lat, group.lng]}
              radius={8 + Math.sqrt(group.ids.length)}
              pathOptions={{
                fillColor: '#90caf9',
                color: 'rgba(144,202,249,.3)',
                weight: 6,
                fillOpacity: 0.9,
              }}
              eventHandlers={{
                click: () => group.ids[0] && open(group.ids[0]),
              }}
            >
              <Tooltip>
                <strong>{group.address ?? 'Unknown location'}</strong>
                <br />{group.ids.length} photo{group.ids.length !== 1 ? 's' : ''}
              </Tooltip>
            </CircleMarker>
          ))}
        </MapContainer>
      </div>
    </div>
  )
}
