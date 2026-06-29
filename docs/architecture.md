# HomeMediaHub — Architecture

## 1. System Context

```
                 ┌─────────────────────────────────────────────────────────────────────┐
                 │                      Dell Precision 3620                             │
                 │                      Intel i7-6700 · 32 GB RAM                      │
                 │                      Ubuntu Server 24.04                             │
                 │                                                                      │
 ┌──────────┐    │  ┌──────────────────────────────────────────────────────────────┐   │
 │ iPhone   │────┼─►│  Immich  :2283   (mobile backup endpoint only, no UI used)   │   │
 │ (family) │    │  └──────────────────────┬─────────────────────────────────────-─┘   │
 └──────────┘    │     writes originals to  │ /photo/iPhone/...                        │
                 │                          │                                           │
 ┌──────────┐    │  ┌──────────────────────▼─────────────────────────────────────┐    │
 │ Browser  │────┼─►│  NGINX  :80                                                  │    │
 │ (LAN)    │    │  │   /         → frontend (React)   :3000                      │    │
 └──────────┘    │  │   /api/     → .NET API            :8080                     │    │
                 │  │   /thumbs/  → filesystem direct (X-Accel-Redirect)          │    │
                 │  └─────────────────────────────────────────────────────────────┘    │
 ┌──────────┐    │                                                                      │
 │ Windows  │    │  ┌──────────────────────────────────────────────────────────────┐   │
 │ MacOS    │────┼─►│  Samba  (native Ubuntu service, not containerized)           │   │
 │ (LAN)    │    │  │  share: \\server\Storage → /mnt/storage                     │   │
 └──────────┘    │  └──────────────────────────────────────────────────────────────┘   │
                 │                                                                      │
                 │  ┌──────────────────────────────────────────────────────────────┐   │
                 │  │  .NET 10 API  :8080                                           │   │
                 │  │   • REST endpoints for the React frontend                    │   │
                 │  │   • POST /api/admin/scan → publishes ScanRequested           │   │
                 │  │   • Monthly ScanScheduler IHostedService (cron 0 2 1 * *)    │   │
                 │  │   • Prometheus /metrics on :8080                             │   │
                 │  └──────────────────┬────────────────────┬──────────────────────┘   │
                 │                     │ publishes           │ HTTP POST /embed/text    │
                 │                     ▼                     ▼                          │
                 │  ┌──────────────────────────────┐  ┌───────────────────────────┐   │
                 │  │  RabbitMQ  :5672             │  │  AI Sidecar  :8000 (health)│   │
                 │  │  :15672 (mgmt UI)            │  │              :8001 (metrics)│  │
                 │  │  :15692 (Prometheus metrics) │  │  Python 3.11              │   │
                 │  └──────┬───────────────────────┘  │  pika RabbitMQ consumer   │   │
                 │         │ messages                  │  YOLOv8n + CLIP           │   │
                 │         ▼                           └───────────────────────────┘   │
                 │  ┌──────────────────────────────────────────────────────────────┐   │
                 │  │  MediaWorker  (.NET 10 Worker Service)                        │   │
                 │  │   :9090 (Prometheus /metrics only)                           │   │
                 │  │                                                               │   │
                 │  │  ScanConsumer      — finds new files → FileDiscovered        │   │
                 │  │  IngestConsumer    — hash/dedup/convert/metadata/thumbnail   │   │
                 │  │  ProcessingSaga    — aggregates AI + Geo fan-out results     │   │
                 │  │  MediaSaveConsumer — EF Core → homemediahub_db               │   │
                 │  └──────────────────────────────────────────────────────────────┘   │
                 │                                                                      │
                 │  ┌──────────────────────┐  ┌───────────────────────────────────┐   │
                 │  │  GeoService  :8001   │  │  PostgreSQL  :5432                │   │
                 │  │  .NET 10             │  │  pgvector/pgvector:pg16            │   │
                 │  │  HTTP /reverse +     │  │  homemediahub_db                  │   │
                 │  │  MassTransit consumer│  │  (app data + saga state)          │   │
                 │  └──────────────────────┘  └───────────────────────────────────┘   │
                 │  ┌──────────────────────┐  ┌───────────────────────────────────┐   │
                 │  │  GeoService Postgres  │  │  Prometheus  :9090               │   │
                 │  │  :5434               │  │  Grafana     :3001               │   │
                 │  │  geoservice_db       │  │  (scrapes all services + RabbitMQ)│   │
                 │  └──────────────────────┘  └───────────────────────────────────┘   │
                 │  ┌──────────────────────┐  ┌───────────────────────────────────┐   │
                 │  │  Immich PostgreSQL   │  │  Redis  :6379  (Immich only)      │   │
                 │  │  :5433               │  └───────────────────────────────────┘   │
                 │  └──────────────────────┘                                           │
                 │                                                                      │
                 │  ┌──────────────────────────────────────────────────────────────┐   │
                 │  │  SATA HDD  /mnt/storage/                                     │   │
                 │  │   └── Photo/               ← single source of truth          │   │
                 │  │        ├── 2005/ ... 2025/ ← existing archive                │   │
                 │  │        └── iPhone/         ← new uploads from Immich         │   │
                 │  │   └── thumbnails/          (generated, recreatable)          │   │
                 │  │   └── video-cache/         (H.264 transcodes, recreatable)   │   │
                 │  │   └── display-cache/       (JPEG from HEIC, recreatable)     │   │
                 │  └──────────────────────────────────────────────────────────────┘   │
                 └─────────────────────────────────────────────────────────────────────┘
```

