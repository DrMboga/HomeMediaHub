# HomeMediaHub

Self-hosted home photo and video library. Replaces the manual iPhone → MacBook → USB drive
workflow with automated ingestion, AI tagging, and semantic search. Runs on a single Dell
Precision 3620 (Ubuntu Server 24.04) as a Docker Compose stack. LAN-only.

## Current Status

**Phase 8 in progress — Steps 1–4 complete** (2026-08-01).

Phase 8 replaces n8n with a RabbitMQ 4 + MassTransit 8.x message-driven pipeline
(**save-early**: media rows written at ingest so the gallery is browsable immediately; AI + geo
applied later as enrichment updates via a saga). The server will be torn down and rebuilt fresh
— no DB migration needed.

**Read these before touching pipeline code:**
- `docs/architecture.md` — the **target** architecture: message contracts, queue topology, saga
  design, Python↔MassTransit interop, `processing_jobs` lifecycle, schema ownership.
- `docs/delivery-plan.md` — the **step-by-step implementation plan** (Steps 1–10) with per-step
  verification, plus history of Phases 1–7.5.

**What exists in the repo today (pre-migration):** n8n workflows (`infra/n8n/`,
`Dockerfile.n8n`) — now orphaned, since the `/api/tools/*` endpoints they called were deleted in
Step 4; AI sidecar as a FastAPI HTTP service. Both are **removed/replaced** during Phase 8 (n8n's
files in Step 7) — do not extend them.

**Done in Phase 8 so far:**
- **Step 1** — `src/Contracts` (10 message records, zero deps) and `src/Data` (entity POCOs +
  the single `MediaModelConfiguration.ConfigureMediaEntities` fluent mapping) shared libs.
  Entities moved from `Api/Data/` to `Data/Entities/` (namespace `Data.Entities`);
  `ProcessingJob` reworked (dropped `N8nExecutionId`, added `ScanId`/`MediaFileId`);
  `MediaFile.CreatedAt` mapped.
- **Step 2** — EF Core owns the media schema. `AppDbContext` now maps the media tables
  (`excludeFromMigrations: false`) + `HasPostgresExtension("vector")`; the `AddMediaSchema`
  migration creates all 11 media tables with unique/perf indexes + raw-SQL partials & ivfflat.
  Raw init SQL (`infra/postgres/init/`) deleted; compose `postgres` uses
  `POSTGRES_DB: homemediahub_db`. Verified against a throwaway pg16 pgvector container. The
  media tables change **only** via Api migrations.
- **Step 3** — `src/MediaWorker` (Worker Service) + `src/MediaWorker.Tests`. Processing services
  ported from `Api/Features/Tools/*` to `Services/` (MediatR stripped, plain classes); ingest/scan/
  bridge/apply/failure consumers; `EnrichmentStateMachine` + `WorkerDbContext` (owns
  `enrichment_sagas` via `AddEnrichmentSaga` in `__ef_migrations_history_worker`; `xmin` optimistic
  concurrency); MassTransit **8.5.10** bus (kebab formatter, raw-JSON AI endpoint, saga endpoint) +
  OTel Prometheus `:9090`; `Dockerfile` (context `./src`, libheif apt block). 54 tests green; full
  solution green (Api 24 · GeoService 10 · MediaWorker 54).
- **Step 4** — Api rework. `Features/Tools/`, `Endpoints/ToolEndpoints.cs`, `Features/Media/SaveMedia/`,
  and `POST /api/media` deleted — the worker writes media rows directly via EF Core now. Api gained a
  **publish-only** MassTransit bus (`AddMassTransit` + `UsingRabbitMq`, no consumers) used by
  `POST /api/admin/scan` (publishes `ScanRequested`) and job retry (publishes
  `FileDiscovered(Reprocess: true)`). Admin endpoints reworked: `GET /api/admin/pipeline/status`
  (counts scoped to the latest scan; `completedLastHour`/`staleCount` computed globally), `JobDto`
  gains `scanId`/`mediaFileId` (drops `n8nExecutionId`), `POST /api/admin/jobs/retry-failed` (bulk).
  `ScanScheduler` `BackgroundService` added (monthly, off by default via `Scan:Enabled`). OTel
  Prometheus metrics on `/metrics`. Semantic search accuracy fixed: `"a photo of {query}"` prompt
  template, cosine-distance cutoff (`Search:MaxCosineDistance`, default 0.8), no more `TakenAt`
  re-sort after the vector query. Dockerfile's duplicate COPY/ENTRYPOINT fixed. Full solution:
  Api 31 · GeoService 10 · MediaWorker 60, all green.

**What does NOT exist yet (do not assume it does):** MassTransit/RabbitMQ **wiring in GeoService**
(Step 5), the `rabbitmq`/`media-worker`/`prometheus`/`grafana` **compose services** (Step 7 —
MediaWorker and the reworked Api build but neither is wired into `docker-compose.yml` yet), the AI
sidecar pika worker (Step 6), frontend `/admin` page (Step 8). The old n8n workflows
(`infra/n8n/`, `Dockerfile.n8n`) are untouched until Step 7.

