import { createContext, useCallback, useContext, useMemo, useState } from 'react'

interface LightboxState {
  mediaId: string | null
  hasPrev: boolean
  hasNext: boolean
  open: (id: string, ids?: string[]) => void
  close: () => void
  prev: () => void
  next: () => void
}

const LightboxContext = createContext<LightboxState>({
  mediaId: null,
  hasPrev: false,
  hasNext: false,
  open: () => {},
  close: () => {},
  prev: () => {},
  next: () => {},
})

const NO_IDS: string[] = []

export function LightboxProvider({ children }: { children: React.ReactNode }) {
  const [mediaId, setMediaId] = useState<string | null>(null)
  // The ordered ids of the list the lightbox was opened from — what was on screen at that
  // moment, so stepping stops at the end of the current page. Empty when the opener has no
  // list (a map marker, the Photo of the Day tile): then there is nothing to step through.
  const [ids, setIds] = useState<string[]>(NO_IDS)

  const open = useCallback((id: string, list?: string[]) => {
    setIds(list ?? NO_IDS)
    setMediaId(id)
  }, [])
  const close = useCallback(() => setMediaId(null), [])

  const index = mediaId ? ids.indexOf(mediaId) : -1
  const prevId = index > 0 ? ids[index - 1] : null
  const nextId = index >= 0 && index < ids.length - 1 ? ids[index + 1] : null

  const prev = useCallback(() => { if (prevId) setMediaId(prevId) }, [prevId])
  const next = useCallback(() => { if (nextId) setMediaId(nextId) }, [nextId])

  const value = useMemo(
    () => ({ mediaId, hasPrev: !!prevId, hasNext: !!nextId, open, close, prev, next }),
    [mediaId, prevId, nextId, open, close, prev, next],
  )

  return (
    <LightboxContext.Provider value={value}>
      {children}
    </LightboxContext.Provider>
  )
}

export function useLightbox() {
  return useContext(LightboxContext)
}

const LightboxListContext = createContext<string[] | undefined>(undefined)

/** Wraps a grid of PhotoCards so each card opens the lightbox with the whole grid as its
    previous/next sequence. Pass the ids in display order. */
export function LightboxList({ ids, children }: { ids: string[]; children: React.ReactNode }) {
  return <LightboxListContext.Provider value={ids}>{children}</LightboxListContext.Provider>
}

export function useLightboxList() {
  return useContext(LightboxListContext)
}
