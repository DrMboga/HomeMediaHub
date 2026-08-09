# HomeMediaHub

Self-hosted home photo and video library running on a single Dell Precision 3620 (Ubuntu Server
24.04). Replaces a manual iPhone → MacBook → USB drive workflow with automated ingestion, AI
tagging, and semantic search. LAN-only.

## What it does

- **iPhone backup** via Immich — originals land directly on the server's photo library
- **Automated processing pipeline** — RabbitMQ 4 + MassTransit message-driven pipeline: dedup →
  HEIC/video conversion → EXIF extraction → thumbnail generation, with AI tagging and geocoding
  applied as async enrichment (**save-early**: photos are browsable in the gallery immediately;
  AI results fill in afterwards)
- **Natural-language search** ("birthday cake", "beach sunset") powered by CLIP ViT-B/32 + pgvector
- **Object detection tags** via YOLOv8n (runs fully local — no cloud ML), browsable on a Tags page
  that AND-filters across several tags at once
- **Browser UI** (React + TypeScript) for browsing, searching, albums, favorites, and an Admin
  page for triggering scans / watching pipeline progress / retrying failed jobs
- **Samba share** for direct file access from Windows/macOS on the LAN

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700 (4c/8t), 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | 2 TB HGST Ultrastar SATA HDD at `/mnt/storage` — `Photo/` holds ~190K files / 750 GB |
| Network | LAN-only |

## Stack

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (iPhone backup endpoint only, no Immich UI) |
| Message broker | RabbitMQ 4 (`rabbitmq:4-management-alpine`) |
| Orchestration | MassTransit 8.5.x — consumers + saga in `src/MediaWorker/` |
| API | .NET 10 ASP.NET Core minimal APIs + MediatR — frontend queries, JWT auth, admin/scan endpoints, owns media schema via EF migrations |
| Media worker | .NET 10 Worker Service — ingest consumers, enrichment saga, all file processing |
| Shared libs | `src/Contracts` (message records), `src/Data` (entity POCOs + shared EF mapping) |
| Frontend | React 18 + TypeScript + Vite + TanStack Query + react-leaflet |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n + CLIP ViT-B/32 (CPU); pika RabbitMQ consumer |
| Geocoding | .NET 10 GeoService — Nominatim cache; HTTP `/reverse` + MassTransit consumer |
| Media processing | ffmpeg, libheif (strukturag PPA), ExifTool, SixLabors.ImageSharp |
| Observability | OpenTelemetry metrics → Prometheus + Grafana |
| Reverse proxy | NGINX (X-Accel-Redirect thumbnails) |
| File sharing | Samba (native Ubuntu, not containerized) |

## Services

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 | single public ingress |
| api | 8080 | internal; `/metrics` scraped on docker network only |
| frontend | 3000 | internal |
| rabbitmq | 5672 AMQP · 15672 mgmt UI (LAN) · 15692 metrics | |
| media-worker | 9090 | Prometheus `/metrics` only — no HTTP API |
| ai-sidecar | 8000 health + `/embed/text` · 8001 metrics | |
| geoservice | 8001 | HTTP `/reverse` + `/metrics` + MassTransit consumer |
| prometheus | 9090 | loopback |
| grafana | 3001 | LAN, admin password from env |
| postgres | 5432 | `homemediahub_db` (app data + saga state) |
| immich-server | 2283 | iPhone app connects here |
| immich-postgres | 5433 · geoservice-postgres | 5434 · redis 6379 (Immich only) |

## Processing Pipeline

```
POST /api/admin/scan  or  monthly ScanScheduler (API)
  → ScanRequested → ScanConsumer → FileDiscovered per file (+ processing_jobs row)
  → IngestConsumer (prefetch 2): hash → dedup → convert → metadata → thumbnail
      → SAVE media row  ◄── photo visible in gallery immediately
      → MediaEnrichmentRequested (starts saga)
  → EnrichmentStateMachine fans out: AiAnalysisRequested + GeoLookupRequested (skipped if no GPS)
      AI sidecar (pika, prefetch 1) → raw JSON → AiResultBridgeConsumer → saga
      GeoService (MassTransit)      → GeoLookupCompleted → saga
  → both done → ApplyEnrichmentRequested → ApplyEnrichmentConsumer
      → UPDATE tags/embedding/origin/address, cleanup temp, job completed
```

Key invariants (full detail in [`docs/architecture.md`](docs/architecture.md)):
- AI failures never dead-letter — the sidecar always publishes a `success:false` result and ACKs,
  so the saga always completes.
