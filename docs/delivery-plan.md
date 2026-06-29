# HomeMediaHub — Phased Delivery Plan

Each phase produces a running, testable increment. Later phases build on earlier ones without requiring rework of completed pieces.

## Development strategy: stage → prod

All phases are validated on the **stage** environment first (TestPhoto folder on server SSD, no SATA HDD required). When a phase is stable on stage, it is smoke-tested on **prod** (SATA HDD, full Photo library). The same `docker-compose.yml` is used for both; only the env file differs.

| Env | Photo source | How to start |
|-----|-------------|--------------|
| stage | `/opt/homemediahub/stage/TestPhoto` | `docker compose --env-file .env.stage up -d` |
| prod  | `/mnt/storage/Photo` | `docker compose --env-file .env.prod up -d` |

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
- Volume mounts wired: `/mnt/storage/Photo`, `/mnt/storage/thumbnails`, `/mnt/storage/video-cache`, `/tmp/homemediahub-work`
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

> **Phase 8 note:** The tool endpoints (`/api/tools/*`) are removed in Phase 8. Their logic (hashing, conversion, metadata extraction, thumbnail generation, scanning, dedup) is ported to `src/MediaWorker/Services/`. The GeoService HTTP endpoint (`GET /reverse`) is kept; Phase 8 adds a RabbitMQ consumer alongside it.

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

> **Phase 8 note:** n8n and all its workflows are removed in Phase 8 and replaced by RabbitMQ + MassTransit consumers in `src/MediaWorker/`. The processing logic (HEIC conversion, metadata extraction, dedup, thumbnail, geocode, DB insert) is equivalent — ported to `FileConvertService`, `MetadataService`, `IngestConsumer`, `MediaSaveConsumer`. The n8n workflow JSON files are deleted along with `Dockerfile.n8n`.

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

## Phase 4 — AI Sidecar ✅ VALIDATED

**Goal:** Every ingested photo receives object-detection tags and a CLIP embedding.

> **Phase 8 note:** The AI Sidecar's FastAPI HTTP server (`/analyze`, `/health`) is replaced in Phase 8 with a pika RabbitMQ consumer. The model loading code (YOLOv8n + CLIP), inference logic, and origin classification are unchanged. Only `POST /embed/text` is kept as an HTTP endpoint (API calls it for semantic search query embedding). The sidecar is renamed from `main.py` to `worker.py`.

### Deliverables

- Python 3.11 FastAPI service (`src/AiSidecar/`)
- Models baked into image at build time:
  - YOLOv8n (`ultralytics` — 6 MB)
  - CLIP ViT-B/32 (`open_clip` — 151 MB)
- Endpoints:
  - `POST /analyze { filePath }` → `{ tags: [{label, confidence}], embedding: [512], originClass, originConfidence }` — combined YOLOv8 + CLIP in a single call
  - `POST /embed/text { text }` → `{ embedding: [512] }`
- Confidence threshold: ≥ 0.30 for YOLOv8 tags
- L2-normalised CLIP embeddings
- Origin classification: CLIP zero-shot across 6 classes — `photo`, `screenshot`, `document`, `artwork`, `reference`, `people`. `meme` is user-assigned only (never set by AI).

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
- Admin job endpoints (`/api/admin/jobs`, `/api/admin/jobs/{id}`)
- Review endpoints:
  - `GET /api/media/review` — items flagged as meme/screenshot/artwork, sorted by confidence desc
  - `DELETE /api/media/bulk` — `{ ids: [...] }` hard-deletes files from disk and DB
- SignalR hub `/hubs/library` — `media.added` and `media.deleted` events
- NGINX updated: `X-Accel-Redirect` internal location for `/thumbs/`; WebSocket upgrade for `/hubs/`

---

## Phase 6 — React Frontend ✅ VALIDATED (2026-05-06)

**Goal:** Both users can browse, search, and manage the library from a browser.

### Deliverables

- React + TypeScript (`src/Frontend/`)
- Vite build; production output served by `nginx:alpine` container
- Auth: login screen, JWT stored in memory (refresh token in HttpOnly cookie), silent refresh
- 7 pages wired to the real API: Dashboard (POTD, OTD strip, library stats), Gallery (paginated grid, year/month nav), On This Day, Geo (Leaflet dark map with pins), Search (semantic), Albums, Videos
- Lightbox with zoom, keyboard shortcuts, Info/Map tab, visible file path row
- **Review page** (`/review`): paginated grid (50/page, URL-backed) of AI-flagged items, each with "Meme" / "Real Photo" action buttons. Optimistic card removal.
- Three new API endpoints added: `/api/media/stats`, `/api/media/on-this-day`, `/api/media/geo`
- Thumbnail, original, and stream endpoints made public (JWT cannot be sent from `<img>`/`<video>` tags; GUID URLs are unguessable on LAN)

