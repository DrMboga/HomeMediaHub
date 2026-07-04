# HomeMediaHub — Design System ("Darkroom Archive")

The living brief. **Read this before touching UI.** Designed in claude.ai/design (project
"HomeMediaHubDesign"), exported to `design-kit/HomeMediaHubDesign/`, implemented here in
vanilla CSS tokens. Every color/type decision derives from `src/styles/tokens.css`.

## Direction

The photos are the color; the interface is the darkroom around them. Warm near-black,
ivory text, a single amber "safelight" accent used **only where the user is acting**
(active nav link, focused input, selected chip, checked item). Metadata is set in dim
monospace like contact-sheet edge markings — that is the signature element.

## Tokens (source of truth: `tokens.css`)

| Token | Value | Role |
|---|---|---|
| `--bg` | `#0c0a08` | page — warm darkroom black |
| `--surface` | `#171210` | nav, cards, lightbox chrome, panels |
| `--surface-2` | `#1a1512` | selected/hover surfaces (e.g. review toolbar) |
| `--ink` | `#ece4d4` | primary text — warm ivory, never pure white |
| `--ink-70/55/45/42/30/28` | `rgba(236,228,212,·)` | text/opacity steps |
| `--accent` | `#D98E2B` | the safelight — active/interactive only |
| `--danger` | `#d9736a` | delete / review-meme (muted brick) |
| `--line` | `rgba(236,228,212,0.1)` | hairline rules (also `-08`, `-18` variants) |

Type roles — self-hosted via `@fontsource`, no CDN:
- `--font-display` **Newsreader** (serif) — month headers, page titles, wordmark, stat numbers. Large, sparing.
- `--font-body` **IBM Plex Sans** — nav links, buttons, inputs.
- `--font-mono` **IBM Plex Mono** — all data: dates, dims, filenames, counts, EXIF, eyebrows, chips. Letterspaced, dim, `·` separated.

## Rules

- **Nav is text-only** (no icons), serif wordmark left, mono `MIKE` + ringed initial right.
- Section headers = mono eyebrow (`KYIV · 342 PHOTOS · 12 VIDEOS`) above a Newsreader title.
- Separation via hairlines + `--surface` steps, **not** box-shadows.
- Photo tiles: 4px gaps, 1:1 (16:9 for videos), amber 1px inset outline on hover/selected.
- Icons: `lucide-react` only where a glyph beats a word (chevrons, close, ±, trash, play, pin, search). Never an icon font.
- Motion: `settle` fade-in on grids (respect `prefers-reduced-motion`); otherwise quiet 150ms.

## Rollout status

- [x] Design approved in canvas (all 10 frames), exported.
- [x] Foundation (tokens/fonts) + pilot (Nav, Gallery, PhotoCard, Lightbox) — verified on live data, approved.
- [ ] Propagate: Dashboard → Search → On This Day → Albums → Videos → Geo → Review → Login.
- [ ] Cleanup: drop Material Icons, legacy vars, dead CSS; `prefers-reduced-motion`; 390px floor.

Note: photo tiles show date **and** time (mono, `4 Jan 2025 · 10:03`), except fallback
dates (midnight UTC) which stay date-only — see `formatCardDate` in PhotoCard.tsx.

## Decisions log

- Serif is **Newsreader**, not Fraunces (the canvas tool chose it; it reads warmer/older, fits the archive).
- Body is **IBM Plex Sans** (pairs with Plex Mono at the same optical rhythm) rather than Public Sans.
- Accent locked to `#D98E2B` (canvas offered `#E0A04A`/`#C9A15E`; the mid amber is the safelight).
- Nav dropped its Material icons entirely — text-only reads quieter and less like an admin panel.
