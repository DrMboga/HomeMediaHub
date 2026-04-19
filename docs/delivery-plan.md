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

## Phase 1 — Infrastructure Skeleton

**Goal:** Docker Compose stack boots; all containers start and can reach each other.

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

## Phase 2 — .NET Tool Endpoints

**Goal:** n8n can call all four `.NET` tool endpoints; each returns correct output against real files.

### Deliverables

- `.NET 10` Web API project (`src/Api/`)
- ASP.NET Core Identity schema via EF Core migration (users table only — no auth wired yet)
- Seed script: create Mike and wife accounts
- Four tool controllers:
  - `POST /api/tools/hash` — SHA-256 via `System.Security.Cryptography`
  - `POST /api/tools/metadata` — EXIF via `MetadataExtractor`; accepts JPEG only
  - `POST /api/tools/thumbnail` — 400 px WebP via `SixLabors.ImageSharp`; writes to `/thumbnails/<id>.webp`
  - `GET  /api/tools/scan?path=` — walks directory, excludes paths already in `media_files`
- Serilog structured JSON logging (file path, duration_ms, outcome per request)
- Docker image build (`src/Api/Dockerfile`)

### Done when

```bash
# From outside the container:
curl -s -X POST http://localhost:8080/api/tools/hash \
     -H 'Content-Type: application/json' \
     -d '{"filePath":"/photo/2005/img001.jpg"}' | jq .hash
# Returns a SHA-256 hex string
```

---

## Phase 3 — n8n Ingest Pipeline

**Goal:** Library Scanner finds files and fully processes them through Workflow 1 into the database.

### Deliverables

- Custom n8n image (`Dockerfile.n8n`): `FROM n8nio/n8n:latest` + `apk add ffmpeg libheif-tools`
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

### Done when

1. Drop a test HEIC and JPEG into `/mnt/usb-hdd/Photo/test/`
2. Trigger Library Scanner manually
3. Confirm rows appear in `media_files`, `media_metadata`, `media_thumbnails`, `media_tags` (tags may be empty — AI not wired yet), `processing_jobs` status = `completed`

---

## Phase 4 — AI Sidecar

**Goal:** Every ingested photo receives object-detection tags and a CLIP embedding.

### Deliverables

- Python 3.11 FastAPI service (`src/AiSidecar/`)
- Models baked into image at build time:
  - YOLOv8n (`ultralytics` — 6 MB)
  - CLIP ViT-B/32 (`openai/clip-vit-base-patch32` via `transformers` or `clip` package — 151 MB)
- Endpoints:
  - `POST /process { filePath }` → `{ tags: [{label, confidence}], embedding: [512] }`
  - `POST /embed/text { text }` → `{ embedding: [512] }`
- Confidence threshold: ≥ 0.40 for YOLOv8 tags
- L2-normalised CLIP embeddings
- Workflow 1 already calls `/process`; simply uncomment the AI step (it was `continue-on-fail`)

### Done when

```bash
curl -s -X POST http://localhost:8000/process \
     -H 'Content-Type: application/json' \
     -d '{"filePath":"/photo/2005/img001.jpg"}' | jq '{tags: .tags[:3], dims: (.embedding | length)}'
# Returns up to 3 tags and confirms embedding length == 512
```

Re-run Library Scanner or re-process test files and confirm `media_tags` and `media_embeddings` populate.

---

## Phase 5 — Frontend API + Auth

**Goal:** All REST endpoints needed by the React UI are implemented and guarded by JWT auth.

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
- SignalR hub `/hubs/library` — `media.added` and `media.deleted` events
- NGINX updated: `X-Accel-Redirect` internal location for `/thumbs/`; WebSocket upgrade for `/hubs/`

### Done when

```bash
TOKEN=$(curl -s -X POST http://localhost/api/auth/login \
        -d '{"username":"mike","password":"..."}' | jq -r .token)
curl -s "http://localhost/api/media?pageSize=5" \
     -H "Authorization: Bearer $TOKEN" | jq '.[].id'
# Returns 5 media IDs
curl -s "http://localhost/api/search?q=birthday+cake" \
     -H "Authorization: Bearer $TOKEN" | jq '.[0].id'
# Returns semantic matches
```

---

## Phase 6 — React Frontend

**Goal:** Both users can browse, search, and manage the library from a browser.

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