Phases 1–7.5 are complete: full React frontend (7 pages + Review), JWT auth, semantic search,
albums, videos, geo map, review/meme workflow, on-demand HEIC display + video transcoding.

## Server Access

| | |
|--|--|
| SSH | `ssh mike@192.168.0.192` |
| IP | `192.168.0.192` (static DHCP — reserved by MAC in the home router) |
| App directory | `/opt/homemediahub/app` |

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700 (4c/8t), 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | 2 TB HGST Ultrastar SATA HDD at `/mnt/storage` — `Photo/` holds ~190K files / 750 GB |
| Network | LAN-only, no public exposure |

## Stack (Phase 8 target)

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (iPhone backup endpoint only, no Immich UI) |
| Message broker | RabbitMQ 4 (`rabbitmq:4-management-alpine`) |
| Orchestration | MassTransit **8.5.x** (pinned `[8.*,9.0)` — v9 is commercial) — consumers + saga in `src/MediaWorker/` |
| API | .NET 10 ASP.NET Core minimal APIs + MediatR — frontend queries, JWT auth, admin/scan endpoints, owns media schema via EF migrations |
| Media worker | .NET 10 Worker Service — ingest consumers, enrichment saga, all file processing (hash/convert/metadata/thumbnail) |
| Shared libs | `src/Contracts` (message records, zero deps), `src/Data` (entity POCOs + shared EF mapping) |
| Frontend | React 18 + TypeScript + Vite + TanStack Query + react-leaflet |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n + CLIP ViT-B/32 (CPU); pika RabbitMQ consumer; FastAPI kept only for `/embed/text` + `/health` |
| Geocoding | .NET 10 GeoService — Nominatim cache; HTTP `/reverse` + MassTransit consumer |
| Media processing | ffmpeg, libheif (strukturag PPA), ExifTool, SixLabors.ImageSharp — inside `media-worker` (pipeline) and `api` (on-demand display/stream) |
| Observability | OpenTelemetry metrics (.NET), prometheus-client (Python), rabbitmq_prometheus → Prometheus + Grafana |
| Reverse proxy | NGINX (X-Accel-Redirect thumbnails) |
| File sharing | Samba (native Ubuntu, not containerized) |

## Service Ports (Phase 8 target)

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 | single public ingress |
| api | 8080 | internal; `/metrics` scraped on docker network only |
| frontend | 3000 | internal |
| rabbitmq | 5672 AMQP · 15672 mgmt UI (LAN) · 15692 metrics | |
| media-worker | 9090 | Prometheus /metrics only — no HTTP API |
| ai-sidecar | 8000 health + `/embed/text` · 8001 metrics | |
| geoservice | 8001 | HTTP `/reverse` + `/metrics` + MassTransit consumer |
| prometheus | 9090 | loopback |
| grafana | 3001 | LAN, admin password from env |
| postgres | 5432 | `homemediahub_db` (app data + saga state) |
| immich-server | 2283 | iPhone app connects here |
| immich-postgres | 5433 · geoservice-postgres | 5434 · redis 6379 (Immich only) |

## Processing Pipeline (Phase 8 target — summary)

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

Key invariants (full detail in `docs/architecture.md`):
- **AI failures never dead-letter**: the sidecar always publishes `AiAnalysisCompleted{success:false}` and ACKs, so the saga always completes.
- The saga never sits on the raw-JSON endpoint; `AiResultBridgeConsumer` re-publishes Python's plain JSON as a proper envelope.
- `processing_jobs` lifecycle: `queued → processing → enriching → completed | failed | skipped_duplicate`. Retry = republish `FileDiscovered(Reprocess: true)`.
- Idempotency: unique indexes on `media_files(file_path)` and `(file_hash)`; consumers treat 23505 as duplicate; enrichment apply is an idempotent upsert.
- Full-library AI pass on this CPU takes **days** (~2–5 s/image × 190K files) — that is accepted; queue depth is the progress bar.

## Databases

| Database | Host | Purpose |
|----------|------|---------|
| `homemediahub_db` | postgres:5432 | media, tags, embeddings, jobs, identity, `enrichment_sagas` |
| Immich DB | immich-postgres:5433 | Immich internal — never accessed by custom services |
| `geoservice_db` | geoservice-postgres:5434 | reverse-geocode cache (lat/lon rounded 4dp) |

Schema ownership (Phase 8): **EF Core migrations, no init SQL.** Api's `AppDbContext` owns
identity + media tables; MediaWorker's `WorkerDbContext` owns only `enrichment_sagas` in a
separate history table (`__ef_migrations_history_worker`). Media tables change **only** via Api
migrations.

## Environments

Two isolated environments from one `docker-compose.yml`; only one runs at a time (shared ports).
**Always pass `--env-file`.**