---

## 2. Processing Pipeline

```
POST /api/admin/scan  OR  monthly IHostedService cron (0 2 1 * *)
              │
              │ publishes ScanRequested
              ▼
        ┌─────────────┐
        │  RabbitMQ   │◄── all inter-service messages flow through here
        └──────┬──────┘
               │ FileDiscovered (one per new file)
               ▼
   ┌─────────────────────────────────┐
   │  MediaWorker / ScanConsumer     │  scans /photo, filters already-indexed
   └────────────┬────────────────────┘
                │ FileDiscovered published per new file
                ▼
   ┌─────────────────────────────────┐
   │  MediaWorker / IngestConsumer   │
   │                                 │  1. SHA-256 hash
   │  prefetch_count = 4             │  2. dedup check → skip if exists
   │                                 │  3. convert: HEIC → JPEG (heif-convert + exiftool)
   │                                 │             video → JPEG frame (ffmpeg)
   │                                 │  4. extract metadata (ImageSharp EXIF + exiftool JSON)
   │                                 │  5. generate WebP thumbnail (ImageSharp)
   └────────────┬────────────────────┘
                │ publishes MediaReadyForEnrichment
                ▼
   ┌────────────────────────────────────────┐
   │  Fan-out → 2 queues simultaneously     │
   └────────┬──────────────────┬────────────┘
            │                  │
   ai-analysis-requested  geo-lookup-requested (skipped if no GPS)
            │                  │
            ▼                  ▼
   ┌─────────────────┐  ┌──────────────────────────┐
   │  AI Sidecar     │  │  GeoService               │
   │  Python + pika  │  │  .NET MassTransit consumer│
   │  prefetch = 1   │  │  + Nominatim cache        │
   │                 │  │  (same ReverseGeocode      │
   │  YOLOv8n        │  │   handler as HTTP /reverse)│
   │  CLIP ViT-B/32  │  └──────────────┬────────────┘
   └────────┬────────┘                 │
            │ AiAnalysisCompleted      │ GeoLookupCompleted
            └──────────────┬───────────┘
                           ▼
   ┌──────────────────────────────────────────────┐
   │  MediaWorker / ProcessingSaga                 │
   │  (MassTransitStateMachine + PostgreSQL)       │
   │                                               │
   │  States: Initial → Enriching → (Finalized)   │
   │                                               │
   │  On MediaReadyForEnrichment:                  │
   │    store all fields                           │
   │    GeoCompleted = true if no GPS             │
   │    publish AiAnalysisRequested                │
   │    publish GeoLookupRequested (if GPS)        │
   │    → Enriching                                │
   │                                               │
   │  On AiDone (in Enriching):                   │
   │    store tags, embedding, originClass         │
   │    if GeoCompleted → publish MediaSaveReq     │
   │                       → Finalize (row deleted)│
   │                                               │
   │  On GeoDone (in Enriching):                  │
   │    store address                              │
   │    if AiCompleted → publish MediaSaveReq      │
   │                      → Finalize (row deleted) │
   └──────────────────────────────────────────────┘
                           │ MediaSaveRequested
                           ▼
   ┌──────────────────────────────────────────────┐
   │  MediaWorker / MediaSaveConsumer             │
   │  EF Core → homemediahub_db                   │
   │  writes: media_files, media_metadata,        │
   │          media_thumbnails, media_tags,        │
   │          media_embeddings, processing_jobs    │
   └──────────────────────────────────────────────┘

Dead Letter Queue → processing_jobs.status = 'failed'
```

