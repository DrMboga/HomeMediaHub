# HomeMediaHub — Phased Delivery Plan

Each phase produces a running, testable increment. Later phases build on earlier ones without requiring rework of completed pieces.

## Development strategy: stage → prod

All phases are validated on the **stage** environment first (TestPhoto folder on server SSD, no USB HDD required). When a phase is stable on stage, it is smoke-tested on **prod** (USB HDD, full Photo library). The same `docker-compose.yml` is used for both; only the env file differs.

| Env | Photo source | How to start |
|-----|-------------|--------------|
| stage | `/opt/homemediahub/stage/TestPhoto` | `docker compose --env-file .env.stage up -d` |
| prod  | `/mnt/usb-hdd/Photo` | `docker compose --env-file .env.prod up -d` |

**TestPhoto setup (one-time):** Create a `TestPhoto` folder on your Windows laptop with a small representative sample (a handful of JPEGs, HEICs, and one short video). Copy it to the server:
```bash
scp -r "C:\Users\Mike\TestPhoto" mike@<server-ip>:/opt/homemediahub/stage/TestPhoto
```

---

## Phase 1 — Infrastructure Skeleton ✅ COMPLETE (2026-04-22)

**Goal:** Docker Compose stack boots; all containers start and can reach each other.

### Notes from implementation

- `immich-postgres` image changed from `postgres:16` to `pgvector/pgvector:pg16` — Immich v2.7.5 requires the vector extension.
- `Dockerfile.n8n` changed from `FROM n8nio/n8n:latest` + `apk add` to `FROM node:20-alpine` + `npm install -g n8n` — the current n8n:latest image ships without a package manager.
- Added loopback port bindings (`127.0.0.1:8080`, `127.0.0.1:8000`, `127.0.0.1:8001`) for api, ai-sidecar, and geoservice so health endpoints are reachable from the server via SSH.
- Added `N8N_SECURE_COOKIE=false` to n8n env — required for HTTP access on a LAN without TLS.
- nginx n8n `proxy_pass` uses trailing slash (`http://n8n/`) to strip the `/n8n/` prefix before forwarding, which is required for n8n's Vite asset paths to resolve correctly.

### Deliverables

- `docker-compose.yml` with all services declared (images, ports, volumes, env vars, depends_on)
- Custom `Dockerfile` stubs for `api`, `ai-sidecar`, `n8n` (minimal — just enough to build)
- PostgreSQL container (`pgvector/pgvector:pg16`) with init script:
  - Creates `homemediahub_db` and `n8n_db`
  - Runs `CREATE EXTENSION IF NOT EXISTS vector`
- Full DB schema applied via init SQL (Section 5 of architecture doc)
- NGINX config with routing rules (all upstreams may return 502 at this point — routing is declared)
- Volume mounts wired: `/mnt/usb-hdd/Photo`, `/mnt/usb-hdd/thumbnails`, `/mnt/usb-hdd/video-cache`, `/tmp/homemediahub-work`
- Immich + its Postgres + Redis declared and wired to iPhone storage path

### Done when (validate on stage first)

```bash
# Start stage
docker compose --env-file .env.stage up -d

# All containers running?
docker compose --env-file .env.stage ps

# Schema applied?
docker compose --env-file .env.stage exec postgres \
    psql -U postgres -d homemediahub_db -c "\dt"
# Expected: 10 tables listed

# Health checks?
curl http://localhost:8080/health   # → {"status":"ok","phase":1}
curl http://localhost:8000/health   # → {"status":"ok","phase":1}
```

---

## Phase 2 — .NET Tool Endpoints + GeoService ✅ VALIDATED (2026-04-24)

**Goal:** n8n can call all four `.NET` tool endpoints and the reverse-geocoding service; each returns correct output against real files.

### Notes from implementation

