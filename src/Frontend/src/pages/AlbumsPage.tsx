import { useQuery } from '@tanstack/react-query'
import { getAlbums, getAlbumDetail } from '../api/media'
import { PhotoCard } from '../components/PhotoCard'
import { LightboxList } from '../context/LightboxContext'
import { useState } from 'react'

function AlbumDetail({ id, onBack }: { id: string; onBack: () => void }) {
  const { data } = useQuery({
    queryKey: ['album', id],
    queryFn: () => getAlbumDetail(id),
    staleTime: 30_000,
  })

  return (
    <div className="page">
      <div className="gallery-nav">
        <button
          className="pager-btn"
          onClick={onBack}
          style={{ marginLeft: 0 }}
        >
          <span className="mi" style={{ fontSize: 16 }}>arrow_back</span>Albums
        </button>
        <span className="gallery-title">{data?.name ?? '…'}</span>
      </div>

      {data?.description && (
        <p style={{ color: 'var(--label2)', fontSize: 14 }}>{data.description}</p>
      )}

      {data && data.media.length === 0 && (
        <div className="empty-state">
          <span className="mi">collections</span>
          <span>No photos in this album</span>
        </div>
      )}

      {data && data.media.length > 0 && (
        <LightboxList ids={data.media.map(m => m.id)}>
          <div className="gallery-grid">
            {data.media.map(item => (
              <PhotoCard key={item.id} item={item} className="gallery-card" />
            ))}
          </div>
        </LightboxList>
      )}
    </div>
  )
}

export function AlbumsPage() {
  const [openAlbumId, setOpenAlbumId] = useState<string | null>(null)

  const { data: albums, isLoading } = useQuery({
    queryKey: ['albums'],
    queryFn: getAlbums,
    staleTime: 30_000,
  })

  if (openAlbumId) {
    return <AlbumDetail id={openAlbumId} onBack={() => setOpenAlbumId(null)} />
  }

  return (
    <div className="page">
      <div className="sec-head" style={{ marginBottom: 0 }}>
        <span className="sec-title">Albums</span>
      </div>

      {isLoading && <div className="loading">Loading…</div>}

      {!isLoading && albums?.length === 0 && (
        <div className="empty-state">
          <span className="mi">collections</span>
          <span>No albums yet</span>
        </div>
      )}

      {albums && albums.length > 0 && (
        <div className="albums-grid">
          {albums.map(album => (
            <div
              key={album.id}
              className="album-card"
              onClick={() => setOpenAlbumId(album.id)}
            >
              <div className="album-cover">
                <div className="album-cover-placeholder">
                  <span className="mi">photo_library</span>
                </div>
              </div>
              <div className="album-info">
                <div className="album-name">{album.name}</div>
                <div className="album-count">{album.mediaCount} photo{album.mediaCount !== 1 ? 's' : ''}</div>
              </div>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
