import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Dev proxy target — the server running the Docker stack
const API_HOST = 'http://192.168.0.192'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    proxy: {
      '/api': { target: API_HOST, changeOrigin: true },
    },
  },
})