- `MetadataExtractor` replaced with `SixLabors.ImageSharp` for EXIF extraction — ImageSharp is already a dependency for thumbnail generation, removing a redundant package.
- `ImplicitUsings` and `Nullable` must be declared explicitly in each `.csproj` — the Docker build context (`src/Api`, `src/GeoService`) does not include the root `src/Directory.Build.props`, so MSBuild cannot walk up to find it inside the container.
- Identity tables land in PascalCase (`AspNetUsers`, `AspNetRoles`, etc.) despite `UseSnakeCaseNamingConvention()` — ASP.NET Core Identity sets table names explicitly, and the naming convention plugin does not override explicit names. Functionally correct; `UserManager` uses the EF model names, not raw SQL. Fix in a future migration if consistency with snake_case application tables matters.
- `docker compose down -v` only removes named Docker volumes, not bind mounts. The Postgres data directories (`/opt/homemediahub/stage/data/postgres`, etc.) are bind mounts and survive `down -v`. Wipe them manually when a clean slate is needed.

### Deliverables

**API (`src/Api/`)**
- `.NET 10` Web API project
- ASP.NET Core Identity schema via EF Core migration (users table only — no auth wired yet)
- Seed script: create Mike and Badger accounts
- Four tool endpoints:
  - `POST /api/tools/hash` — SHA-256 via `System.Security.Cryptography`
  - `POST /api/tools/metadata` — EXIF via `SixLabors.ImageSharp`; accepts JPEG only
  - `POST /api/tools/thumbnail` — 400 px WebP via `SixLabors.ImageSharp`; writes to `/thumbnails/<id>.webp`
  - `GET  /api/tools/scan?path=` — walks directory, excludes paths already in `media_files`
- Serilog structured JSON logging (file path, duration_ms, outcome per request)
- Docker image build (`src/Api/Dockerfile`)

**GeoService (`src/GeoService/`)**
- `.NET 10` minimal API project — standalone microservice on port 8001
- `GET /reverse?lat=&lon=` — rounds coordinates to 4 decimal places (~11 m), looks up in local cache first, calls Nominatim on miss, persists result; enforces Nominatim's 1 req/sec policy
- `GET /health` — liveness check
- EF Core with `geoservice_db` on its own Postgres container (port 5434)
- Serilog structured JSON logging (cache hit/miss, Nominatim latency)
- Docker image build (`src/GeoService/Dockerfile`)

### Validation results (stage, 2026-04-24)

| Check | Result |
|---|---|
| `GET /health` (api + geoservice) | ✅ `{"status":"ok","phase":2}` |
| `GET /api/tools/scan?path=/photo` | ✅ returns file list |
| `POST /api/tools/hash` | ✅ SHA-256 hex string |
| `POST /api/tools/metadata` | ✅ EXIF, GPS, camera make/model |
| `POST /api/tools/thumbnail` | ✅ WebP written to `/thumbnails/<id>.webp` |
| `GET /reverse` — cache miss | ✅ `{"address":"...","cached":false}` |
| `GET /reverse` — cache hit | ✅ `{"address":"...","cached":true}` |
| Identity schema migrated | ✅ `identity` schema, 7 ASP.NET tables |
| Users seeded | ✅ `mike` and `badger` in `identity."AspNetUsers"` |

---

## Phase 3 — n8n Ingest Pipeline ✅ VALIDATED (2026-04-28)

**Goal:** Library Scanner finds files and fully processes them through Workflow 1 into the database.

### Notes from implementation