---

## Phase 7 — Automation & Notifications

**Goal:** Unattended operation: daily Telegram photo, admin alerts on failures.

> **Superseded by Phase 8.** Telegram notifications (Photo of the Day, error alerts) were dropped with the n8n removal. They can be added later as a dead-letter consumer or scheduled BackgroundService in `media-worker`. Phase 8 replaces n8n entirely, so Workflow 3 and Workflow 4 are deleted.

### Original deliverables (not implemented)

- **Workflow 3 — Photo of the Day** — cron `0 15 * * *`; queries favorites / high-confidence tags; sends thumbnail to Telegram
- **Workflow 4 — Error Handler** — global n8n error workflow; updates `processing_jobs`; sends Telegram admin alert

---

## Phase 7.5 — Post-Phase Improvements ✅ COMPLETE (2026-05-31)

**Goal:** Incremental quality improvements after Phase 7 validation.

### Deliverables

- **Lightbox file path**: Replaced hidden `<abbr title>` tooltip with a visible `.lb-filepath` row (monospace, full path, ellipsis truncation, `folder_open` icon). Always visible above the date in the lightbox info bar.
- **Meme/Review feature**:
  - `/review` page and nav link: paginated grid (50/page, URL-backed) of AI-flagged items (`screenshot`, `document`, `artwork`, `reference`), each with "Meme" and "Real Photo" action buttons
  - `GET /api/media/review` — paginated (`page`, `pageSize`), returns `{ items, total }`, sorted by `origin_confidence DESC`
  - `PATCH /api/media/{id}/review` — `{ action: "meme"|"real" }`: sets `origin_reviewed = true`; "meme" also sets `origin_class = 'meme'`; uses `ExecuteUpdateAsync` (not `SaveChangesAsync`) to bypass the global `NoTracking` context
  - Gallery (`GET /api/media`), semantic search, and On This Day all exclude `origin_class = 'meme'`
  - `origin_reviewed BOOLEAN NOT NULL DEFAULT FALSE` column added to `media_files`
- **OriginClass rework**: AI sidecar expanded from 3 to 6 classes
  - `photo` — real camera photograph (landscape, object, scene)
  - `screenshot` — computer or phone screen capture
  - `document` — printed paper, forms, passports, ID cards, credit cards, driver's licenses
  - `artwork` — drawings, paintings, illustrations, digital art
  - `reference` — informational photos: utility meters, signs, notices, labels
  - `people` — portraits and group shots (NOT flagged for review — normal photos)
  - `meme` — user-assigned only, never set by AI
  - Gallery: color-coded origin badge on each card; filter chip row (All · Photo · People · Screenshot · Artwork · Document · Reference · Meme)
  - Search: origin type dropdown filter
  - Lightbox: origin class + confidence % shown in info bar
  - Both `GET /api/media` and `GET /api/search/semantic` accept `?originClass=` query param
- **Migration squash**: `AddRefreshTokens` migration folded into `CreateIdentitySchema`. Refresh token columns (`refresh_token_hash`, `refresh_token_expiry`) added directly to `AspNetUsers` CREATE TABLE. Single EF migration now. No manual column patching needed on fresh deployments.
- **Bug fix — ReviewMediaHandler**: `AppDbContext` sets `QueryTrackingBehavior.NoTracking` globally. Original handler loaded entity + `SaveChangesAsync` — UPDATE was silently discarded. Fixed by switching to `ExecuteUpdateAsync`.

---

## Phase 8 — RabbitMQ + MassTransit Migration 🚧 IN PROGRESS (2026-06-29)

**Goal:** Replace n8n with a RabbitMQ message broker + MassTransit worker pipeline. Server is torn down and rebuilt fresh — no DB migration needed.

**What changes:**
- `n8n` container removed; `Dockerfile.n8n` and all n8n workflow JSONs deleted
- New `src/MediaWorker/` .NET Worker Service owns the ingest pipeline
- New `src/Contracts/` shared message library (records only, no NuGet dependencies)
- AI Sidecar: FastAPI HTTP server replaced with pika RabbitMQ consumer; `POST /embed/text` kept as a minimal HTTP endpoint for the API
- GeoService: gains MassTransit `GeoLookupConsumer` alongside existing `GET /reverse` HTTP endpoint
- API: adds `IPublishEndpoint` (publish-only, no consumers) + `ScanScheduler` IHostedService; removes tool endpoints
- New Prometheus + Grafana for pipeline observability

