import { NavLink, useNavigate } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

export function Nav() {
  const { username, logout } = useAuth()
  const navigate = useNavigate()

  const handleLogout = async () => {
    await logout()
    navigate('/login')
  }

  return (
    <nav className="nav">
      <NavLink to="/" className="nav-brand">HomeMediaHub</NavLink>
      <div className="nav-links">
        <NavLink to="/"        end className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">home</span>Home
        </NavLink>
        <NavLink to="/on-this-day" className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">today</span>On This Day
        </NavLink>
        <NavLink to="/gallery" className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">photo_library</span>Gallery
        </NavLink>
        <NavLink to="/geo"     className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">map</span>Geo
        </NavLink>
        <NavLink to="/search"  className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">search</span>Search
        </NavLink>
        <NavLink to="/albums"  className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">collections</span>Albums
        </NavLink>
        <NavLink to="/videos"  className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">videocam</span>Videos
        </NavLink>
        <NavLink to="/review"  className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}>
          <span className="mi">rate_review</span>Review
        </NavLink>
      </div>
      <div className="nav-user">
        <div className="nav-avatar" onClick={handleLogout} title="Logout">
          {username?.[0]?.toUpperCase() ?? 'M'}
        </div>
        {username}
      </div>
    </nav>
  )
}