- W1 deployed as a webhook-triggered workflow. W2 calls it via `POST http://n8n:5678/webhook/process-file` (HTTP Request node with `continueOnFail: true`) rather than n8n's Execute Workflow node — each W1 call becomes a discrete execution in n8n history, making failures easier to inspect individually.
- W2 processes files sequentially (one at a time), not in parallel batches of 10 — the HTTP Request node iterates items one at a time. This is simpler, avoids concurrency pressure, and throughput is adequate for the library size.
- EF Core entity classes (`MediaFile`, `MediaMetadata`, `MediaThumbnail`, `MediaTag`) added to the API with `ExcludeFromMigrations()` — tables already exist via init SQL. EF entities enable `SaveChangesAsync` which is compatible with Npgsql's `EnableRetryOnFailure` policy (raw `ExecuteSqlAsync` inside `BeginTransactionAsync` is not retry-compatible).
- Npgsql connection reliability hardened on both API and GeoService: `Keepalive=30;Connection Idle Lifetime=300` in the connection string; `EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: 5 s)` and `CommandTimeout(60)` on each `DbContext`. Docker's bridge network silently drops idle TCP connections via conntrack; without these settings, any connection idle for ~4 minutes fails the next request with "connection closed unexpectedly".
- `Image.LoadAsync` (full pixel decode) replaced with `Image.IdentifyAsync` (header-only) in the metadata handler — avoids memory spikes under concurrent load that caused Kestrel to drop connections.
- ExifTool subprocess capped at 30 seconds with `proc.Kill(entireProcessTree: true)` on cancellation — prevents indefinite hangs on malformed video containers.
- `processing_jobs` table write deferred to Phase 7 (Error Handler workflow). W2's final `Code: Build Summary` node provides interim run observability: total/succeeded/failed counts and a list of failed file paths with error messages.

### Deliverables

- Custom n8n image (`Dockerfile.n8n`): `FROM node:20-alpine` + `npm install -g n8n` + `apk add ffmpeg libheif-tools libde265 perl-image-exiftool`
- n8n configured to use `n8n_db` (`DB_TYPE=postgresdb`)
- **Workflow 1 — Process Single File** (JSON export committed to `n8n/workflows/`)
  - All branches: HEIC, VIDEO (ffprobe + frame extract), JPEG/PNG pass-through
  - hash → dedup check → convert → metadata → thumbnail → geocode (continue-on-fail) → DB insert
  - `processing_jobs` written at start; updated on completion or failure
  - Shared-temp cleanup after conversion
- **Workflow 2 — Library Scanner** (JSON export committed)
  - Calls `GET /api/tools/scan`, splits into batches of 10, calls W1 per file
  - Trigger: Manual button + monthly cron `0 2 1 * *`
- n8n Nominatim geocoding: 1 req/sec rate limit enforced via Wait node (1 000 ms)
- HEIC conversion is a two-command Execute Command node: `heif-convert -q 90` then `exiftool -TagsFromFile` to copy EXIF/GPS from the original — `heif-convert` alone does not preserve metadata

### Done when

1. Drop a test HEIC and JPEG into `/mnt/usb-hdd/Photo/test/`
2. Trigger Library Scanner manually
3. Confirm rows appear in `media_files`, `media_metadata`, `media_thumbnails`; `media_tags` will be empty until Phase 4 (AI not wired yet)

### Validation results (stage, 2026-04-28)

| Check | Result |
|---|---|
| W2 Manual Trigger → scan → W1 per file | ✅ 281 files processed |
| JPEG pipeline (hash → dedup → metadata → thumbnail → geocode → save) | ✅ |
| HEIC pipeline (heif-convert → exiftool TagsFromFile → metadata → thumbnail → geocode → save) | ✅ |
| MOV/video pipeline (ffmpeg frame extract → metadata → thumbnail → geocode → save) | ✅ |
| Dedup check (re-run W2 on already-processed library — 0 new inserts) | ✅ |
| Files with GPS: address populated via geoservice | ✅ |
| Files without GPS: lat/lon/address null | ✅ |
| W2 Code: Build Summary output | ✅ `{ total: 281, succeeded: 281, failed: 0, failedFiles: [] }` |

---

## Phase 4 — AI Sidecar

**Goal:** Every ingested photo receives object-detection tags and a CLIP embedding.

### Deliverables