- `processing_jobs` lifecycle: `queued → processing → enriching → completed | failed |
  skipped_duplicate`. Retry = republish `FileDiscovered(Reprocess: true)` from the Admin page.
- Idempotency: unique indexes on `media_files(file_path)` / `(file_hash)`; enrichment apply is an
  idempotent upsert.
- Full-library AI pass on this CPU takes days (~2–5 s/image × 190K files) — accepted; queue depth
  is the progress bar (visible in Grafana and the Admin page).

## Environments

Two isolated environments from one `docker-compose.yml`; only one runs at a time (shared ports).
Always pass `--env-file`.

| Env | Command | Photo library | Data root |
|-----|---------|---------------|-----------|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/storage/Photo` (SATA HDD) | `/opt/homemediahub/data/` |

## Quick Start

```bash
# Start an environment
docker compose --env-file .env.stage up -d

# Rebuild after code changes (build context is ./src for .NET services)
docker compose --env-file .env.stage build api media-worker ai-sidecar geoservice

# View logs
docker compose --env-file .env.stage logs -f media-worker
docker compose --env-file .env.stage logs -f api

# Connect to the database
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db

# Trigger a library scan (JWT required)
curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"
# or use the Admin page in the browser UI

# RabbitMQ mgmt UI: http://192.168.0.192:15672   Grafana: http://192.168.0.192:3001
```

## AI Pipeline

Both models run fully locally — no cloud APIs.

- **YOLOv8n** — object detection (80 COCO classes, ~50 ms/image on CPU, 6 MB model)
- **CLIP ViT-B/32** — semantic embeddings for natural-language search (151 MB model)

Models are baked into the `ai-sidecar` Docker image at build time. AI inference runs in a pika
worker (single-flight, `prefetch=1`, `torch.set_num_threads(4)`) — a pure FastAPI service now only
serves `/embed/text` (search) and `/health`.

## Authentication

ASP.NET Core Identity with two users (Mike + Badger). No roles — both share the full library with
equal access. JWT (15-minute expiry) + refresh token (30-day sliding window). Thumbnail/original/
display/stream endpoints are public by design (`<img>`/`<video>` can't send a JWT; GUID URLs,
LAN-only).

## Samba Share

`\\<server-ip>\Storage` → the photo library root. Read-write for the `mediahub` group.

- **Windows:** `\\<server-ip>\Storage`
- **macOS:** Finder → Go → Connect to Server → `smb://<server-ip>/Storage`

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — target architecture: message contracts, queue
  topology, saga design, Python↔MassTransit interop, `processing_jobs` lifecycle, schema ownership
- [`docs/delivery-plan.md`](docs/delivery-plan.md) — step-by-step implementation plan + history
- [`docs/fresh-stage-deploy.md`](docs/fresh-stage-deploy.md) — clean-slate stage rebuild
- [`docs/prod-first-run.md`](docs/prod-first-run.md) — switching stage → prod

## Design Decisions

- **RabbitMQ + MassTransit is the orchestrator; the API contains no processing logic** — only
  queries, auth, and publish-only endpoints (scan trigger, retry). Every pipeline stage is a
  durable queue with explicit prefetch: backpressure by construction.
- **Save-early, enrich-later.** The gallery never waits for AI.
- **MassTransit pinned to v8** (Apache-2.0); v9 is commercial.
- **EF Core over raw SQL.** EF migrations own the schema — no init SQL. Raw SQL only where EF
  can't express it (partial/ivfflat indexes, bulk `ExecuteUpdate/Delete`).
- **MediaWorker hosts all pipeline processing** (hash, HEIC/video conversion, EXIF, thumbnails,
  scan, dedup). On-demand HEIC display + video transcode stay in the API (interactive, cached).
- **AI Sidecar stays Python** (no .NET YOLO/CLIP); pika consumer, prefetch 1, single-flight
  inference. Ingest concurrency is 2 — one SATA spindle, more concurrency = seek thrash.
- **Thumbnails served by nginx** via `X-Accel-Redirect` — no .NET process per request.
- **Separate Postgres per concern.** **Samba native**, not containerized. **All AI local**,
  CPU-only; face recognition out of scope.

## Out of Scope

- Face / people recognition
- Remote access (LAN only)
- Browser upload (use the Samba share + scan)
- RAW camera files (CR2/ARW/NEF)
- Per-user library separation (shared pool, both users see everything)
- Telegram notifications and Photo-of-the-Day (dropped with n8n; future dead-letter consumer /
  scheduled worker)
