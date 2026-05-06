import { createContext, useCallback, useContext, useState } from 'react'

interface LightboxState {
  mediaId: string | null
  open: (id: string) => void
  close: () => void
}

const LightboxContext = createContext<LightboxState>({
  mediaId: null,
  open: () => {},
  close: () => {},
})

export function LightboxProvider({ children }: { children: React.ReactNode }) {
  const [mediaId, setMediaId] = useState<string | null>(null)
  const open = useCallback((id: string) => setMediaId(id), [])
  const close = useCallback(() => setMediaId(null), [])

  return (
    <LightboxContext.Provider value={{ mediaId, open, close }}>
      {children}
    </LightboxContext.Provider>
  )
}

export function useLightbox() {
  return useContext(LightboxContext)
}
