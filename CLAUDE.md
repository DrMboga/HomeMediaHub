# HomeMediaHub

Self-hosted home photo and video library. Replaces manual iPhone → MacBook → USB drive workflow with automated ingestion, AI tagging, and semantic search. Runs on a single Dell Precision 3620 (Ubuntu Server 24.04) as a Docker Compose stack.

## Current Status

**Phase 6 complete** (2026-05-06). React/TypeScript frontend live at `src/Frontend/` (Vite + React Router + TanStack Query + react-leaflet). All 7 pages wired to the real API: Dashboard (POTD, OTD strip, library stats), Gallery (paginated grid, year/month nav), On This Day, Geo (Leaflet dark map with pins), Search (semantic), Albums, Videos. Lightbox with zoom, keyboard shortcuts, Info/Map tab. Three new API endpoints added: `/api/media/stats`, `/api/media/on-this-day`, `/api/media/geo`. Thumbnail, original, and stream endpoints made public (JWT cannot be sent from `<img>`/`<video>` tags; GUID URLs are unguessable on a LAN-only app). `docker-compose.yml` frontend service updated to build from `src/Frontend/Dockerfile`.

**Phase 6 prototype complete** (2026-05-06). Frontend UI prototype in `prototype/` (`index.html` + `server.js`, served at `http://localhost:3333`). Covers all pages: Dashboard (POTD, OTD strip, library stats), Gallery (square grid, year dropdown, month breadcrumb, 50/page paged), On This Day, Geo (Leaflet dark map), Search (semantic + tag + date + country), Albums, Videos. Lightbox: always bottom-bar layout, full-size originals from TestPhoto, video playback, zoom, map tab. Prototype is the React implementation spec.

