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
 └──────────┘    │     writes originals to  │ webhook: asset.uploaded                  │
                 │     Photo/iPhone/...     │                                           │
 ┌──────────┐    │  ┌──────────────────────▼─────────────────────────────────────┐    │
 │ Browser  │────┼─►│  NGINX  :80 / :443                                          │    │
 │ (LAN)    │    │  │   /         → frontend (React)   :3000                      │    │
 └──────────┘    │  │   /api/     → .NET API            :8080                     │    │
                 │  │   /hubs/    → .NET API (WebSocket) :8080                    │    │
                 │  │   /n8n/     → n8n UI               :5678 (admin only)       │    │
                 │  └─────────────────────────────────────────────────────────────┘    │
 ┌──────────┐    │                                                                      │
 │ Windows  │    │  ┌──────────────────────────────────────────────────────────────┐   │
 │ MacOS    │────┼─►│  Samba  (native Ubuntu service, not containerized)           │   │
 │ (LAN)    │    │  │  share: \\server\Photo → /mnt/usb-hdd/Photo                  │   │
 └──────────┘    │  └──────────────────────────────────────────────────────────────┘   │
                 │                                                                      │
 ┌──────────┐    │  ┌──────────────────────────────────────────────────────────────┐   │
 │ Telegram │◄───┤  │  n8n  :5678  ◄── ORCHESTRATION HUB                          │   │
 │ Channel  │    │  │                                                               │   │
 └──────────┘    │  │  Workflow 1: Process Single File  (core pipeline)            │   │
                 │  │  Workflow 2: Library Scanner      (manual/monthly → calls W1) │   │
                 │  │  Workflow 3: Photo of the Day     (daily cron → Telegram)     │   │
                 │  │  Workflow 4: Error Handler        (global error trigger)      │   │
                 │  └───────┬───────────────────────────────────────────────────────┘  │
                 │          │ HTTP calls to tools endpoints, AI sidecar & geoservice   │
                 │  ┌───────▼──────────────────────────────────────────────────────┐   │
                 │  │  .NET API  :8080                                              │   │
                 │  │   • REST endpoints for the React frontend                    │   │
                 │  │   • SignalR hubs (library updates)                           │   │
                 │  │   • Tool endpoints (called by n8n — stateless, atomic):      │   │
                 │  │       POST /api/tools/hash         compute SHA-256           │   │
                 │  │       POST /api/tools/metadata     extract EXIF              │   │
                 │  │       POST /api/tools/thumbnail    generate WebP thumbnail   │   │
                 │  │       GET  /api/tools/scan         list unprocessed files    │   │
                 │  └───────┬───────────────────────────┬──────────────────────────┘   │
                 │          │ SQL                        │ HTTP                         │
                 │  ┌───────▼──────────────┐  ┌─────────▼───────────────────────────┐ │
                 │  │  PostgreSQL  :5432    │  │  AI Sidecar  :8000  (Python 3.11)   │ │
                 │  │  + pgvector           │  │   POST /process  → tags + embedding │ │
                 │  └──────────────────────┘  │   POST /embed/text → query vector   │ │
                 │                            └─────────────────────────────────────┘ │
                 │  ┌──────────────────────┐  ┌──────────────────────────────────────┐ │
                 │  │  GeoService  :8001   │  │  GeoService PostgreSQL  :5434        │ │
                 │  │  (.NET 10 — reverse  │  │  geoservice_db (address cache,       │ │
                 │  │   geocoding cache)   │  │  keyed by rounded lat/lon)           │ │
                 │  └──────────────────────┘  └──────────────────────────────────────┘ │
                 │  ┌──────────────────────┐  ┌──────────────────────────────────────┐ │
                 │  │  Immich PostgreSQL   │  │  Redis  :6379  (Immich only)         │ │
                 │  │  :5433               │  └──────────────────────────────────────┘ │
                 │  └──────────────────────┘                                            │
                 │                                                                      │
                 │  ┌──────────────────────────────────────────────────────────────┐   │
                 │  │  USB HDD  /mnt/usb-hdd/                                      │   │
                 │  │   └── Photo/                  ← single source of truth        │   │
                 │  │        ├── 2005/                                              │   │
                 │  │        ├── 2006/                                              │   │
                 │  │        ├── iPhoneMike2025-06-09/  (existing structure)        │   │
                 │  │        ├── iPhone/              (new uploads from Immich)     │   │
                 │  │        │    └── 2026/2026-04-18/ IMG_1234.HEIC               │   │
                 │  │        └── ...                                               │   │
                 │  │   └── thumbnails/             (generated, recreatable)        │   │
                 │  └──────────────────────────────────────────────────────────────┘   │
                 └─────────────────────────────────────────────────────────────────────┘