**Key design points:**
- `IngestConsumer` runs with `prefetch_count = 4` — 4 files processed concurrently (HEIC/video conversion is the bottleneck, not CPU contention)
- AI Sidecar runs with `prefetch_count = 1` — inference is CPU-bound, no benefit from parallelism on a single host
- Saga is eliminated immediately on finalization — `SetCompletedWhenFinalized()` removes the row from `media_processing_sagas` once the save request is published
- No GPS → saga sets `GeoCompleted = true` in `Initially` handler and never publishes `GeoLookupRequested`; the saga remains symmetric — always waits for both flags before finalizing

---

## 3. Service Breakdown

### immich-server
- **Image:** `ghcr.io/immich-app/immich-server:release`
- **Port:** 2283
- **Responsibility:** Receive iPhone backups via the Immich mobile app. Writes originals directly into `/photo/iPhone/{y}/{y-MM-dd}/`. No webhooks used — the API's monthly `ScanScheduler` picks up new files.
- **Boundaries:** Owns its own Postgres and Redis. Not accessed by any custom service.

### immich-postgres
- **Image:** `pgvector/pgvector:pg16`
- **Port:** 5433 (internal)
- **Responsibility:** Immich's private database. Not accessed by custom services.

### redis
- **Image:** `redis:7-alpine`
- **Port:** 6379 (internal)
- **Responsibility:** Immich's job queue. Not shared.

### rabbitmq
- **Image:** `rabbitmq:3-management-alpine`
- **Ports:** 5672 (AMQP), 15672 (management UI), 15692 (Prometheus metrics)
- **Responsibility:** Message broker for all inter-service communication. MassTransit declares all exchanges and queues on startup. Plugins enabled: `rabbitmq_management`, `rabbitmq_prometheus`.
- **Queue overview (MassTransit conventions):**
  - `MediaWorker_ScanConsumer` — receives `ScanRequested`
  - `MediaWorker_IngestConsumer` — receives `FileDiscovered`
  - `MediaWorker_ProcessingState` — receives `MediaReadyForEnrichment`, `AiAnalysisCompleted`, `GeoLookupCompleted` (saga)
  - `MediaWorker_MediaSaveConsumer` — receives `MediaSaveRequested`
  - `GeoService_GeoLookupConsumer` — receives `GeoLookupRequested`
  - `AiSidecar_AiAnalysisConsumer` — receives `AiAnalysisRequested` (declared by pika, durable)
  - Dead letter queues: `*_error` suffix for each consumer

### api
- **Image:** custom `.NET 10` build
- **Port:** 8080
- **Responsibilities:**
  1. **Frontend REST API** — all media queries, search, albums, series, auth; unchanged from Phase 7
  2. **Scan trigger** — `POST /api/admin/scan` publishes `ScanRequested` via MassTransit (publish-only, no consumers in API)
  3. **Monthly cron** — `ScanScheduler : BackgroundService` fires `0 2 1 * *` automatically
  4. **On-demand video transcoding** — `GET /api/media/{id}/stream` transcodes HEVC→H.264 via ffmpeg, cached in `video_transcodes` + disk
  5. **On-demand HEIC display** — `GET /api/media/{id}/display` converts HEIC→JPEG on first request, caches in `display-cache/`
  6. **Prometheus** — `/metrics` endpoint via `prometheus-net.AspNetCore`
- **What it does NOT do:** no background file processing, no hashing, no EXIF extraction, no thumbnail generation, no AI calls, no geocoding