### Step 1 — Create `src/Contracts/` shared message library

`dotnet new classlib -n Contracts -o src/Contracts --framework net10.0`

Create `src/Contracts/Messages/` with these records:

| File | Record |
|------|--------|
| `ScanRequested.cs` | `(Path, Source, TriggeredBy, RequestedAt)` |
| `FileDiscovered.cs` | `(FilePath, Source, DiscoveredAt)` |
| `MediaReadyForEnrichment.cs` | `(MediaFileId, FilePath, WorkFilePath?, FileHash, Source, MimeType, FileSize, Width, Height, DurationSec?, TakenAt?, Lat?, Lon?, CameraMake?, CameraModel?, ThumbnailPath)` |
| `AiAnalysisRequested.cs` | `(CorrelationId, FilePath)` |
| `AiAnalysisCompleted.cs` | `(CorrelationId, Tags[], Embedding[], OriginClass, OriginConfidence)` + `AiTag(Tag, Confidence, Model)` |
| `GeoLookupRequested.cs` | `(CorrelationId, Lat, Lon)` |
| `GeoLookupCompleted.cs` | `(CorrelationId, Address?)` |
| `MediaSaveRequested.cs` | `(MediaFileId, FilePath, FileHash, Source, MimeType, FileSize, Width, Height, DurationSec?, TakenAt?, Lat?, Lon?, Address?, CameraMake?, CameraModel?, ThumbnailPath, Tags[], Embedding[], OriginClass, OriginConfidence)` |
| `MediaProcessingFailed.cs` | `(FilePath, Stage, ErrorMessage, OccurredAt)` |

`CorrelationId` = `MediaFileId` throughout.

### Step 2 — Create `src/MediaWorker/` (.NET Worker Service)

`dotnet new worker -n MediaWorker -o src/MediaWorker --framework net10.0`

**NuGet packages:**
```
MassTransit, MassTransit.RabbitMQ, MassTransit.EntityFrameworkCore
Npgsql.EntityFrameworkCore.PostgreSQL, pgvector.EntityFrameworkCore
EFCore.NamingConventions, SixLabors.ImageSharp, prometheus-net
```

**Service classes** (ported from `src/Api/Features/Tools/`):

| From | To |
|------|-----|
| `Hash/HashFileHandler.cs` | `Services/FileHashService.cs` |
| `Convert/ConvertFileHandler.cs` | `Services/FileConvertService.cs` |
| `Metadata/ExtractMetadataHandler.cs` | `Services/MetadataService.cs` |
| `Thumbnail/GenerateThumbnailHandler.cs` | `Services/ThumbnailService.cs` |
| `Scan/ScanDirectoryHandler.cs` | `Services/ScanService.cs` |
| `Dedup/DedupHandler.cs` | `Services/DedupService.cs` |

Remove MediatR, change namespace. Each becomes a plain `public class XxxService` with constructor-injected `WorkerDbContext` and `ILogger`.

**`WorkerDbContext`** — same entities as `AppDbContext` but without Identity tables. Shares `homemediahub_db`. Adds `ProcessingState` saga entity. EF migration creates `media_processing_sagas` table. `Program.cs` calls `db.Database.MigrateAsync()` on startup.

**Consumers:**
- `ScanConsumer` — consumes `ScanRequested`, calls `ScanService.FindUnprocessedAsync`, publishes `FileDiscovered` per file
- `IngestConsumer` — consumes `FileDiscovered`, runs hash → dedup → convert → metadata → thumbnail → publishes `MediaReadyForEnrichment` (`prefetch_count = 4`)
- `MediaSaveConsumer` — consumes `MediaSaveRequested`, EF Core inserts into `homemediahub_db`, writes `processing_jobs` row with `status = 'completed'`
- `DeadLetterConsumer` — consumes dead-letter queue, writes `processing_jobs` row with `status = 'failed'`

**`ProcessingStateMachine`** (MassTransitStateMachine):
- States: `Initial` → `Enriching` → Finalized (row removed via `SetCompletedWhenFinalized()`)
- In `Initially`: store enrichment fields, set `GeoCompleted = true` if no GPS, publish `AiAnalysisRequested`, conditionally publish `GeoLookupRequested`, transition to `Enriching`
- In `Enriching` on `AiAnalysisCompleted`: store AI fields; if `GeoCompleted` → publish `MediaSaveRequested` + Finalize
- In `Enriching` on `GeoLookupCompleted`: store address; if `AiCompleted` → publish `MediaSaveRequested` + Finalize
- Tags and embedding stored as JSON strings (`TagsJson`, `EmbeddingJson`) in saga state

