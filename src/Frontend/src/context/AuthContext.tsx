import { createContext, useCallback, useContext, useState } from 'react'
import { login as apiLogin, logout as apiLogout } from '../api/auth'

interface AuthState {
  username: string | null
  isLoggedIn: boolean
  login: (username: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthState>({
  username: null,
  isLoggedIn: false,
  login: async () => {},
  logout: async () => {},
})

function getStoredUsername(): string | null {
  return localStorage.getItem('username')
}

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const [username, setUsername] = useState<string | null>(() => {
    if (!localStorage.getItem('accessToken')) return null
    return getStoredUsername()
  })

  const login = useCallback(async (user: string, password: string) => {
    const resp = await apiLogin(user, password)
    localStorage.setItem('accessToken', resp.accessToken)
    localStorage.setItem('refreshToken', resp.refreshToken)
    localStorage.setItem('username', user)
    setUsername(user)
  }, [])

  const logout = useCallback(async () => {
    await apiLogout()
    localStorage.removeItem('username')
    setUsername(null)
  }, [])

  return (
    <AuthContext.Provider value={{ username, isLoggedIn: !!username, login, logout }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  return useContext(AuthContext)
}