**Phase 7 complete** (2026-05-06). All Phase 6 validation bugs fixed:
- **Semantic search 500**: AI sidecar (`src/AiSidecar/main.py`) was missing the `POST /embed/text` endpoint. Added it; promoted `_tokenizer` to module level.
- **Lightbox — file path tooltip**: Date `<span>` wrapped in `<abbr title={detail.filePath}>` so hovering shows the full path.
- **Lightbox — map tab covers close button**: `.lb-close` z-index raised to `700` (above Leaflet's internal ~400–600).
- **Gallery state resets on nav**: `GalleryPage` rewritten to use `useSearchParams` (URL-backed state, bookmarkable). `sessionStorage` fallback ensures clicking the nav link (which goes to `/gallery` with no params) still restores the last-visited year/month. "Undated" sentinel year (`0`) added to show `WHERE taken_at IS NULL` photos.
- **Videos pagination broken**: Server-side `isVideo` filter added to `GetMediaListRequest` / `GetMediaListHandler` (`.Where(f => f.DurationSec.HasValue)`). `VideosPage` passes `isVideo: true`; uses `useSearchParams` for page state. Total/page count now correct.
- **Old AVI/Canon MIME type**: `DetectMimeType` in `ExtractMetadataHandler` now maps `.avi` → `video/x-msvideo`, `.mkv` → `video/x-matroska`. Both extensions added to `VideoExtensions`.
- **`taken_at` fallback (images)**: `DateTimeOriginal` → `DateTimeDigitized` → `LastWriteTimeUtc` (mtime). No path-parsing heuristics.
- **`taken_at` fallback (videos)**: ExifTool `DateTimeOriginal` → `CreateDate` → `LastWriteTimeUtc` (mtime).
- **POTD widget**: Now uses On This Day data with a stable day-based index (`(day + month*31) % count`) so it shows a historically meaningful photo. Falls back to most-recent only when no OTD data exists. Both queries include `from=2000-01-01` to exclude null-date items.

**Operational note — file copy to server**: The `taken_at` mtime fallback only works if files are copied with mtime preservation. Use `rsync -av --times` or `scp -p` when copying photos to the server. Plain `scp` (without `-p`) sets mtime to the copy time, giving wrong dates for files without EXIF.

**Operational note — Docker bind mounts**: If a new directory is created on the host while a container is running (e.g. a new `NotIndexed/` subfolder in the photo library), the container may not see it due to a stale mount. Run `docker compose --env-file .env.stage up -d --force-recreate <service>` to reload the mount.

**Phase 5 validated** (2026-05-03). Frontend API + Auth: JWT login/refresh/logout, token refresh (sliding window), logout, all media/search/albums/series/admin endpoints. Fixes applied during validation: `AddRefreshTokens` migration applied manually (class not compiled into image — root cause unresolved); `page`/`pageSize` query params given defaults to fix 400s; date filter forced UTC kind to fix Npgsql `timestamptz` rejection; semantic search path renamed to `/api/search/semantic`; tag search param renamed from `tag` to `q`. All 64 unit tests pass.

**W4 validated** (2026-04-30). Error Handler workflow active: Global Error Trigger → extracts filePath/source from W1 run data → `POST /api/tools/record-failure` → `processing_jobs` table. Configure per-workflow in W1/W2 settings → Error Workflow → W4. Known issue: `file_path` always `unknown` in error records — W4 Code: Extract can't locate filePath in execution run data structure.

**W2 concurrency fix** (2026-04-30). Root cause of intermittent API "connection closed" errors: n8n HTTP Request node fires all items concurrently by default — 281 simultaneous W1 executions caused .NET thread pool starvation. Fixed by adding `batching: { batchSize: 4, batchInterval: 500 }` to W2's HTTP: Process File node. Validated: 2 × 281-file runs with 0 errors.

**Phase 4 validated** (2026-04-30). AI Sidecar deployed: YOLOv8n object detection + CLIP ViT-B/32 512-dim embeddings + origin classification (photo/screenshot/document). W1 pipeline extended with `HTTP: AI Analyze` step. API persists tags, embeddings (EF Core + `Pgvector`), and `origin_class/confidence`. All 281 stage files processed with 0 errors, all 281 embeddings saved.

## Server Access

| | |
|--|--|
| SSH | `ssh mike@192.168.0.192` |
| IP | `192.168.0.192` (static DHCP — reserved by MAC address in home router) |
| App directory | `/opt/homemediahub/app` |

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700, 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | USB HDD 500 GB — mounted at `/mnt/usb-hdd` |
| Network | LAN-only (no public internet exposure) |

## Stack

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (MIT) — iPhone backup only, no Immich UI used |
| Orchestration | n8n — all multi-step processing workflows |
| API | .NET 10 ASP.NET Core |
| Frontend | React + TypeScript |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n (object detection) + CLIP ViT-B/32 (semantic embeddings) |
| Geocoding | .NET 10 — standalone geoservice microservice (Nominatim cache) |
| Media processing | ffmpeg, libheif + libde265 (HEIC/HEVC), ExifTool, SixLabors.ImageSharp |
| Reverse proxy | NGINX |
| Notifications | Telegram Bot API (called from n8n) |
| File sharing | Samba (native Ubuntu, not containerized) |
| Containerization | Docker Compose (single host) |

## Service Ports

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 (443 future) | Single public ingress |
| api | 8080 | Internal only |
| frontend | 3000 | Internal only |
| n8n | 5678 | Admin only — restricted by nginx IP filter |
| ai-sidecar | 8000 | Internal only |
| geoservice | 8001 | Internal only — reverse geocoding cache |
| postgres | 5432 | Internal only — hosts both `homemediahub_db` and `n8n_db` |
| immich-server | 2283 | iPhone app connects here |
| immich-postgres | 5433 | Internal only |
| geoservice-postgres | 5434 | Internal only — hosts `geoservice_db` |
| redis | 6379 | Internal only — Immich only |

## Databases

| Database | Host | Purpose |
|----------|------|---------|
| `homemediahub_db` | postgres:5432 | Application data — media, tags, embeddings, users |
| `n8n_db` | postgres:5432 | n8n internal — workflow definitions, execution history, credentials |
| Immich DB | immich-postgres:5433 | Immich internal — not accessed by custom services |
| `geoservice_db` | geoservice-postgres:5434 | Geoservice cache — reverse geocoding results keyed by rounded lat/lon |

## Environments

Two isolated environments run from the same `docker-compose.yml` via separate env files. Only one runs at a time (they share ports 80, 8080, etc.).

| Environment | Command | Photo library | Data root |
|-------------|---------|---------------|-----------|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/usb-hdd/Photo` (USB HDD) | `/opt/homemediahub/data/` |

Stage uses the `TestPhoto` folder (copied from Windows laptop to server SSD) so the USB HDD is not required during development. Switch to prod when the USB HDD is attached and the full library is ready.

## Key Paths on Disk

### Stage (SSD)
```
/opt/homemediahub/stage/
├── TestPhoto/            ← small curated test library (copied from Windows laptop)
│   └── iPhone/          ← Immich uploads in stage
├── thumbnails/           ← generated, recreatable
├── video-cache/          ← H.264 transcodes, recreatable
├── display-cache/        ← JPEG conversions of HEIC originals, recreatable
└── data/
    ├── postgres/           ← stage Postgres data volume
    ├── immich-postgres/    ← stage Immich Postgres data volume
    ├── geoservice-postgres/ ← stage GeoService Postgres data volume
    └── n8n/                ← stage n8n data volume

/tmp/homemediahub-stage/  ← shared temp (stage)
```

### Prod (USB HDD)
```
/mnt/usb-hdd/
├── Photo/               ← single source of truth for all originals
│   ├── 2005/ ... 2025/  ← existing archive (not reorganised)
│   └── iPhone/          ← new uploads from Immich (storage template: {y}/{y-MM-dd}/)
├── thumbnails/          ← 400 px WebP, recreatable from originals
├── video-cache/         ← H.264 MP4 transcodes of iPhone HEVC videos, recreatable
└── display-cache/       ← JPEG conversions of HEIC originals, recreatable

/opt/homemediahub/data/
├── postgres/              ← prod Postgres data volume
├── immich-postgres/       ← prod Immich Postgres data volume
├── geoservice-postgres/   ← prod GeoService Postgres data volume
└── n8n/                   ← prod n8n data volume

/tmp/homemediahub-prod/  ← shared temp (prod)
```

## Samba Share

`\\<server-ip>\Photo` maps to `/mnt/usb-hdd/Photo`. Read-write for `mediahub` group.
- Windows: `\\<server-ip>\Photo`
- macOS: Finder → Go → Connect to Server → `smb://<server-ip>/Photo`

## n8n Workflows

| # | Name | Trigger | Purpose |
|---|------|---------|---------|
| 1 | Process Single File | Webhook / sub-workflow call | Core pipeline: hash → dedup → convert → metadata → thumbnail → geocode → AI → DB |
| 2 | Library Scanner | Manual button OR monthly cron (`0 2 1 * *`) | Scans `/mnt/usb-hdd/Photo`, finds unprocessed files, calls W1 for each |
| 3 | Photo of the Day | Daily cron `0 15 * * *` | Picks a notable photo from DB, sends to Telegram channel |
| 4 | Error Handler | Global n8n error trigger | Updates `processing_jobs` table, sends Telegram alert to admin |

**Ingest strategy:** No Immich webhooks. Immich writes files to disk; Library Scanner (W2) is the only ingest entry point. Run manually after large imports; monthly cron catches iPhone backups automatically.

## Docker Compose Commands

Always pass `--env-file` to target the right environment. Omitting it defaults to `.env` which may not exist.

```bash
# Start stage (TestPhoto on SSD)
docker compose --env-file .env.stage up -d

# Start prod (USB HDD — ensure drive is mounted first)
docker compose --env-file .env.prod up -d

# View logs (same flag required)
docker compose --env-file .env.stage logs -f api
docker compose --env-file .env.stage logs -f n8n

# Restart a single service
docker compose --env-file .env.stage restart api

# Stop an environment
docker compose --env-file .env.stage down

# Rebuild custom images after code changes
docker compose --env-file .env.stage build api
docker compose --env-file .env.stage build ai-sidecar
docker compose --env-file .env.stage build n8n
docker compose --env-file .env.stage build geoservice

# Run a one-off Postgres command (stage example)
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db
```

## Authentication

ASP.NET Core Identity. Two users: Mike and Badger. No roles — both users share the full library with equal access. JWT (15 min expiry) + refresh token (30-day sliding window).

## Design Decisions

- **EF Core over raw SQL.** Use EF Core entity navigation and `SaveChangesAsync` for all persistence. Raw SQL (`ExecuteSqlRawAsync` / `ExecuteSqlAsync` string concatenation) is forbidden — use proper type mappings instead (e.g. `Pgvector.Vector` with `HasColumnType("vector(512)")` for embeddings, standard EF navigation for relationships). Only reach for raw SQL when EF genuinely cannot express the operation (e.g. bulk operations with `ExecuteUpdateAsync`/`ExecuteDeleteAsync` are fine).
- **n8n is the orchestrator.** The .NET API contains no background workers, no Quartz jobs, no multi-step logic. n8n owns all workflow execution.
- **Tool endpoints pattern.** n8n calls small atomic .NET endpoints for operations it cannot perform natively: SHA-256 hashing, EXIF extraction, thumbnail generation, directory scanning.
- **HEIC/video conversion in n8n.** HEIC conversion is a two-step Execute Command node: `heif-convert -q 90` converts pixels, then `exiftool -TagsFromFile` copies all EXIF/GPS/XMP metadata from the original — `heif-convert` alone strips metadata. `ffmpeg` handles video frame extraction. Both run as Execute Command nodes in n8n (installed in the custom n8n Alpine image via `apk add libheif-tools libde265 perl-image-exiftool ffmpeg`). Converted temp files go to `/tmp/homemediahub-work/` (shared with the api container).
- **Thumbnails on filesystem.** nginx serves thumbnails directly via `X-Accel-Redirect` — no Postgres or .NET involved per thumbnail request.
- **Video on-demand transcoding.** iPhone HEVC videos are transcoded to H.264 MP4 on first play request by the .NET API. Cached in `/mnt/usb-hdd/video-cache/`. Original is never modified.
- **Single storage tree.** All originals (archive + new iPhone uploads) live under `/mnt/usb-hdd/Photo/`. No duplication. Immich's storage template writes new uploads to `Photo/iPhone/{y}/{y-MM-dd}/`.
- **Separate Postgres instances.** Immich gets its own Postgres (port 5433). The custom stack uses a separate container (port 5432) with two databases: `homemediahub_db` and `n8n_db`. GeoService gets its own Postgres (port 5434) so it is fully self-contained and portable to other projects.
- **GeoService is a standalone microservice.** Reverse geocoding is extracted into its own .NET 10 service (`geoservice:8001`) with its own database. n8n calls `GET geoservice:8001/reverse?lat=&lon=` instead of Nominatim directly. The service looks up coordinates (rounded to 4 decimal places, ~11 m precision) in its local cache first; only on a cache miss does it call Nominatim, then persists the result. Nominatim's 1 req/sec policy is enforced inside the service.
- **Samba native, not containerised.** Running Samba in a container requires `--privileged` and `--network host`, eliminating isolation benefits.
- **All AI runs locally.** No cloud ML APIs. YOLOv8n for object detection, CLIP ViT-B/32 for semantic embeddings and natural-language search. Face recognition is explicitly out of scope.

## Out of Scope

- Face / people recognition
- Remote access (LAN only)
- Web browser upload (copy to Samba share + run Library Scanner)
- RAW camera files (CR2, ARW, NEF) — iOS devices only
- Per-user library separation — shared pool, both users see everything

## Prior Art

Previous implementation in `C:/Users/Mike/Projects/PhotoLibrary` — ran on Raspberry Pi 4 with .NET 10, React, Postgres, ML.NET. Key lessons incorporated here: SignalR for real-time progress, EXIF via MetadataExtractor, geocoding via Nominatim, HEIC conversion via libheif/ffmpeg. Key problems it had: no iPhone auto-sync, poor ML.NET tagging results, no semantic search.
