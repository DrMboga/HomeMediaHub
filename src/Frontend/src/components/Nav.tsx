import { NavLink, useNavigate } from 'react-router-dom'
import { useAuth } from '../context/AuthContext'

const LINKS: [string, string, boolean?][] = [
  ['/', 'Home', true],
  ['/on-this-day', 'On This Day'],
  ['/gallery', 'Gallery'],
  ['/geo', 'Geo'],
  ['/search', 'Search'],
  ['/tags', 'Tags'],
  ['/albums', 'Albums'],
  ['/videos', 'Videos'],
  ['/review', 'Review'],
  ['/admin', 'Admin'],
]

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
        {LINKS.map(([to, label, end]) => (
          <NavLink
            key={to}
            to={to}
            end={end}
            className={({ isActive }) => `nav-link${isActive ? ' active' : ''}`}
          >
            {label}
          </NavLink>
        ))}
      </div>
      <div className="nav-user">
        <div className="nav-avatar" onClick={handleLogout} title="Log out">
          {username?.[0]?.toUpperCase() ?? 'M'}
        </div>
        <span className="nav-username">{username}</span>
      </div>
    </nav>
  )
}