**`ProcessingState` fields:** `CorrelationId`, `CurrentState`, `RowVersion`, all enrichment request fields, `AiCompleted`, `OriginClass`, `OriginConfidence`, `TagsJson`, `EmbeddingJson`, `GeoCompleted`, `Address`

**Dockerfile:** SDK + publish → `aspnet:10.0` runtime + `apt-get install libheif-tools libde265-0 perl-image-exiftool ffmpeg`

### Step 3 — Modify `src/Api/`

**Add:**
- NuGet: `MassTransit`, `MassTransit.RabbitMQ`, `prometheus-net.AspNetCore`
- `<ProjectReference>` to `Contracts.csproj`
- MassTransit registration (publish-only, no consumers) in `Program.cs`
- `app.UseHttpMetrics()` + `app.MapMetrics("/metrics")`
- `POST /api/admin/scan` endpoint → publishes `ScanRequested`
- `src/Api/Services/ScanScheduler.cs` (BackgroundService, monthly cron `0 2 1 * *`)

**Remove:**
- `src/Api/Features/Tools/` — entire folder
- `src/Api/Endpoints/ToolEndpoints.cs` + its registration in `Program.cs`
- `POST /api/media` save endpoint + `SaveMedia` feature folder (saving is now done by `MediaSaveConsumer`)

**Keep unchanged:** all frontend-facing endpoints, JWT auth, EF Core query handlers, SignalR

### Step 4 — Modify `src/GeoService/`

**Add:**
- NuGet: `MassTransit`, `MassTransit.RabbitMQ`, `prometheus-net.AspNetCore`
- `<ProjectReference>` to `Contracts.csproj`
- `GeoLookupConsumer : IConsumer<GeoLookupRequested>` — calls existing `ReverseGeocodeHandler` → publishes `GeoLookupCompleted`
- MassTransit registration + `app.MapMetrics("/metrics")` in `Program.cs`

**Keep unchanged:** `GET /reverse`, `GET /health`, all existing geocoding logic

### Step 5 — Modify `src/AiSidecar/`

**Replace `main.py` with `worker.py`:**
- Load models once at startup (same code as existing lifespan)
- pika consumer loop on `ai-analysis-requested` queue (`prefetch_count=1`, durable)
- On each message: run inference → publish JSON to `ai-analysis-completed`
- `POST /embed/text` kept as minimal HTTP handler on port 8000 (daemon thread); `GET /health` on same thread
- Prometheus metrics server on port 8001 (`start_http_server(8001)`)

**`requirements.txt`:** add `pika`, `prometheus-client`; remove `fastapi`, `uvicorn`

**`Dockerfile`:** change CMD from `uvicorn main:app ...` to `python worker.py`

### Step 6 — Update `docker-compose.yml`

**Remove:** `n8n` service block, n8n volume, `n8n` from nginx `depends_on`

**Add services:** `rabbitmq`, `media-worker`, `prometheus`, `grafana`

**Update services:**
- `api` — add RabbitMQ env vars, add `depends_on: rabbitmq`
- `geoservice` — add RabbitMQ env vars, add `depends_on: rabbitmq`
- `ai-sidecar` — add `RABBITMQ_URL` env var, add `depends_on: rabbitmq`

**Add volumes:** `rabbitmq_data`, `prometheus_data`, `grafana_data`

### Step 7 — Create `infra/` config files

**`infra/rabbitmq/enabled_plugins`:**
```
[rabbitmq_management,rabbitmq_prometheus].
```

**`infra/prometheus/prometheus.yml`:**
```yaml
global:
  scrape_interval: 15s
scrape_configs:
  - { job_name: api,          static_configs: [{targets: ['api:8080']}],          metrics_path: /metrics }
  - { job_name: media-worker, static_configs: [{targets: ['media-worker:9090']}],  metrics_path: /metrics }
  - { job_name: geoservice,   static_configs: [{targets: ['geoservice:8001']}],    metrics_path: /metrics }
  - { job_name: ai-sidecar,   static_configs: [{targets: ['ai-sidecar:8001']}],    metrics_path: /metrics }
  - { job_name: rabbitmq,     static_configs: [{targets: ['rabbitmq:15692']}],     metrics_path: /metrics }
```