### media-worker
- **Image:** custom `.NET 10` Worker Service build
- **Port:** 9090 (Prometheus `/metrics` only — no HTTP API)
- **Responsibility:** Owns the entire ingestion pipeline. All processing logic ported from the old `/api/tools/*` handlers lives here:

| Service class | Logic |
|---------------|-------|
| `FileHashService` | SHA-256 via `System.Security.Cryptography` |
| `DedupService` | `WorkerDbContext` lookup by hash |
| `FileConvertService` | `heif-convert -q 90` + `exiftool -TagsFromFile --Orientation`; `ffmpeg` for video frame |
| `MetadataService` | `Image.IdentifyAsync` (EXIF) + exiftool JSON (video); same logic as old `ExtractMetadataHandler` |
| `ThumbnailService` | `Image.LoadAsync` + `AutoOrient` + `Resize(Max 400×400)` + WebP 80% via ImageSharp |
| `ScanService` | `Directory.EnumerateFiles` filtered by extension + DB exclusion |

- **Docker image:** `mcr.microsoft.com/dotnet/aspnet:10.0` + `apt-get install libheif-tools libde265-0 perl-image-exiftool ffmpeg`

### ai-sidecar
- **Image:** custom `Python 3.11` build
- **Ports:** 8000 (health check HTTP server), 8001 (Prometheus `/metrics`)
- **Responsibility:** RabbitMQ consumer for AI inference. Replaces FastAPI HTTP server from Phase 4.
  - Consumes `ai-analysis-requested` queue (pika, `prefetch_count=1`)
  - Runs YOLOv8n object detection + CLIP ViT-B/32 embedding + origin classification
  - Publishes `AiAnalysisCompleted` to `ai-analysis-completed` queue
  - **Keeps** `POST /embed/text` as a minimal HTTP endpoint on port 8000 (side thread) — API calls this for semantic search query embedding
- **Models:** YOLOv8n (6 MB) + CLIP ViT-B/32 (151 MB) — baked into image at build time