- Python 3.11 FastAPI service (`src/AiSidecar/`)
- Models baked into image at build time:
  - YOLOv8n (`ultralytics` — 6 MB)
  - CLIP ViT-B/32 (`openai/clip-vit-base-patch32` via `transformers` or `clip` package — 151 MB)
- Endpoints:
  - `POST /analyze { filePath }` → `{ tags: [{label, confidence}], embedding: [512], originClass, originConfidence }` — combined YOLOv8 + CLIP in a single call
  - `POST /embed/text { text }` → `{ embedding: [512] }`
- Confidence threshold: ≥ 0.40 for YOLOv8 tags
- L2-normalised CLIP embeddings
- Origin classification: CLIP zero-shot across 6 classes — `photo`, `screenshot`, `document`, `artwork`, `reference`, `people`. `meme` is user-assigned only (never set by AI).
- Workflow 1 calls `/analyze` (continue-on-fail); tags, embedding, and `origin_class`/`origin_confidence` all written in one step

### Done when

```bash
curl -s -X POST http://localhost:8000/process \
     -H 'Content-Type: application/json' \
     -d '{"filePath":"/photo/2005/img001.jpg"}' | jq '{tags: .tags[:3], dims: (.embedding | length)}'
# Returns up to 3 tags and confirms embedding length == 512
```

Re-run Library Scanner or re-process test files and confirm `media_tags` and `media_embeddings` populate.

---

## Phase 5 — Frontend API + Auth ✅ VALIDATED (2026-05-03)

**Goal:** All REST endpoints needed by the React UI are implemented and guarded by JWT auth.

### Notes from implementation

- `Microsoft.AspNetCore.Authentication.JwtBearer` is NOT in the ASP.NET Core shared framework for the .NET 10 SDK (10.0.100) used here — must be added as an explicit NuGet reference (`10.0.0`). It transitively brings `System.IdentityModel.Tokens.Jwt` and `Microsoft.IdentityModel.Tokens`.
- `CosineDistance` vector extension method requires `using Pgvector.EntityFrameworkCore;` — not `using Pgvector;` alone.
- Refresh token stored as a SHA-256 hash in `identity."AspNetUsers"` (new columns via migration `20260501000000_AddRefreshTokens`). Only 2 users exist so a full scan to find the matching hash is acceptable.
- JWT: HS256, 15 min access token, 30-day sliding refresh token window. Secret key in `appsettings.json` (override via env var in production).
- SignalR hub at `/hubs/library` — NGINX config already had the WebSocket upgrade rule. `media.added` broadcast fires from `SaveMediaHandler` (n8n calls this after each file processed). `media.deleted` fires from soft-delete and bulk-delete endpoints.
- Tool endpoints (`/api/tools/*`) and `POST /api/media` are intentionally unauthenticated — they are called by n8n from within the Docker network.
- Video transcode: on-demand HEVC → H.264 via ffmpeg subprocess, cached in `VideoTranscodes` table and `/video-cache/{id}.mp4`. `VideoCacheDir` config key defaults to `/video-cache` (Docker volume mount).
- Albums, photo_series, series_media, video_transcodes tables already exist via Phase 1 init SQL — all configured with `ExcludeFromMigrations()`.
- Snapshot updated: corrected `Api.Data.MediaFileRecord` → `Api.Data.MediaFile` (pre-existing stale name), added `RefreshTokenHash` and `RefreshTokenExpiry` properties to AppUser section.
- `DELETE /api/media/bulk` accepts a JSON body (unconventional but valid HTTP). Client must set `Content-Type: application/json`.

### Deliverables

- JWT auth middleware (15 min expiry) + refresh token (30-day sliding window) via ASP.NET Identity
  - `POST /api/auth/login`, `/api/auth/refresh`, `/api/auth/logout`
