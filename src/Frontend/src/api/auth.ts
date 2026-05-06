import axios from 'axios'
import type { AuthResponse } from '../types/api'

export async function login(username: string, password: string): Promise<AuthResponse> {
  const { data } = await axios.post<AuthResponse>('/api/auth/login', { username, password })
  return data
}

export async function logout(): Promise<void> {
  const token = localStorage.getItem('accessToken')
  if (token) {
    await axios.post('/api/auth/logout', null, {
      headers: { Authorization: `Bearer ${token}` },
    }).catch(() => {})
  }
  localStorage.removeItem('accessToken')
  localStorage.removeItem('refreshToken')
}