### geoservice
- **Image:** custom `.NET 10` build
- **Port:** 8001
- **Responsibilities:**
  1. **RabbitMQ consumer** — `GeoLookupConsumer : IConsumer<GeoLookupRequested>` → calls existing `ReverseGeocodeHandler` → publishes `GeoLookupCompleted`
  2. **HTTP cache** — `GET /reverse?lat=&lon=` kept for any direct callers (API's display endpoints)
  3. **Prometheus** — `/metrics` endpoint
- **Cache logic unchanged:** rounds coordinates to 4 decimal places, `geoservice_db` lookup first, Nominatim fallback, 1 req/sec policy

### geoservice-postgres
- **Image:** `postgres:16`
- **Port:** 5434 (internal)
- **Responsibility:** Geoservice's private address cache. Unchanged.

### postgres (custom)
- **Image:** `pgvector/pgvector:pg16`
- **Port:** 5432
- **Databases:** `homemediahub_db` only (n8n_db removed). Hosts both application tables and the `media_processing_sagas` saga state table (created by MediaWorker EF migration on first startup).

### prometheus
- **Image:** `prom/prometheus:latest`
- **Port:** 9090 (internal)
- **Scrape targets:**

| Job | Target | Path |
|-----|--------|------|
| `api` | `api:8080` | `/metrics` |
| `media-worker` | `media-worker:9090` | `/metrics` |
| `geoservice` | `geoservice:8001` | `/metrics` |
| `ai-sidecar` | `ai-sidecar:8001` | `/metrics` |
| `rabbitmq` | `rabbitmq:15692` | `/metrics` |

### grafana
- **Image:** `grafana/grafana:latest`
- **Port:** 3001 (internal, admin only)
- **Dashboards (pre-provisioned via `infra/grafana/provisioning/`):**
  - MassTransit .NET (Grafana ID 15467) — message rates, consumer lag, error rates
  - RabbitMQ Overview (Grafana ID 10991) — queue depths, publish/consume rates, connections
  - HomeMediaHub Pipeline (custom) — files/hour, AI inference time P95, dead-letter count, saga state breakdown

### frontend
- **Image:** custom `nginx:alpine` serving the React + TypeScript Vite build
- **Port:** 3000 (internal, proxied by main NGINX)

### nginx
- **Image:** `nginx:alpine`
- **Port:** 80
- **Routes:**
  - `/` → frontend :3000
  - `/api/` → api :8080
  - `/thumbs/` → filesystem (`/thumbs/`) via `X-Accel-Redirect` (no .NET per thumbnail)
  - `/immich/` → immich-server :2283

### Samba (native Ubuntu — not containerized)
- **Share:** `\\<server-ip>\Storage` → `/mnt/storage`
- **Access:** read-write for `mediahub` group members on LAN

---

## 4. Message Contracts (`src/Contracts/`)

Shared `net10.0` class library. No NuGet dependencies — pure C# records. Referenced by `Api`, `MediaWorker`, and `GeoService`.

```
src/Contracts/Messages/
├── ScanRequested.cs          record(Path, Source, TriggeredBy, RequestedAt)
├── FileDiscovered.cs         record(FilePath, Source, DiscoveredAt)
├── MediaReadyForEnrichment.cs record(MediaFileId, FilePath, WorkFilePath?,
│                                      FileHash, Source, MimeType, FileSize,
│                                      Width, Height, DurationSec?, TakenAt?,
│                                      Lat?, Lon?, CameraMake?, CameraModel?,
│                                      ThumbnailPath)
├── AiAnalysisRequested.cs    record(CorrelationId, FilePath)
├── AiAnalysisCompleted.cs    record(CorrelationId, Tags[], Embedding[], OriginClass, OriginConfidence)
│                             record AiTag(Tag, Confidence, Model)
├── GeoLookupRequested.cs     record(CorrelationId, Lat, Lon)
├── GeoLookupCompleted.cs     record(CorrelationId, Address?)
├── MediaSaveRequested.cs     record(MediaFileId, FilePath, FileHash, Source,
│                                     MimeType, FileSize, Width, Height, DurationSec?,
│                                     TakenAt?, Lat?, Lon?, Address?, CameraMake?,
│                                     CameraModel?, ThumbnailPath, Tags[], Embedding[],
│                                     OriginClass, OriginConfidence)
└── MediaProcessingFailed.cs  record(FilePath, Stage, ErrorMessage, OccurredAt)
```

`CorrelationId` = `MediaFileId` (Guid) — MassTransit correlates saga instances on this value.

---

## 5. Environments

Two isolated environments share the same `docker-compose.yml`. Only one runs at a time.

| Variable | Stage | Prod |
|----------|-------|------|
| `COMPOSE_PROJECT_NAME` | `hmh-stage` | `hmh-prod` |
| `PHOTO_DIR` | `/opt/homemediahub/stage/TestPhoto` | `/mnt/storage/Photo` |
| `THUMBNAILS_DIR` | `/opt/homemediahub/stage/thumbnails` | `/mnt/storage/thumbnails` |
| `VIDEO_CACHE_DIR` | `/opt/homemediahub/stage/video-cache` | `/mnt/storage/video-cache` |
| `DISPLAY_CACHE_DIR` | `/opt/homemediahub/stage/display-cache` | `/mnt/storage/display-cache` |
| `WORK_DIR` | `/tmp/homemediahub-stage` | `/tmp/homemediahub-prod` |
| `DATA_DIR` | `/opt/homemediahub/stage/data` | `/opt/homemediahub/data` |
| `RABBITMQ_USER` | `hmh` | `hmh` |
| `RABBITMQ_PASS` | (set in .env.stage) | (set in .env.prod) |
| `GRAFANA_PASSWORD` | (set in .env.stage) | (set in .env.prod) |

---

## 6. Storage Layout

```
/mnt/storage/
├── Photo/                          ← photo library (Samba \\server\Storage\Photo)
│   ├── 2005/ ... 2025/             ← existing archive (untouched)
│   └── iPhone/                     ← new uploads from Immich
│       └── 2026/2026-06-29/        └── IMG_1234.HEIC
├── thumbnails/                     ← generated WebP thumbnails (recreatable)
│   └── <media-file-id>.webp
├── video-cache/                    ← H.264 transcodes (recreatable)
│   └── <media-file-id>.mp4
└── display-cache/                  ← JPEG from HEIC (recreatable)
    └── <media-file-id>.jpg
```

---

## 7. Database Schema

### `homemediahub_db` (postgres:5432)

Application tables defined in `infra/postgres/init/02-schema.sql`. Saga state table added by EF migration in `MediaWorker`.

```sql
CREATE EXTENSION IF NOT EXISTS vector;

-- Application tables (schema: public) ----------------------------------------

CREATE TABLE media_files (
    id                UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path         TEXT NOT NULL UNIQUE,
    file_hash         TEXT NOT NULL,
    source            TEXT NOT NULL,          -- 'library' | 'scheduled'
    immich_asset_id   TEXT,
    mime_type         TEXT NOT NULL,
    file_size         BIGINT,
    width             INT,
    height            INT,
    duration_sec      FLOAT,
    taken_at          TIMESTAMPTZ,
    created_at        TIMESTAMPTZ DEFAULT NOW(),
    is_deleted        BOOLEAN DEFAULT FALSE,
    is_favorite       BOOLEAN DEFAULT FALSE,
    origin_class      TEXT,                   -- 'photo'|'screenshot'|'document'|'artwork'|'reference'|'people'|'meme'
    origin_confidence FLOAT,
    origin_reviewed   BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE media_metadata (
    media_file_id  UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    latitude       FLOAT,
    longitude      FLOAT,
    address        TEXT,
    camera_make    TEXT,
    camera_model   TEXT,
    exif_json      JSONB
);

CREATE TABLE media_tags (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    media_file_id  UUID NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    tag            TEXT NOT NULL,
    confidence     FLOAT NOT NULL,
    model          TEXT NOT NULL              -- 'yolov8n'
);

CREATE TABLE media_embeddings (
    media_file_id  UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    embedding      vector(512),
    created_at     TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX ON media_embeddings USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE TABLE media_thumbnails (
    media_file_id  UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    thumbnail_path TEXT NOT NULL
);

CREATE TABLE processing_jobs (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path      TEXT NOT NULL,
    source         TEXT NOT NULL,
    status         TEXT NOT NULL,             -- 'completed' | 'failed'
    error_message  TEXT,
    failed_step    TEXT,                      -- consumer or stage name
    started_at     TIMESTAMPTZ DEFAULT NOW(),
    completed_at   TIMESTAMPTZ,
    retry_count    INT DEFAULT 0
);

CREATE TABLE albums (
    id          UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name        TEXT NOT NULL,
    description TEXT,
    created_at  TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE album_media (
    album_id       UUID REFERENCES albums(id) ON DELETE CASCADE,
    media_file_id  UUID REFERENCES media_files(id) ON DELETE CASCADE,
    added_at       TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (album_id, media_file_id)
);

CREATE TABLE video_transcodes (
    media_file_id  UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    transcode_path TEXT NOT NULL,
    codec          TEXT NOT NULL,
    container      TEXT NOT NULL,
    file_size      BIGINT,
    created_at     TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE photo_series (
    id             UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    series_type    TEXT NOT NULL,
    taken_at_start TIMESTAMPTZ,
    taken_at_end   TIMESTAMPTZ,
    latitude       FLOAT,
    longitude      FLOAT,
    cover_media_id UUID REFERENCES media_files(id)
);

CREATE TABLE series_media (
    series_id      UUID REFERENCES photo_series(id) ON DELETE CASCADE,
    media_file_id  UUID REFERENCES media_files(id) ON DELETE CASCADE,
    PRIMARY KEY (series_id, media_file_id)
);

-- Saga state table (schema: public) — created by EF migration in MediaWorker --
-- media_processing_sagas: columns generated by MassTransit EF repository
--   correlation_id UUID PK, current_state TEXT, row_version BYTEA (optimistic lock),
--   + all ProcessingState fields stored between the Enriching transitions
```

### `geoservice_db` (geoservice-postgres:5434)

Single table. Created by GeoService EF migration on startup. Unchanged from Phase 2.

### Identity schema (schema: `identity` in `homemediahub_db`)

ASP.NET Core Identity tables created by EF migration in `src/Api/`. Unchanged from Phase 5.

---

## 8. API Surface

### Media (frontend — all require JWT)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/media` | Paginated. Params: `page`, `pageSize`, `from`, `to`, `tag`, `favorite`, `isVideo`, `noDate`, `originClass`. Excludes `meme` unconditionally. |
| GET | `/api/media/stats` | `{ totalCount, photoCount, videoCount, totalSizeMb, oldestTakenAt, newestTakenAt }` |
| GET | `/api/media/on-this-day` | `?month=&day=` → grouped by year |
| GET | `/api/media/geo` | `[{ id, lat, lon, thumbnailPath }]` for all geotagged items |
| GET | `/api/media/review` | Unreviewed items with `origin_class IN (screenshot, document, artwork, reference)`, sorted by confidence DESC |
| GET | `/api/media/{id}` | Single item with metadata and tags |
| GET | `/api/media/{id}/thumbnail` | WebP via nginx `X-Accel-Redirect` |
| GET | `/api/media/{id}/original` | Stream original file as-is |
| GET | `/api/media/{id}/display` | Browser-renderable image. HEIC→JPEG on first request, cached. |
| GET | `/api/media/{id}/stream` | H.264 MP4 transcode on first request, cached. |
| PATCH | `/api/media/{id}` | `{ isFavorite? }` |
| PATCH | `/api/media/{id}/review` | `{ action: "meme" \| "real" }` |
| DELETE | `/api/media/{id}` | Soft delete |
| DELETE | `/api/media/bulk` | `{ ids: [...] }` hard-delete from disk + DB |

### Search

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/search/semantic?q=` | CLIP semantic search — calls AI sidecar `POST /embed/text`, then pgvector `<=>`. Excludes `meme`. Optional `?originClass=` filter. |
| GET | `/api/search/tags?q=` | Tag filter |

### Albums

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/albums` | List |
| POST | `/api/albums` | `{ name, description }` |
| GET | `/api/albums/{id}` | Detail + media |
| POST | `/api/albums/{id}/media` | `{ mediaFileId }` |
| DELETE | `/api/albums/{id}/media/{mediaId}` | Remove from album |
| DELETE | `/api/albums/{id}` | Delete album (not media) |

### Admin

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/admin/scan` | Publish `ScanRequested` to RabbitMQ → triggers MediaWorker scan |
| GET | `/api/admin/jobs` | List processing jobs. Params: `status` (failed/completed), `page` |
| GET | `/api/admin/jobs/{id}` | Job detail including error message and failed stage |

### Series / Auth (unchanged from Phase 7)

---

## 9. AI Pipeline

```
RabbitMQ: ai-analysis-requested queue
              │
              ▼ (pika consumer, prefetch=1)
AI Sidecar — _load_models() at startup (once, blocking)

on_message(body):
    req = { correlationId, filePath }

    1. Pillow open (JPEG or converted frame)
    2. YOLOv8n inference on original file path
          output: [{label, confidence, bbox}]
          filter: confidence ≥ 0.30
    3. CLIP ViT-B/32 inference
          input: 224×224 center crop
          output: float32[512] L2-normalized embedding
          zero-shot origin classification over 6 prompts:
              "a real photograph taken with a camera..."    → photo
              "a screenshot of a computer or phone screen" → screenshot
              "a photograph of a document, printed paper..." → document
              "a drawing, painting, illustration..."        → artwork
              "a photograph taken to record information..." → reference
              "a portrait or group photograph..."           → people
          → pick highest cosine similarity

    publish to ai-analysis-completed:
        { correlationId, tags, embedding, originClass, originConfidence }

POST /embed/text (side thread, port 8000):
    .NET API calls this for semantic search query embedding
    tokens = tokenizer([text])
    features = clip_model.encode_text(tokens)  [L2-normalized]
    → { embedding: [512 floats] }
```

**Model details:**
- **YOLOv8n:** 6 MB, ~50 ms/image on CPU, 80 COCO object classes, confidence threshold 0.30
- **CLIP ViT-B/32:** 151 MB, 512-dim L2-normalized embeddings, shared image+text vector space
- Both baked into Docker image at build time (no cold-start downloads)
- `_inference_lock` (threading.Semaphore) serializes all inference — PyTorch CPU is not thread-safe under concurrent load

---

## 10. Volume Map

| Container | Mount | Host path | Access |
|-----------|-------|-----------|--------|
| immich-server | `/usr/src/app/upload` | `${PHOTO_DIR}/iPhone` | rw |
| api | `/photo` | `${PHOTO_DIR}` | ro |
| api | `/thumbnails` | `${THUMBNAILS_DIR}` | rw |
| api | `/video-cache` | `${VIDEO_CACHE_DIR}` | rw |
| api | `/display-cache` | `${DISPLAY_CACHE_DIR}` | rw |
| media-worker | `/photo` | `${PHOTO_DIR}` | ro |
| media-worker | `/thumbnails` | `${THUMBNAILS_DIR}` | rw |
| media-worker | `/shared-temp` | `${WORK_DIR}` | rw |
| ai-sidecar | `/photo` | `${PHOTO_DIR}` | ro |
| ai-sidecar | `/shared-temp` | `${WORK_DIR}` | ro |
| nginx | `/thumbs` | `${THUMBNAILS_DIR}` | ro |
| postgres | `/var/lib/postgresql/data` | `${DATA_DIR}/postgres` | rw |
| immich-postgres | `/var/lib/postgresql/data` | `${DATA_DIR}/immich-postgres` | rw |
| geoservice-postgres | `/var/lib/postgresql/data` | `${DATA_DIR}/geoservice-postgres` | rw |
| rabbitmq | `/var/lib/rabbitmq` | `${DATA_DIR}/rabbitmq` | rw |
| grafana | `/var/lib/grafana` | `${DATA_DIR}/grafana` | rw |

**Samba (native Ubuntu):** `/mnt/storage` → share name `Storage` — whole drive r/w from Windows/macOS on LAN.

---

## 11. Observability

### Prometheus metrics

| Service | Key metrics |
|---------|-------------|
| API (`prometheus-net.AspNetCore`) | HTTP request rate, latency percentiles, error rate by endpoint |
| MediaWorker (MassTransit built-in) | `masstransit_consume_total` by message type, consume duration, fault rate |
| GeoService (MassTransit + ASP.NET) | Geocode request rate, cache hit/miss ratio, Nominatim call latency |
| AI Sidecar (`prometheus_client`) | `ai_messages_processed_total`, `ai_processing_seconds` histogram |
| RabbitMQ (`rabbitmq_prometheus`) | Queue depths, publish/consume rates, connection count, memory |

### Grafana dashboards

| Dashboard | Source |
|-----------|--------|
| MassTransit .NET | Grafana ID 15467 — consumer lag, message rates, fault counts |
| RabbitMQ Overview | Grafana ID 10991 — queue depths, publish/consume rates |
| HomeMediaHub Pipeline | Custom — files/hour, AI inference P95, dead-letter count, saga states |

### Failure handling

| Stage | On failure | Effect |
|-------|-----------|--------|
| IngestConsumer | Unhandled exception | Message moves to dead-letter queue; `processing_jobs` row written with `status = 'failed'` by `DeadLetterConsumer` |
| AI Sidecar | pika nack | Message retried up to RabbitMQ retry policy; after max retries → dead letter |
| GeoService consumer | MassTransit fault | Retried by MassTransit; after retries exhausted → fault exchange |
| MediaSaveConsumer | EF Core error | MassTransit retry policy; after exhaustion → dead letter |
| Duplicate file | Detected in `DedupService` by hash | Silently dropped in `IngestConsumer` (return early, no message published) |

### Re-processing failed files

Files that failed with no `media_files` row (failed before save) are re-processed automatically on the next scan — `ScanService` excludes only files already in `media_files`. Files that failed geocoding or AI enrichment (partial records) can be targeted by querying:

```sql
-- Files missing AI embedding (AI step failed)
SELECT mf.file_path FROM media_files mf
LEFT JOIN media_embeddings me ON me.media_file_id = mf.id
WHERE me.media_file_id IS NULL AND mf.is_deleted = false;

-- Failed processing jobs
SELECT file_path, error_message, failed_step
FROM processing_jobs WHERE status = 'failed'
ORDER BY started_at DESC;
```

---

## 12. Samba Configuration

```ini
# /etc/samba/smb.conf (additions)
[Storage]
   path = /mnt/storage
   browseable = yes
   read only = no
   guest ok = no
   valid users = @mediahub
   create mask = 0664
   directory mask = 0775
```

macOS: Finder → Go → Connect to Server → `smb://<server-ip>/Storage`. Windows: `\\<server-ip>\Storage`.