- Media endpoints (all require auth):
  - `GET /api/media` — paginated, filter by `from`/`to`/`tag`/`favorite`
  - `GET /api/media/{id}` — detail with metadata + tags
  - `GET /api/media/{id}/thumbnail` — `X-Accel-Redirect` to nginx `/thumbs/`
  - `GET /api/media/{id}/original` — stream original as-is
  - `GET /api/media/{id}/stream` — on-demand HEVC→H.264 transcode via ffmpeg subprocess; caches to `video_transcodes` + disk
  - `PATCH /api/media/{id}`, `DELETE /api/media/{id}` (soft delete)
- Search endpoints:
  - `GET /api/search?q=` — CLIP semantic (calls AI sidecar `/embed/text`, then pgvector `<=>` cosine)
  - `GET /api/search/tags?tag=` — tag filter
- Albums CRUD (`/api/albums/*`)
- Series endpoints (`/api/series/*`)
- Admin job endpoints (`/api/admin/jobs`, `/api/admin/jobs/{id}`, `/api/admin/jobs/{id}/retry`)
- Review endpoints:
  - `GET /api/media/review` — items flagged as meme/screenshot/artwork, sorted by confidence desc
  - `DELETE /api/media/bulk` — `{ ids: [...] }` hard-deletes files from disk and DB
- SignalR hub `/hubs/library` — `media.added` and `media.deleted` events
- NGINX updated: `X-Accel-Redirect` internal location for `/thumbs/`; WebSocket upgrade for `/hubs/`

### Done when

```bash
TOKEN=$(curl -s -X POST http://localhost/api/auth/login \
        -H 'Content-Type: application/json' \
        -d '{"username":"mike","password":"DevPassword1!"}' | jq -r .accessToken)
curl -s "http://localhost/api/media?pageSize=5" \
     -H "Authorization: Bearer $TOKEN" | jq '.items[].id'
# Returns 5 media IDs from paged result
curl -s "http://localhost/api/search?q=birthday+cake" \
     -H "Authorization: Bearer $TOKEN" | jq '.[0].id'
# Returns semantic matches
```

---

## Phase 6 — React Frontend

**Goal:** Both users can browse, search, and manage the library from a browser.

### Notes from implementation

- UI prototype built in `prototype/` (`index.html` + `server.js`) — served at `http://localhost:3333`, used for live design review against real TestPhoto thumbnails before React implementation begins.
- Pages implemented in prototype: Dashboard (POTD, OTD strip, library stats widget), On This Day (grouped by year), Gallery (square grid, year dropdown, month breadcrumb, 50 items/page with Prev/Next), Geo (Leaflet dark map with cluster pins), Search (semantic + tag + date range + country chips), Albums, Videos.
- Lightbox: always bottom-bar layout (photo fills upper area, metadata + tags + zoom controls below); loads full-size originals from TestPhoto; supports video playback (`.mp4`/`.MOV`), zoom (wheel + buttons), map tab (Leaflet), keyboard shortcuts.
- Gallery pagination: 50 photos per page. Year dropdown (1997–2026). Month breadcrumb with left/right arrows.
- Country filter chips: country names only, no ISO codes.
- Real thumbnails from TestPhoto served via `/thumbs/<guid>.webp`; originals via `/originals/<path>`.

### Deliverables

- React + TypeScript (`src/Frontend/`)
- Vite build; production output served by `nginx:alpine` container
- Auth: login screen, JWT stored in memory (refresh token in HttpOnly cookie), silent refresh
- Library view: infinite-scroll photo grid sorted by `taken_at DESC`; date grouping headers
- Photo detail: full-screen viewer with metadata panel (EXIF, tags, GPS if available)
- Video playback: `<video>` element targeting `/api/media/{id}/stream`
- Semantic search: search bar → debounced `GET /api/search?q=`; results grid
- Tag filter: click a tag chip → `GET /api/search/tags?tag=`
- Albums: list, create, view, add/remove photos
- Favorites toggle (star icon)
- Admin jobs panel: list jobs by status, view error detail, trigger retry
- SignalR integration: new photos appear in grid without page reload
- **Review page** (`/review`): grid of items AI-flagged as `screenshot`, `document`, `artwork`, or `reference` (not yet reviewed by user), sorted by confidence. Each card has two buttons: **"Meme"** (sets `origin_class = 'meme'`, hidden from gallery/search/OTD) and **"Real Photo"** (marks reviewed, keeps original AI class). Optimistic card removal on action.

