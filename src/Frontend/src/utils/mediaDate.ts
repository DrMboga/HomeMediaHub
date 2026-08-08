// Formatting for media timestamps (`takenAt`, `stats.latestDate`).
//
// `taken_at` holds the photo's *wall-clock* time as written by the camera. EXIF
// DateTimeOriginal carries no zone, and the worker stores it verbatim (the container runs
// UTC, so MetadataService's ToUniversalTime() is a no-op). Reading it back with the
// browser's local getters therefore shifts every photo by the viewer's own offset — that
// is the +2h Mike measured on stage, not a parser fault. See docs/delivery-plan.md,
// "Stage validation backlog — 2026-08-08" item 2.
//
// Everything here reads and formats in UTC so a photo shows the time the camera recorded,
// on any machine. Dates that genuinely mean "now" — today's date on On This Day, the
// Gallery year/month pickers — are local and must NOT use these helpers.

const UTC_DATE = { timeZone: 'UTC' } as const

/**
 * Exactly midnight UTC means the date came from a path-year fallback rather than real
 * EXIF, so callers show the date alone instead of a misleading 00:00.
 */
export function isDateOnly(d: Date): boolean {
  return d.getUTCHours() === 0 && d.getUTCMinutes() === 0 &&
    d.getUTCSeconds() === 0 && d.getUTCMilliseconds() === 0
}

function utcTime(d: Date): string {
  const hh = String(d.getUTCHours()).padStart(2, '0')
  const mm = String(d.getUTCMinutes()).padStart(2, '0')
  return `${hh}:${mm}`
}

/** Photo card caption — "14 Jul 2007 · 10:17". */
export function formatGridDate(takenAt: string | null): string {
  if (!takenAt) return ''
  const d = new Date(takenAt)
  const datePart = d.toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', ...UTC_DATE })
  return isDateOnly(d) ? datePart : `${datePart} · ${utcTime(d)}`
}

/** Lightbox metadata line — "14.07.2007 10:17, Sat". */
export function formatDetailDate(iso: string | null): string {
  if (!iso) return '—'
  const d = new Date(iso)
  const dd = String(d.getUTCDate()).padStart(2, '0')
  const mm = String(d.getUTCMonth() + 1).padStart(2, '0')
  const datePart = `${dd}.${mm}.${d.getUTCFullYear()}`
  const weekday = d.toLocaleDateString('en-US', { weekday: 'short', ...UTC_DATE })
  return isDateOnly(d) ? `${datePart}, ${weekday}` : `${datePart} ${utcTime(d)}, ${weekday}`
}

/** Photo-of-the-Day footer — "Sat, Jul 14, 2007". */
export function formatLongDate(iso: string): string {
  return new Date(iso).toLocaleDateString('en-US',
    { weekday: 'short', month: 'short', day: 'numeric', year: 'numeric', ...UTC_DATE })
}

/** Stats tile — "Jul 2007". */
export function formatMonthYear(iso: string): string {
  return new Date(iso).toLocaleDateString('en-US', { month: 'short', year: 'numeric', ...UTC_DATE })
}

/** Review card caption — "14 Jul 2007", no time. */
export function formatShortDate(takenAt: string | null): string {
  if (!takenAt) return '—'
  return new Date(takenAt).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', year: 'numeric', ...UTC_DATE })
}
