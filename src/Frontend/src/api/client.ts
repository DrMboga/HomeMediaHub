import axios from 'axios'

const client = axios.create({ baseURL: '/api' })

client.interceptors.request.use(cfg => {
  const token = localStorage.getItem('accessToken')
  if (token) cfg.headers.Authorization = `Bearer ${token}`
  return cfg
})

let refreshing = false
type Resolver = (token: string) => void
let queue: Resolver[] = []

client.interceptors.response.use(
  r => r,
  async err => {
    const original = err.config as typeof err.config & { _retry?: boolean }
    if (err.response?.status !== 401 || original._retry) return Promise.reject(err)

    if (refreshing) {
      return new Promise<string>(resolve => queue.push(resolve)).then(token => {
        original.headers.Authorization = `Bearer ${token}`
        return client(original)
      })
    }

    original._retry = true
    refreshing = true

    try {
      const rt = localStorage.getItem('refreshToken')
      if (!rt) throw new Error('no refresh token')
      const { data } = await axios.post('/api/auth/refresh', { refreshToken: rt })
      localStorage.setItem('accessToken', data.accessToken)
      localStorage.setItem('refreshToken', data.refreshToken)
      queue.forEach(r => r(data.accessToken))
      queue = []
      original.headers.Authorization = `Bearer ${data.accessToken}`
      return client(original)
    } catch {
      localStorage.removeItem('accessToken')
      localStorage.removeItem('refreshToken')
      window.location.replace('/login')
      return Promise.reject(err)
    } finally {
      refreshing = false
    }
  }
)

export default client