```

---

## 2. Service Breakdown

### immich-server
- **Image:** `ghcr.io/immich-app/immich-server:release`
- **Port:** 2283
- **Responsibility:** Receive iPhone backups via the Immich mobile app. Configured with a storage template so it writes originals directly into `/mnt/usb-hdd/Photo/iPhone/{y}/{y}-{MM}-{dd}/`. Fires `asset.uploaded` webhook to n8n. No custom UI, no ML processing.
- **Storage template env:** `IMMICH_MEDIA_LOCATION=/mnt/usb-hdd/Photo/iPhone`
- **Boundaries:** Owns its own Postgres and Redis. Not accessed by any custom service directly.

### immich-postgres
- **Image:** `postgres:16`
- **Port:** 5433 (internal)
- **Responsibility:** Immich's private database. Not accessed by custom services.

### redis
- **Image:** `redis:7-alpine`
- **Port:** 6379 (internal)
- **Responsibility:** Immich's job queue. Not shared.

### n8n
- **Image:** custom build — `FROM n8nio/n8n:latest` + `RUN apk add --no-cache ffmpeg libheif-tools`
- **Port:** 5678
- **Responsibility:** All multi-step processing logic. See Workflow definitions in Section 4. Replaces all background workers. Calls .NET tool endpoints, AI sidecar, geoservice, and Telegram Bot API. Runs `heif-convert` and `ffprobe`/`ffmpeg` via Execute Command nodes for format conversion.
- **Internal database:** Uses `n8n_db` on the shared custom Postgres instance (separate database from `homemediahub_db`). Configured via `DB_TYPE=postgresdb`. Execution history, workflow definitions, and credentials stored there — included automatically in `pg_dump` backups.
- **No Immich webhook.** Immich writes files to disk; the Library Scanner workflow (Workflow 2) is the only ingest entry point, run manually or on a monthly cron.

### api
- **Image:** custom `.NET 10` build
- **Port:** 8080
- **Responsibility:** Two distinct roles:
  1. **Frontend API** — REST endpoints and SignalR for the React frontend (browse, search, albums, auth)
  2. **Tool endpoints** — stateless atomic operations n8n calls:
     - `POST /api/tools/hash` — SHA-256 of a file
     - `POST /api/tools/metadata` — EXIF extraction via MetadataExtractor; **always receives a JPEG** (n8n converts HEIC first; video metadata comes from ffprobe, not this endpoint)
     - `POST /api/tools/thumbnail` — generate 400 px WebP thumbnail via ImageSharp; **always receives a JPEG**
     - `GET  /api/tools/scan` — list files under a path not yet in the DB
  3. **On-demand video transcoding** — `GET /api/media/{id}/stream` transcodes HEVC/H.265 (iPhone `.mov`) to H.264 MP4 via `ffmpeg` subprocess on first request. Cached to `/mnt/usb-hdd/video-cache/`. Subsequent requests stream the cached file. Original is never modified.
- **What it does NOT do:** no background workers, no Quartz jobs, no AI calls, no Telegram, no geocoding, no HEIC conversion (those happen in n8n before calling these endpoints).

### ai-sidecar
- **Image:** custom `Python 3.11` build
- **Port:** 8000 (FastAPI)
- **Responsibility:** Stateless inference service.
  - `POST /process { filePath }` → `{ tags: [{label, confidence}], embedding: [512 floats] }`
  - `POST /embed/text { text }` → `{ embedding: [512 floats] }` (for search queries)
  - `POST /classify-origin { filePath }` → `{ originClass, confidence }` — CLIP zero-shot classification of whether a file is a real photo, document, meme, screenshot, or artwork. Used to populate `origin_class` / `origin_confidence` on `media_files`.
- **Models:** YOLOv8n (object detection, 6 MB) + CLIP ViT-B/32 (semantic embeddings and origin classification, 151 MB). Both baked into the image.

### geoservice
- **Image:** custom `.NET 10` build
- **Port:** 8001 (internal only)
- **Responsibility:** Reverse geocoding cache. Single endpoint: `GET /reverse?lat={lat}&lon={lon}` → `{ "address": "..." }`.
  - Rounds coordinates to 4 decimal places (~11 m precision) and looks up the result in `geoservice_db`.
  - On a cache miss, calls `nominatim.openstreetmap.org/reverse` with a proper `User-Agent`, persists the result, and returns it.
  - Enforces Nominatim's 1 req/sec policy internally via a semaphore + 1 s delay.
  - Designed as a standalone microservice — no dependency on any other HomeMediaHub service or database. Reusable in other projects.
- **What it does NOT do:** no EXIF parsing, no media storage, no authentication.

### geoservice-postgres
- **Image:** `postgres:16`
- **Port:** 5434 (internal)
- **Responsibility:** Geoservice's private database (`geoservice_db`). Single table:
  ```sql
  CREATE TABLE geo_cache (
      lat        FLOAT8 NOT NULL,
      lon        FLOAT8 NOT NULL,
      address    TEXT NOT NULL,
      cached_at  TIMESTAMPTZ DEFAULT NOW(),
      PRIMARY KEY (lat, lon)
  );
  ```
  Not accessed by any other service.

### postgres (custom)
- **Image:** `pgvector/pgvector:pg16`
- **Port:** 5432
- **Responsibility:** Application database with pgvector for similarity search. Schema defined in Section 5.

### frontend
- **Image:** custom `nginx:alpine` serving the React + TypeScript build
- **Port:** 3000 (internal, proxied by main NGINX)

### nginx
- **Image:** `nginx:alpine`
- **Port:** 80 (443 when TLS certs added)
- **Routes:**
  - `/` → frontend :3000
  - `/api/`, `/hubs/` → api :8080 (WebSocket upgrade for `/hubs/`)
  - `/thumbs/` → served directly from `/mnt/usb-hdd/thumbnails/` by nginx (no .NET involved). The .NET API returns an `X-Accel-Redirect: /thumbs/<id>.webp` header; nginx intercepts it and serves the file from disk. This bypasses the .NET process entirely for thumbnail delivery.
  - `/immich/` → immich-server :2283 (mobile app connectivity)
  - `/n8n/` → n8n :5678 (admin access only, restrict by IP in nginx config)

### Samba (native Ubuntu — not containerized)
- **Package:** `samba` installed on host
- **Share:** `\\<server-ip>\Photo` → `/mnt/usb-hdd/Photo`
- **Access:** read-write for family members on LAN (Windows and macOS)
- **Why not containerized:** Samba at the container level requires `--privileged` and `--network host` which eliminates the isolation benefit. Running it natively on Ubuntu is standard, simpler, and more reliable.

---

## 3. Environments

Two isolated environments share the same `docker-compose.yml`. Path-sensitive values are injected via env files. Only one environment runs at a time (they bind the same ports).

| Variable | Stage | Prod |
|----------|-------|------|
| `COMPOSE_PROJECT_NAME` | `hmh-stage` | `hmh-prod` |
| `PHOTO_DIR` | `/opt/homemediahub/stage/TestPhoto` | `/mnt/usb-hdd/Photo` |
| `THUMBNAILS_DIR` | `/opt/homemediahub/stage/thumbnails` | `/mnt/usb-hdd/thumbnails` |
| `VIDEO_CACHE_DIR` | `/opt/homemediahub/stage/video-cache` | `/mnt/usb-hdd/video-cache` |
| `WORK_DIR` | `/tmp/homemediahub-stage` | `/tmp/homemediahub-prod` |
| `DATA_DIR` | `/opt/homemediahub/stage/data` | `/opt/homemediahub/data` |
| `GEOSERVICE_DB_PASSWORD` | (set in .env.stage) | (set in .env.prod) |

`COMPOSE_PROJECT_NAME` gives each environment distinct container names so a mistaken `docker compose up` cannot silently overwrite the other environment's state.

**Stage** uses a small curated `TestPhoto` folder on the server SSD (no USB HDD required). It is used for all development and phase validation. **Prod** uses the real archive on the USB HDD and is only switched to after each phase is stable on stage.

---

## 4. Storage Layout

All originals live under a single directory tree. No duplication.

```
/mnt/usb-hdd/
├── Photo/                          ← Samba share root (\\server\Photo)
│   ├── 2005/                       ← existing archive (untouched)
│   ├── 2006/
│   ├── iPhoneMike2025-06-09/       ← existing archive (untouched)
│   ├── iPhone/                     ← new uploads from Immich
│   │   └── 2026/
│   │       └── 2026-04-18/
│   │           └── IMG_1234.HEIC
│   └── ...
├── thumbnails/                     ← generated WebP thumbnails (recreatable)
│   └── <media-file-id>.webp
└── video-cache/                    ← H.264 transcodes of iPhone videos (recreatable)
    └── <media-file-id>.mp4