| Env | Command | Photo library | Data root |
|-----|---------|---------------|-----------|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/storage/Photo` (SATA HDD) | `/opt/homemediahub/data/` |

Data roots hold bind-mounted volumes: `postgres/`, `immich-postgres/`, `geoservice-postgres/`,
`rabbitmq/`, `prometheus/`, `grafana/`. Recreatable caches: `thumbnails/`, `video-cache/`,
`display-cache/`. Shared temp for conversions: `/tmp/homemediahub-{stage|prod}/` → `/shared-temp`.
Env vars incl. `RABBITMQ_USER`, `RABBITMQ_PASS`, `GRAFANA_PASSWORD` (Phase 8); `SERVER_IP` removed.

## Common Commands

```bash
# start / stop an environment
docker compose --env-file .env.stage up -d
docker compose --env-file .env.stage down

# logs
docker compose --env-file .env.stage logs -f media-worker   # or api, ai-sidecar, geoservice

# rebuild after code changes (build context is ./src for .NET services)
docker compose --env-file .env.stage build api media-worker ai-sidecar geoservice

# trigger a library scan (JWT required)
curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"

# pipeline progress
curl http://192.168.0.192/api/admin/pipeline/status -H "Authorization: Bearer $TOKEN"
# RabbitMQ mgmt UI: http://192.168.0.192:15672   Grafana: http://192.168.0.192:3001

# one-off psql
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db

# EF migrations (note the worker's separate history table)
dotnet ef migrations add <Name> --project src/Api
dotnet ef migrations add <Name> --project src/MediaWorker
```

## Authentication

ASP.NET Core Identity. Two users (Mike, Badger), no roles — shared library, equal access.
JWT (15 min) + refresh token (30-day sliding). Thumbnail/original/display/stream endpoints are
public by design (`<img>`/`<video>` can't send JWT; GUID URLs, LAN-only).

## Design Decisions

- **RabbitMQ + MassTransit is the orchestrator; the API contains no processing logic** — only
  queries, auth, and publish-only endpoints (scan trigger, retry). Every pipeline stage is a
  durable queue with explicit prefetch: backpressure by construction (the n8n design overloaded
  the API/AI sidecar with uncontrolled parallelism). This is the AWS-learning analogue:
  queues ≈ SQS/SNS, saga ≈ Step Functions, consumers ≈ Lambda.
- **Save-early, enrich-later.** Gallery never waits for AI.
- **MassTransit pinned to v8** (Apache-2.0); v9 is commercial.
- **EF Core over raw SQL.** EF navigation + `SaveChangesAsync` for persistence; `AsTracking()`
  or `ExecuteUpdateAsync` for mutations (global NoTracking!). Raw SQL only where EF can't
  express it (partial/ivfflat indexes in migrations, bulk `ExecuteUpdate/Delete`). EF
  migrations own the schema — no init SQL.
- **MediaWorker hosts all pipeline processing** (hash, HEIC via `heif-convert -q 90` +
  `exiftool -TagsFromFile <src> --Orientation <dest>`, ffmpeg frame extract, EXIF, WebP
  thumbnails, scan, dedup). On-demand HEIC display + video transcode **stay in the API**
  (interactive, cached).
- **AI Sidecar stays Python** (no .NET YOLO/CLIP); pika consumer, prefetch 1, single-flight
  inference, `torch.set_num_threads(4)`. Ingest concurrency is 2 — one SATA spindle, more
  concurrency = seek thrash.
- **Metrics via OpenTelemetry** (MassTransit v8 emits OTel Meters; prometheus-net can't see
  them). Custom Grafana pipeline dashboard; community MassTransit dashboards don't fit.
- **Thumbnails via nginx X-Accel-Redirect.** **Separate Postgres per concern.** **Samba
  native.** **All AI local, CPU-only.**

## Operational Notes

- **File copies must preserve mtime** (`rsync -av --times` or `scp -p`) — the `taken_at`
  fallback uses mtime; plain `scp` gives wrong dates for files without EXIF.
- **Linux has no reliable creation time** — use `LastWriteTimeUtc` (mtime) only;
  `CreationTimeUtc` on Linux is min(atime,mtime,ctime).
- **Stale Docker bind mounts**: a directory created on the host after container start may be
  invisible — `docker compose --env-file .env.stage up -d --force-recreate <service>`.
- **`docker compose down -v` does not wipe bind mounts** — delete the data dirs manually for a
  clean slate.
- **Npgsql on Docker bridge**: keep `Keepalive=30;Connection Idle Lifetime=300` +
  `EnableRetryOnFailure` — conntrack drops idle TCP after ~4 min.
- **ImageSharp**: `Image.IdentifyAsync` (header-only) for metadata, never `LoadAsync`.
  ExifTool subprocesses capped at 30 s with `Kill(entireProcessTree: true)`.

## Out of Scope

Face/people recognition · remote access (LAN only) · browser upload (Samba + scan) ·
RAW files (CR2/ARW/NEF) · per-user library separation · Telegram notifications and
Photo-of-the-Day (dropped with n8n; future dead-letter consumer / scheduled worker).

## Prior Art

Previous attempt in `C:/Users/Mike/Projects/PhotoLibrary` (Raspberry Pi 4, .NET, ML.NET):
lessons kept — EXIF extraction, Nominatim caching, HEIC via libheif/ffmpeg. Problems fixed
here — no iPhone auto-sync, poor ML.NET tagging, no semantic search.
