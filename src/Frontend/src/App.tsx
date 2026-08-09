import { Navigate, Route, Routes } from 'react-router-dom'
import { useAuth } from './context/AuthContext'
import { Nav } from './components/Nav'
import { Lightbox } from './components/Lightbox'
import { LoginPage } from './pages/LoginPage'
import { DashboardPage } from './pages/DashboardPage'
import { GalleryPage } from './pages/GalleryPage'
import { OnThisDayPage } from './pages/OnThisDayPage'
import { GeoPage } from './pages/GeoPage'
import { SearchPage } from './pages/SearchPage'
import { TagsPage } from './pages/TagsPage'
import { AlbumsPage } from './pages/AlbumsPage'
import { VideosPage } from './pages/VideosPage'
import { ReviewPage } from './pages/ReviewPage'
import { AdminPage } from './pages/AdminPage'

function ProtectedLayout() {
  const { isLoggedIn } = useAuth()
  if (!isLoggedIn) return <Navigate to="/login" replace />
  return (
    <>
      <Nav />
      <Routes>
        <Route path="/"           element={<DashboardPage />} />
        <Route path="/gallery"    element={<GalleryPage />} />
        <Route path="/on-this-day" element={<OnThisDayPage />} />
        <Route path="/geo"        element={<GeoPage />} />
        <Route path="/search"     element={<SearchPage />} />
        <Route path="/tags"       element={<TagsPage />} />
        <Route path="/albums"     element={<AlbumsPage />} />
        <Route path="/videos"     element={<VideosPage />} />
        <Route path="/review"     element={<ReviewPage />} />
        <Route path="/admin"      element={<AdminPage />} />
        <Route path="*"           element={<Navigate to="/" replace />} />
      </Routes>
      <Lightbox />
    </>
  )
}

function LoginRoute() {
  const { isLoggedIn } = useAuth()
  if (isLoggedIn) return <Navigate to="/" replace />
  return <LoginPage />
}

export function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginRoute />} />
      <Route path="/*"     element={<ProtectedLayout />} />
    </Routes>
  )
}