```

---

## 5. n8n Workflows

### Workflow 1 — Process Single File (core pipeline)

**Trigger:** Webhook `POST /webhook/process-file` or sub-workflow call
**Input:** `{ filePath: "/mnt/usb-hdd/Photo/...", source: "immich"|"archive" }`

```
[Webhook / Execute Workflow trigger]
  │
  ├─[Postgres] INSERT INTO processing_jobs (file_path, source, status, n8n_execution_id)
  │             VALUES ({{ filePath }}, {{ source }}, 'running', {{ $execution.id }})
  │   → { jobId }   ← carry this through all steps
  │
  ├─[HTTP Request] POST api:8080/api/tools/hash { filePath }
  │   → { hash: "sha256..." }
  │
  ├─[Postgres] SELECT id FROM media_files WHERE file_hash = {{ hash }}
  │   → if found:
  │       [Postgres] UPDATE processing_jobs SET status='skipped', completed_at=NOW()
  │       [Stop] ── duplicate, skip
  │
  ├─[Set] ext = lowercase file extension from filePath
  │
  ├─── BRANCH: HEIC ──────────────────────────────────────────────────────────
  │  [IF] ext == "heic" or "heif"
  │   ├─[Execute Command] heif-convert "{{ filePath }}" "/shared-temp/{{ uuid }}.jpg"
  │   └─[Set] workPath = "/shared-temp/{{ uuid }}.jpg"  ← temp JPEG for .NET calls
  │
  ├─── BRANCH: VIDEO ─────────────────────────────────────────────────────────
  │  [IF] ext in ["mp4", "mov", "m4v", "avi"]
  │   ├─[Execute Command]
  │   │   ffprobe -v quiet -print_format json -show_format -show_streams "{{ filePath }}"
  │   │   → parse JSON → { width, height, duration, takenAt, lat, lon }
  │   ├─[Execute Command]
  │   │   ffmpeg -i "{{ filePath }}" -ss 00:00:01 -vframes 1 "/shared-temp/{{ uuid }}.jpg"
  │   │   → frame extracted for thumbnail
  │   └─[Set] workPath = "/shared-temp/{{ uuid }}.jpg"
  │           metadata = ffprobe result  ← skip .NET metadata call for video
  │
  ├─── BRANCH: JPEG / PNG / other ────────────────────────────────────────────
  │  [ELSE]
  │   └─[Set] workPath = filePath  ← use original directly
  │
  ├─[IF] not video (HEIC or JPEG branch)
  │   └─[HTTP Request] POST api:8080/api/tools/metadata { filePath: workPath }
  │       → { mimeType, fileSize, width, height, takenAt, lat, lon,
  │           cameraModel, exifJson }
  │       (receives JPEG always — either original or converted from HEIC)
  │
  ├─[HTTP Request] POST api:8080/api/tools/thumbnail { filePath: workPath }
  │   → { thumbnailPath }   (workPath is always JPEG at this point)
  │
  ├─[IF] temp file was created (HEIC or VIDEO branch)
  │   └─[Execute Command] rm "/shared-temp/{{ uuid }}.jpg"  ← cleanup
  │
  ├─[IF] lat and lon present?
  │   └─[HTTP Request] GET geoservice:8001/reverse?lat=&lon=
  │       → { address: "..." }
  │
  ├─[Postgres] INSERT INTO media_files (file_path, file_hash, source, mime_type,
  │             file_size, width, height, duration_sec, taken_at, ...)
  │   → { id: uuid }
  │
  ├─[Postgres] INSERT INTO media_metadata (media_file_id, lat, lon, address,
  │             camera_model, exif_json)
  │
  ├─[Postgres] INSERT INTO media_thumbnails (media_file_id, thumbnail_path)
  │
  ├─[HTTP Request] POST ai-sidecar:8000/process { filePath: workPath }
  │   → { tags: [{label, confidence}], embedding: [512 floats] }
  │   Note: workPath is the JPEG frame for video — good enough for object detection
  │
  ├─[Loop] for each tag:
  │   └─[Postgres] INSERT INTO media_tags (media_file_id, tag, confidence, model)
  │
  ├─[Postgres] INSERT INTO media_embeddings (media_file_id, embedding)
  │
  ├─[HTTP Request] POST ai-sidecar:8000/classify-origin { filePath: workPath }
  │   → { originClass: "real_photo"|"document"|"meme"|"screenshot"|"artwork",
  │       confidence: 0.0–1.0 }
  │   (continue-on-fail — classification is enrichment, not required)
  │
  ├─[Postgres] UPDATE media_files
  │             SET origin_class = {{ originClass }}, origin_confidence = {{ confidence }}
  │             WHERE id = {{ id }}
  │
  └─[Postgres] UPDATE processing_jobs SET status='completed', completed_at=NOW()
               WHERE id = {{ jobId }}