### Done when

End-to-end smoke test (manual):
1. Log in as Mike
2. New HEIC dropped into Samba share → Library Scanner run → photo appears in grid within seconds of SignalR push
3. Search "birthday cake" returns semantically relevant results
4. Video plays in browser without downloading original

---

## Phase 7 — Automation & Notifications

**Goal:** Unattended operation: daily Telegram photo, admin alerts on failures.

### Deliverables

- **Workflow 3 — Photo of the Day** (JSON export committed)
  - Cron `0 15 * * *`; queries `media_files` for favorites / high-confidence tags; sends thumbnail to Telegram channel
- **Workflow 4 — Error Handler** (JSON export committed)
  - Global n8n error workflow; updates `processing_jobs`; sends Telegram alert to admin (suppressed for W3)
- Telegram Bot token and chat IDs stored as n8n credentials (not in env files or repo)
- `.env.example` updated with all required env var names (no actual values)

### Done when

1. Inject a deliberately corrupt HEIC → Library Scanner run → Telegram admin message received within ~30 s
2. Wait for 15:00 (or advance system clock in test) → Photo of the Day message received in Telegram channel

---

## Phase 7.5 — Post-Phase Improvements ✅ COMPLETE (2026-05-31)

**Goal:** Incremental quality improvements after Phase 7 validation.

### Deliverables

- **Lightbox file path**: Replaced hidden `<abbr title>` tooltip with a visible `.lb-filepath` row (monospace, full path, ellipsis truncation, `folder_open` icon). Always visible above the date in the lightbox info bar.
- **Meme/Review feature**:
  - `/review` page and nav link: grid of AI-flagged items (`screenshot`, `document`, `artwork`, `reference`), each with "Meme" and "Real Photo" action buttons
  - `PATCH /api/media/{id}/review` — `{ action: "meme"|"real" }`: sets `origin_reviewed = true`; "meme" also sets `origin_class = 'meme'`
  - Gallery (`GET /api/media`), semantic search, and On This Day all exclude `origin_class = 'meme'`
  - `origin_reviewed BOOLEAN NOT NULL DEFAULT FALSE` column added to `media_files`

---

## Phase 8 — Archive Import

**Goal:** All ~20 years of existing photos catalogued in the database.

### Deliverables

- Run Library Scanner (Workflow 2) against `/mnt/usb-hdd/Photo` — covers all existing year folders
- Monitor `processing_jobs` for failures; investigate and re-run as needed
- Verify row counts: `SELECT COUNT(*) FROM media_files` ≈ known archive size
- Confirm semantic search and Photo of the Day draw from historical photos

### Notes

- This is a one-time operational step, not a code deliverable
- Estimated duration: depends on archive size and CPU; ~50–100 ms/file on i7-6700 (AI is the bottleneck)
- Can be interrupted and resumed — Library Scanner skips files already in `media_files`

---

## Dependency Graph

```
Phase 1 (infra)
    └── Phase 2 (.NET tools)
            └── Phase 3 (n8n pipeline)
                    ├── Phase 4 (AI sidecar)    ← enriches pipeline
                    └── Phase 5 (frontend API)
                            └── Phase 6 (React UI)
                                    └── Phase 7 (automation)
                                            └── Phase 8 (archive import)
```

Phases 4 and 5 can be developed in parallel after Phase 3 is stable.

---

## What is NOT in scope

- Face / people recognition
- Remote access (LAN only)
- Web browser file upload
- RAW camera files (CR2, ARW, NEF)
- Per-user library separation
- TLS / HTTPS (NGINX is `http`-only for now; add certs later)