**`infra/grafana/provisioning/datasources/prometheus.yml`** — Prometheus data source pointing to `http://prometheus:9090`

**`infra/grafana/provisioning/dashboards/dashboards.yml`** — file provider

**Dashboard JSON files to commit:**
- `masstransit.json` — Grafana dashboard ID 15467
- `rabbitmq.json` — Grafana dashboard ID 10991
- `pipeline.json` — custom (files/hour, queue depths, AI P95, dead-letter count)

### Step 8 — Update env example files

**Add to `.env.stage.example` and `.env.prod.example`:**
```bash
RABBITMQ_USER=hmh
RABBITMQ_PASS=
GRAFANA_PASSWORD=
```

**Remove:** `SERVER_IP` (was only used by n8n `WEBHOOK_URL`)

### Step 9 — Delete deprecated files

```
Dockerfile.n8n
infra/n8n/w1-process-single-file.json
infra/n8n/w2-library-scanner.json
infra/n8n/w4-error-handler.json
infra/n8n/             (directory)
```

### Verification checklist

| # | Check | Expected |
|---|-------|----------|
| 1 | `docker compose --env-file .env.stage up -d` | All services start without errors |
| 2 | `http://192.168.0.192:15672` (RabbitMQ mgmt) | Exchanges and queues declared by MassTransit on startup |
| 3 | `POST /api/admin/scan` with JWT | HTTP 202; `ScanRequested` message visible in RabbitMQ mgmt UI |
| 4 | `docker compose logs -f media-worker` | `FileDiscovered` consumed; `MediaReadyForEnrichment` published per file |
| 5 | RabbitMQ mgmt → `ai-analysis-requested` queue | Messages consumed by AI sidecar; `ai-analysis-completed` fills |
| 6 | RabbitMQ mgmt → `geo-lookup-requested` queue | Messages consumed by GeoService |
| 7 | `GET /api/media` | New records appear with tags, embeddings, and address populated |
| 8 | `http://192.168.0.192:9090` → Status → Targets | All 5 targets green (UP) |
| 9 | `http://192.168.0.192:3001` (Grafana) | MassTransit dashboard shows message rates; RabbitMQ dashboard shows queue depths |
| 10 | Corrupt a test file → trigger scan | File lands in dead-letter queue in RabbitMQ UI; `processing_jobs` row with `status = 'failed'` |
| 11 | `dotnet test` | Existing unit tests pass (API query handlers are unchanged) |

---

## Phase 9 — Archive Import

**Goal:** All ~20 years of existing photos catalogued in the database.

> Previously Phase 8. Moved here because Phase 8 is now the RabbitMQ migration.

### Deliverables

- Run `POST /api/admin/scan` against prod environment (full `/mnt/storage/Photo` tree)
- Monitor `processing_jobs` table and RabbitMQ dead-letter queues for failures
- Verify row counts: `SELECT COUNT(*) FROM media_files` ≈ known archive size
- Confirm semantic search, On This Day, and geo map draw from historical photos

### Notes

- This is a one-time operational step, not a code deliverable
- Estimated duration: ~50–100 ms/file on i7-6700; 189K files ≈ 5–10 hours (AI is the bottleneck)
- Scan is idempotent — `IngestConsumer` skips files already in `media_files` via dedup hash check
- Monitor with Grafana pipeline dashboard (files/hour, queue depths, dead-letter count)
- If the run is interrupted, simply trigger another scan; only unindexed files will be picked up

---

## Dependency Graph

```
Phase 1 (infra skeleton)
    └── Phase 2 (.NET tool endpoints + GeoService)
            └── Phase 3 (n8n pipeline — historical)
                    └── Phase 4 (AI sidecar)
                            └── Phase 5 (frontend API + auth)
                                    └── Phase 6 (React frontend)
                                            └── Phase 7.5 (post-phase improvements)
                                                    └── Phase 8 (RabbitMQ + MassTransit migration)
                                                            └── Phase 9 (archive import)

Note: Phase 7 (Telegram notifications) was superseded by Phase 8.
      It can be added after Phase 8 as a dead-letter consumer or scheduled BackgroundService.
```

---

## What is NOT in scope

- Face / people recognition
- Remote access (LAN only)
- Web browser file upload
- RAW camera files (CR2, ARW, NEF)
- Per-user library separation
- TLS / HTTPS (NGINX is `http`-only for now; add certs later)
- Telegram notifications (dropped with n8n; can be added as a dead-letter consumer post-Phase 8)
- Photo of the Day Telegram post (dropped with n8n; can be added as a scheduled BackgroundService)