```

**Node-level error settings:**
- HEIC convert, ffprobe, ffmpeg, metadata, thumbnail: **Stop on fail** — these are blocking; a corrupt file must not silently produce a partial record.
- Geocoding (geoservice): **Continue on fail** — address is enrichment, not required. HTTP Request node configured with 2 retries, 5 s timeout.
- AI sidecar: **Continue on fail** — photo is still stored without tags/embedding; can be re-processed later.

**Shared temp volume:** n8n and api both mount `/shared-temp` → host path `/tmp/homemediahub-work`. Files created by `heif-convert` or `ffmpeg` in n8n are readable by the .NET API container at the same path. Files are deleted by n8n after use.

---

### Workflow 2 — Library Scanner

**Trigger:** Manual button in n8n UI, OR monthly cron (`0 2 1 * *` — 02:00 on the 1st of each month)
**Purpose:** Single entry point for all new media — both the initial archive import and ongoing iPhone uploads that Immich has written to disk. Immich fires no webhook; this workflow is the only ingest mechanism.

```
[Manual Trigger OR Cron: 0 2 1 * *]
  │
  ├─[HTTP Request] GET api:8080/api/tools/scan?path=/mnt/usb-hdd/Photo
  │   → { files: ["/mnt/usb-hdd/Photo/2005/img001.jpg", ...] }
  │   (returns only files not yet in media_files — by path AND hash check)
  │
  ├─[Split In Batches] batch size: 10
  │
  └─[Execute Workflow] → Workflow 1 with { filePath, source: "scan" } for each file
       (runs up to n8n's parallelism limit concurrently)
```

**Why no Immich webhook:** webhooks are fire-and-forget — if n8n is unavailable when Immich fires, the event is lost silently. A scheduled scan is slower to react but guaranteed to pick up every file eventually, with no lost events.

---

### Workflow 3 — Photo of the Day

**Trigger:** Cron — daily at 15:00
**Purpose:** Pick a notable photo and send it to the Telegram channel.

```
[Cron: 0 15 * * *]
  │
  ├─[Postgres] SELECT mf.id, mt.thumbnail_path
  │              FROM media_files mf
  │              JOIN media_thumbnails mt ON mt.media_file_id = mf.id
  │              WHERE mf.is_favorite = true
  │                 OR mf.id IN (
  │                      SELECT media_file_id FROM media_tags
  │                      WHERE confidence > 0.85 GROUP BY media_file_id
  │                    )
  │              ORDER BY md5(mf.id::text || current_date::text)  -- deterministic daily pick
  │              LIMIT 1
  │
  ├─[Read Binary File] thumbnailPath
  │
  └─[HTTP Request] POST https://api.telegram.org/bot{TOKEN}/sendPhoto
      { chat_id, photo: <binary> }
```

---

### Workflow 4 — Error Handler

**Trigger:** n8n Error Trigger (configured as the global error workflow for all other workflows)
**Purpose:** Record failures durably and alert on critical errors.

```
[Error Trigger]
  │ receives: { executionId, workflowId, workflowName,
  │             error: { message, stack },
  │             lastNodeExecuted }
  │
  ├─[Postgres] UPDATE processing_jobs
  │             SET status       = 'failed',
  │                 failed_step  = {{ lastNodeExecuted }},
  │                 error_message = {{ error.message }},
  │                 completed_at = NOW()
  │             WHERE n8n_execution_id = {{ executionId }}
  │
  └─[IF] workflowName != 'Photo of the Day'   ← don't alert on cron misses
      └─[HTTP Request] Telegram Bot API sendMessage
          chat_id: ADMIN_CHAT_ID
          text: "❌ Processing failed\nFile: {{ $json.file_path }}\n
                 Step: {{ lastNodeExecuted }}\n{{ error.message }}\n
                 n8n execution: {{ executionId }}"
```

---

## 5. Database Schema

Database: `homemediahub_db` on custom PostgreSQL instance (port 5432, image: `pgvector/pgvector:pg16`).

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE media_files (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path       TEXT NOT NULL UNIQUE,
    file_hash       TEXT NOT NULL,
    source          TEXT NOT NULL,           -- 'immich' | 'archive'
    immich_asset_id TEXT,
    mime_type       TEXT NOT NULL,
    file_size       BIGINT,
    width           INT,
    height          INT,
    duration_sec    FLOAT,                   -- video only
    taken_at        TIMESTAMPTZ,
    created_at      TIMESTAMPTZ DEFAULT NOW(),
    is_deleted      BOOLEAN DEFAULT FALSE,
    is_favorite     BOOLEAN DEFAULT FALSE,
    origin_class    TEXT,                    -- 'real_photo' | 'document' | 'meme' | 'screenshot' | 'artwork' | NULL (not yet classified)
    origin_confidence FLOAT                  -- CLIP classification confidence [0.0–1.0]
);
CREATE INDEX ON media_files (taken_at DESC);
CREATE INDEX ON media_files (file_hash);
CREATE INDEX ON media_files (is_deleted, taken_at DESC);
CREATE INDEX ON media_files (origin_class) WHERE origin_class IS NOT NULL;

CREATE TABLE media_metadata (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    latitude        FLOAT,
    longitude       FLOAT,
    address         TEXT,
    camera_make     TEXT,
    camera_model    TEXT,
    exif_json       JSONB
);
CREATE INDEX ON media_metadata (latitude, longitude);

CREATE TABLE media_tags (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    media_file_id   UUID NOT NULL REFERENCES media_files(id) ON DELETE CASCADE,
    tag             TEXT NOT NULL,
    confidence      FLOAT NOT NULL,
    model           TEXT NOT NULL            -- 'yolov8' | 'clip'
);
CREATE INDEX ON media_tags (media_file_id);
CREATE INDEX ON media_tags (tag);

CREATE TABLE media_embeddings (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    embedding       vector(512),
    created_at      TIMESTAMPTZ DEFAULT NOW()
);
CREATE INDEX ON media_embeddings
    USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100);

CREATE TABLE media_thumbnails (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    thumbnail_path  TEXT NOT NULL,
    width           INT,
    height          INT
);

CREATE TABLE albums (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name            TEXT NOT NULL,
    description     TEXT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

CREATE TABLE album_media (
    album_id        UUID REFERENCES albums(id) ON DELETE CASCADE,
    media_file_id   UUID REFERENCES media_files(id) ON DELETE CASCADE,
    added_at        TIMESTAMPTZ DEFAULT NOW(),
    PRIMARY KEY (album_id, media_file_id)
);

-- On-demand video transcodes (H.264 cache — recreatable from original)
CREATE TABLE video_transcodes (
    media_file_id   UUID PRIMARY KEY REFERENCES media_files(id) ON DELETE CASCADE,
    transcode_path  TEXT NOT NULL,       -- /mnt/usb-hdd/video-cache/<id>.mp4
    codec           TEXT NOT NULL,       -- 'h264'
    container       TEXT NOT NULL,       -- 'mp4'
    file_size       BIGINT,
    created_at      TIMESTAMPTZ DEFAULT NOW()
);

-- Photo series / burst groups
CREATE TABLE photo_series (
    id              UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    series_type     TEXT NOT NULL,       -- 'burst' (< 10 s apart) | 'event' (same location, < 4 h apart)
    taken_at_start  TIMESTAMPTZ,
    taken_at_end    TIMESTAMPTZ,
    latitude        FLOAT,
    longitude       FLOAT,
    cover_media_id  UUID REFERENCES media_files(id)
);

CREATE TABLE series_media (
    series_id       UUID REFERENCES photo_series(id) ON DELETE CASCADE,
    media_file_id   UUID REFERENCES media_files(id) ON DELETE CASCADE,
    PRIMARY KEY (series_id, media_file_id)
);

-- Processing job journal — one row per file attempt (written by n8n workflows)
CREATE TABLE processing_jobs (
    id               UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    file_path        TEXT NOT NULL,
    source           TEXT NOT NULL,           -- 'immich' | 'archive'
    status           TEXT NOT NULL,           -- 'running' | 'completed' | 'failed' | 'skipped'
    n8n_execution_id TEXT,                    -- links back to n8n execution history
    started_at       TIMESTAMPTZ DEFAULT NOW(),
    completed_at     TIMESTAMPTZ,
    failed_step      TEXT,                    -- name of the n8n node that failed
    error_message    TEXT,
    retry_count      INT DEFAULT 0
);
CREATE INDEX ON processing_jobs (status);
CREATE INDEX ON processing_jobs (file_path);
CREATE INDEX ON processing_jobs (started_at DESC);

-- ASP.NET Identity tables (schema: identity) — created by EF Core migrations
```

---

## 6. API Surface

### Tool endpoints (called by n8n — not exposed to browser)

| Method | Path | Input | Output |
|--------|------|-------|--------|
| POST | `/api/tools/hash` | `{ filePath }` | `{ hash }` |
| POST | `/api/tools/metadata` | `{ filePath }` | `{ mimeType, fileSize, width, height, takenAt, lat, lon, cameraModel, exifJson }` |
| POST | `/api/tools/thumbnail` | `{ filePath, mediaFileId }` | `{ thumbnailPath }` |
| GET  | `/api/tools/scan` | `?path=` query param | `{ files: [filePath, ...] }` — only files not yet in DB |

### Media (frontend)

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/media` | Paginated. Params: `page`, `pageSize`, `from`, `to`, `tag`, `favorite` |
| GET | `/api/media/{id}` | Single item with metadata and tags |
| GET | `/api/media/{id}/thumbnail` | Serve thumbnail (WebP, via nginx X-Accel-Redirect) |
| GET | `/api/media/{id}/original` | Stream original file (JPEG/HEIC/MOV as-is) |
| GET | `/api/media/{id}/stream` | Browser-compatible video stream. Checks `video_transcodes` cache; if absent, transcodes original → H.264 MP4 via ffmpeg on first request, caches result, then streams. Returns 404 for non-video media. |
| PATCH | `/api/media/{id}` | `{ isFavorite?, seriesId? }` |
| DELETE | `/api/media/{id}` | Soft delete |
| GET | `/api/media/review` | Returns media where `origin_class` is `meme`, `screenshot`, or `artwork`, sorted by `origin_confidence DESC`. Used by the Review page so the user can inspect and delete non-real photos. |
| DELETE | `/api/media/bulk` | `{ ids: [uuid, ...] }` — hard-deletes files from disk and DB. Used by the Review page bulk-delete action. |

### Search

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/search?q=` | Semantic CLIP search — calls AI sidecar for query embedding, then pgvector |
| GET | `/api/search/tags?tag=` | Tag filter |

### Albums

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/albums` | List |
| POST | `/api/albums` | `{ name, description }` |
| GET | `/api/albums/{id}` | Detail + media |
| POST | `/api/albums/{id}/media` | `{ mediaFileId }` |
| DELETE | `/api/albums/{id}/media/{mediaId}` | Remove from album |
| DELETE | `/api/albums/{id}` | Delete album (not media) |

### Admin / Observability

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/admin/jobs` | List processing jobs. Params: `status` (failed/running/completed), `page` |
| GET | `/api/admin/jobs/{id}` | Job detail including error message, failed step, n8n execution ID |
| POST | `/api/admin/jobs/{id}/retry` | Re-queue a failed job: calls n8n webhook for the file path |

### Series

| Method | Path | Description |
|--------|------|-------------|
| GET | `/api/series` | List all series/bursts |
| GET | `/api/series/{id}` | Series detail + member media |
| PATCH | `/api/series/{id}` | Update cover photo or series type |
| POST | `/api/series/{id}/media` | Manually add a photo to a series |
| DELETE | `/api/series/{id}/media/{mediaId}` | Remove from series |

### Auth

| Method | Path | Description |
|--------|------|-------------|
| POST | `/api/auth/login` | `{ username, password }` → JWT + refresh token |
| POST | `/api/auth/refresh` | Refresh token → new JWT |
| POST | `/api/auth/logout` | Invalidate refresh token |

**Auth model:** ASP.NET Core Identity, two fixed users (Mike, wife). No roles — both users share the full library with equal read-write access. JWT issued on login, 15-minute expiry, refresh token with 30-day sliding window.

### SignalR

| Hub | Events | Purpose |
|-----|--------|---------|
| `/hubs/library` | `media.added`, `media.deleted` | Push real-time library updates to frontend |

---

## 7. AI Pipeline

```
n8n Workflow 1 calls:

  POST ai-sidecar:8000/process { filePath }
      │
      ├─ Pillow decode (HEIC via pillow-heif, MP4 extracts first frame via ffmpeg)
      │
      ├─ YOLOv8n inference
      │     input:  640×640 resize
      │     output: [{label, confidence, bbox}]
      │     filter: confidence ≥ 0.40
      │
      └─ CLIP ViT-B/32 inference
            input:  224×224 center crop, normalized
            output: float32[512] (L2-normalized)

  → returns { tags: [{label, confidence, model: "yolov8"}],
               embedding: [512 floats] }

  POST ai-sidecar:8000/classify-origin { filePath }
      │
      └─ CLIP ViT-B/32 zero-shot classification
            candidates:
              "a photograph of people, places, or real life events"
              "a financial document, receipt, invoice, or payment confirmation"
              "a meme, joke, or internet image with text captions"
              "a screenshot of a phone or computer screen"
              "a painting, artwork, or illustration"
            → pick highest cosine similarity

  → returns { originClass: "real_photo"|"document"|"meme"|"screenshot"|"artwork",
               confidence: 0.0–1.0 }

  POST ai-sidecar:8000/embed/text { text: "birthday cake" }
  → returns { embedding: [512 floats] }

Semantic search:
  .NET API receives GET /api/search?q="birthday cake"
  → POST ai-sidecar:8000/embed/text
  → pgvector: SELECT ... ORDER BY embedding <=> $vec LIMIT 50

Origin classification (runs after /process in Workflow 1):
  → result stored in media_files.origin_class / origin_confidence
  → GET /api/media/review queries WHERE origin_class IN ('meme','screenshot','artwork')
  → user reviews flagged items in UI and bulk-deletes via DELETE /api/media/bulk
```

**Model choices:**
- **YOLOv8n**: 6 MB, ~50 ms/image on CPU, COCO-trained (80 object classes)
- **CLIP ViT-B/32**: 151 MB, enables natural-language search across both image and text in the same vector space
- Both models downloaded at Docker build time (baked in, no cold-start)

---

## 8. Volume Map

| Container | Mount | Host path | Access |
|-----------|-------|-----------|--------|
| immich-server | `/usr/src/app/upload` | `/mnt/usb-hdd/Photo/iPhone` | rw |
| api | `/photo` | `/mnt/usb-hdd/Photo` | ro |
| api | `/thumbnails` | `/mnt/usb-hdd/thumbnails` | rw |
| api | `/shared-temp` | `/tmp/homemediahub-work` | rw |
| api | `/video-cache` | `/mnt/usb-hdd/video-cache` | rw |
| ai-sidecar | `/photo` | `/mnt/usb-hdd/Photo` | ro |
| n8n | `/photo` | `/mnt/usb-hdd/Photo` | ro |
| n8n | `/shared-temp` | `/tmp/homemediahub-work` | rw |
| nginx | `/thumbs` | `/mnt/usb-hdd/thumbnails` | ro |
| postgres | `/var/lib/postgresql/data` | `/opt/homemediahub/data/postgres` | rw |
| immich-postgres | `/var/lib/postgresql/data` | `/opt/homemediahub/data/immich-postgres` | rw |
| geoservice-postgres | `/var/lib/postgresql/data` | `/opt/homemediahub/data/geoservice-postgres` | rw |
| n8n | `/home/node/.n8n` | `/opt/homemediahub/data/n8n` | rw |

**Samba (native Ubuntu):**
- `/mnt/usb-hdd/Photo` → share name `Photo` — readable and writable from Windows/macOS on LAN

---

## 9. Samba Configuration Sketch

```ini
# /etc/samba/smb.conf (additions)

[Photo]
   path = /mnt/usb-hdd/Photo
   browseable = yes
   read only = no
   guest ok = no
   valid users = @mediahub
   create mask = 0664
   directory mask = 0775
```

Users in the `mediahub` group get read-write access. macOS connects via Finder → Go → Connect to Server → `smb://<server-ip>/Photo`. Windows connects via `\\<server-ip>\Photo`.

---

## 10. Observability & Error Handling

### Layers of visibility

| Layer | What you see | How to access |
|-------|-------------|---------------|
| n8n Execution History | Every workflow run: each node's input/output JSON, error, timing. Green = ok, red = failed. Click any past execution to replay it. | n8n UI → Executions tab |
| `processing_jobs` table | All file attempts with status, failed step, error message, n8n execution ID. Queryable forever even after n8n history is pruned. | `SELECT * FROM processing_jobs WHERE status='failed'` |
| Telegram alerts | Instant notification when a file fails processing, with the file path, failing node name, and error message | Telegram admin chat |
| Docker container logs | Structured JSON logs from .NET API (Serilog) and Python AI sidecar. Each tool endpoint logs file path, duration, and any exception. | `docker compose logs -f api` or `docker compose logs -f ai-sidecar` |
| n8n + Error Workflow link | The `n8n_execution_id` in `processing_jobs` links directly to the n8n execution detail page for full node-by-node inspection | `https://<server>/n8n/execution/<id>` |

### Failure modes and responses

| Step | Failure type | Node setting | Effect |
|------|-------------|--------------|--------|
| Hash computation | Disk read error, file missing | Stop on fail | Job marked failed; Error Workflow fires |
| HEIC conversion (`heif-convert`) | Corrupt file, unsupported variant | Stop on fail | Job marked failed; original untouched |
| ffprobe / ffmpeg | Corrupt video, unsupported codec | Stop on fail | Job marked failed; original untouched |
| .NET metadata extraction | File unreadable, EXIF parse error | Stop on fail | Job marked failed |
| .NET thumbnail generation | ImageSharp error, disk full | Stop on fail | Job marked failed |
| Geocoding (geoservice) | geoservice down, timeout, Nominatim unavailable | Continue on fail | Photo stored without address; can be re-geocoded later |
| AI sidecar (YOLOv8 + CLIP) | OOM, model error, timeout | Continue on fail | Photo stored without tags/embedding; re-processable via Archive Scanner |
| Postgres INSERT | Constraint violation, connection lost | Stop on fail | Job marked failed; no partial record written |

### Re-processing failed jobs

Run Archive Scanner (Workflow 3) again at any time — it calls `GET /api/tools/scan` which returns only files not yet in `media_files`. Files that failed mid-pipeline (job status = `failed`, no row in `media_files`) will be picked up and retried. Files that succeeded (status = `completed`, row exists) are skipped via the hash dedup check.

For files that failed geocoding or AI (status = `completed` but missing enrichment), a future targeted re-enrichment workflow can query:
```sql
-- photos missing AI tags
SELECT mf.id, mf.file_path FROM media_files mf
LEFT JOIN media_embeddings me ON me.media_file_id = mf.id
WHERE me.media_file_id IS NULL AND mf.is_deleted = false;
```

### .NET API logging

Serilog configured with:
- Minimum level: `Information` in production, `Debug` available via env var override
- Output: structured JSON to stdout (captured by Docker)
- Each tool endpoint logs: `file_path`, `duration_ms`, outcome or exception type
- No personal/sensitive data in logs (no GPS coords, no file content)
