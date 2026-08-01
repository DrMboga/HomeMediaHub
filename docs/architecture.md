# HomeMediaHub — Architecture (Phase 8 Target)

> **Status:** This document describes the **target** architecture of the Phase 8 migration
> (n8n → RabbitMQ + MassTransit). Implementation is **in progress** — Steps 1–4 are done: the
> `Contracts`/`Data` shared libraries (§3/§8), EF Core ownership of the media schema (§9, the
> `AddMediaSchema` migration + deleted init SQL), the `MediaWorker` service (§6/§8 — consumers,
> the enrichment saga, ported processing services, `IJobStore`, MassTransit 8.5 bus), and the Api
> rework (§10 — `/api/tools/*` and `POST /api/media` removed, publish-only MassTransit bus, Admin
> endpoints on `processing_jobs`, `ScanScheduler`, OTel metrics, semantic search accuracy fix).
> Neither service is wired into `docker-compose.yml` yet (Step 7), so the **running** system is
> still n8n-orchestrated. The step-by-step migration guide lives in [`delivery-plan.md`](delivery-plan.md).

## 1. System Context

```
                 ┌─────────────────────────────────────────────────────────────────────┐
                 │              Dell Precision 3620 · i7-6700 (4c/8t) · 32 GB RAM       │
                 │              Ubuntu Server 24.04 · Docker Compose · LAN-only         │
                 │                                                                      │
 ┌──────────┐    │  ┌──────────────────────────────────────────────────────────────┐   │
 │ iPhone   │────┼─►│  Immich  :2283   (mobile backup endpoint only, no UI used)   │   │
 │ (family) │    │  └──────────────────────┬───────────────────────────────────────┘   │
 └──────────┘    │     writes originals to │ /photo/iPhone/...                          │
                 │                         │                                            │
 ┌──────────┐    │  ┌──────────────────────▼───────────────────────────────────────┐   │
 │ Browser  │────┼─►│  NGINX  :80                                                  │   │
 │ (LAN)    │    │  │   /         → frontend (React)    :3000                      │   │
 └──────────┘    │  │   /api/     → .NET API            :8080                      │   │
                 │  │   /thumbs/  → filesystem direct (X-Accel-Redirect)           │   │
                 │  │   /immich/  → immich-server       :2283                      │   │
                 │  └──────────────────────────────────────────────────────────────┘   │
 ┌──────────┐    │                                                                      │
 │ Windows/ │────┼─►│  Samba (native Ubuntu, not containerized)                        │
 │ macOS    │    │  │  \\server\Storage → /mnt/storage                                 │
 └──────────┘    │                                                                      │
                 │  ┌──────────────────────────────────────────────────────────────┐   │
                 │  │  .NET 10 API  :8080                                          │   │
                 │  │   • REST for the React frontend + JWT auth                   │   │
                 │  │   • POST /api/admin/scan → publishes ScanRequested           │   │
                 │  │   • ScanScheduler BackgroundService (cron 0 2 1 * *)         │   │
                 │  │   • Owns the media DB schema (EF Core migrations)            │   │
                 │  │   • OTel Prometheus /metrics                                 │   │
                 │  └────────────┬──────────────────────────┬──────────────────────┘   │
                 │    publishes  │                          │ HTTP POST /embed/text     │
                 │               ▼                          ▼                           │
                 │  ┌──────────────────────────┐  ┌────────────────────────────────┐   │
                 │  │  RabbitMQ 4  :5672 AMQP  │  │  AI Sidecar (Python 3.11)      │   │
                 │  │  :15672 management UI    │  │   :8000 FastAPI /health,       │   │
                 │  │  :15692 Prometheus       │  │         /embed/text            │   │
                 │  └──────┬───────────────────┘  │   :8001 Prometheus metrics     │   │
                 │         │ messages             │   pika consumer (main thread)  │   │
                 │         ▼                      │   YOLOv8n + CLIP ViT-B/32 (CPU)│   │
                 │  ┌───────────────────────────┐ └────────────────────────────────┘   │
                 │  │  MediaWorker (.NET 10     │                                       │
                 │  │  Worker Service)  :9090   │  ┌────────────────────────────────┐   │
                 │  │   ScanConsumer            │  │  GeoService (.NET 10)  :8001   │   │
                 │  │   IngestConsumer          │  │   HTTP GET /reverse (kept)     │   │
                 │  │   EnrichmentStateMachine  │  │   GeoLookupConsumer            │   │
                 │  │   AiResultBridgeConsumer  │  │   (MassTransit)                │   │
                 │  │   ApplyEnrichmentConsumer │  │   Nominatim cache, 1 req/s     │   │
                 │  │   JobFailureConsumer      │  └────────────────────────────────┘   │
                 │  └───────────────────────────┘                                       │
                 │                                                                      │
                 │  ┌──────────────────────┐  ┌───────────────────────────────────┐    │
                 │  │  PostgreSQL  :5432   │  │  Prometheus :9090 · Grafana :3001 │    │
                 │  │  pgvector:pg16       │  │  scrapes api, media-worker,       │    │
                 │  │  homemediahub_db     │  │  geoservice, ai-sidecar, rabbitmq │    │
                 │  │  (app data + sagas)  │  └───────────────────────────────────┘    │
                 │  └──────────────────────┘                                            │
                 │  ┌──────────────────────┐  ┌──────────────────────┐  ┌────────────┐ │
                 │  │  GeoService Postgres │  │  Immich Postgres     │  │ Redis :6379│ │
                 │  │  :5434 geoservice_db │  │  :5433               │  │ (Immich)   │ │
                 │  └──────────────────────┘  └──────────────────────┘  └────────────┘ │
                 │                                                                      │
                 │  SATA HDD /mnt/storage/                                              │
                 │   ├── Photo/           ← single source of truth (archive + iPhone/)  │
                 │   ├── thumbnails/      ← 400px WebP, recreatable                     │
                 │   ├── video-cache/     ← H.264 transcodes, recreatable               │
                 │   └── display-cache/   ← JPEG from HEIC, recreatable                 │
                 └─────────────────────────────────────────────────────────────────────┘
```

## 2. Processing Pipeline (save-early)

The pipeline's defining property: **a photo becomes visible in the gallery as soon as ingest
finishes** (hash, convert, metadata, thumbnail, DB row). AI tagging and reverse geocoding are
applied later as an enrichment *update*. On a CPU-only box the full-library AI pass takes days;
save-early means the library is browsable on day one while tags, origin badges, and addresses
fill in gradually.

```
POST /api/admin/scan   or   ScanScheduler (monthly cron 0 2 1 * *, in API)
  └─ publish ScanRequested
        ▼
  ScanConsumer                                                 [queue: media-scan]
        enumerate ${path} recursively (supported extensions);
        exclude paths already in media_files OR with an active processing_job;
        INSERT processing_jobs (status=queued, scan_id) in ~1000-row batches;
        publish FileDiscovered per file
        ▼
  IngestConsumer                              [queue: media-ingest, prefetch 2/concurrency 2]
        job → processing
        SHA-256 hash → dedup by file_hash → duplicate? job → skipped_duplicate, stop
        convert:  HEIC → /shared-temp/{mediaFileId}.jpg (heif-convert + exiftool)
                  video → single frame JPEG (ffmpeg)
        extract metadata (ImageSharp EXIF / exiftool -json for video)
        generate 400px WebP thumbnail (ImageSharp)
        SAVE MediaFile + MediaMetadata + MediaThumbnail   ◄── visible in gallery NOW
        job → enriching
        publish MediaEnrichmentRequested                      (starts the saga)
        ▼
  EnrichmentStateMachine (saga)                     [queue: media-enrichment-saga]
        Initially: publish AiAnalysisRequested;
                   GPS present? publish GeoLookupRequested : GeoDone = true
        ▼ fan-out (parallel)
  ┌─────────────────────────────────┐   ┌─────────────────────────────────────┐
  │ AI Sidecar (Python + pika)      │   │ GeoService (MassTransit consumer)   │
  │ [queue: ai-analysis, prefetch 1]│   │ [queue: geo-lookup, concurrency 1]  │
  │ YOLOv8n tags + CLIP embedding   │   │ cache lookup → Nominatim fallback   │
  │ + origin classification         │   │ publish GeoLookupCompleted          │
  │ publish raw JSON →              │   └─────────────────────────────────────┘
  │   ai-analysis-completed         │
  └─────────────────────────────────┘
        ▼
  AiResultBridgeConsumer            [queue: ai-analysis-completed, raw-JSON endpoint]
        re-publish AiAnalysisCompleted as a normal MassTransit envelope
        ▼
  saga: when AiDone && GeoDone → publish ApplyEnrichmentRequested → Finalize (row deleted)
        ▼
  ApplyEnrichmentConsumer                        [queue: media-apply-enrichment]
        UPDATE media_files (origin_class, origin_confidence)
        upsert media_embeddings (vector(512), ON CONFLICT DO UPDATE)
        replace media_tags for the media id
        UPDATE media_metadata.address
        delete /shared-temp work file (only if AI succeeded — kept for retry otherwise)
        job → completed   (or failed + failed_step = ai|geo if a part failed)
```

Failures anywhere in ingest publish `MediaProcessingFailed`; it and `Fault<FileDiscovered>`
(retry-exhausted messages) are consumed by `JobFailureConsumer` [queue: `media-job-status`],
which marks the job `failed` with `failed_step` and `error_message`. MassTransit `_error`
queues remain as a human-inspectable backstop, not a data path.

**Ingest strategy:** No Immich webhooks. Immich writes files to disk; scans (manual or monthly)
discover them. The API contains no processing logic — it only publishes `ScanRequested`.

## 3. Message Contracts — `src/Contracts/Messages/`

Pure C# records, `net10.0` classlib, **zero NuGet dependencies**. Saga correlation uses
`CorrelateById(ctx => ctx.Message.CorrelationId)` — contracts do not implement
`CorrelatedBy<Guid>` so the library stays dependency-free. `CorrelationId == MediaFileId`.

```csharp
record ScanRequested(Guid ScanId, string Path, string Source, string TriggeredBy, DateTimeOffset RequestedAt);

record FileDiscovered(Guid JobId, Guid ScanId, string FilePath, string Source,
    bool Reprocess, DateTimeOffset DiscoveredAt);

record MediaEnrichmentRequested(          // starts the saga
    Guid CorrelationId,                   // = MediaFileId
    Guid JobId,
    string OriginalFilePath,
    string AnalyzeFilePath,               // /shared-temp/{id}.jpg for HEIC/video, else original
    string? WorkFilePath,                 // temp file to clean up, null if none created
    bool HasGps, double? Lat, double? Lon);

record AiAnalysisRequested(Guid CorrelationId, string FilePath);

record AiTag(string Tag, double Confidence, string Model);

record AiAnalysisCompleted(Guid CorrelationId, bool Success,
    AiTag[] Tags, float[] Embedding, string? OriginClass, double? OriginConfidence, string? Error);

record GeoLookupRequested(Guid CorrelationId, double Lat, double Lon);

record GeoLookupCompleted(Guid CorrelationId, bool Success, string? Address, string? Error);

record ApplyEnrichmentRequested(Guid MediaFileId, Guid JobId, string? WorkFilePath,
    bool AiSuccess, AiTag[] Tags, float[] Embedding, string? OriginClass, double? OriginConfidence, string? AiError,
    bool GeoSuccess, string? Address, string? GeoError);

record MediaProcessingFailed(Guid? JobId, Guid? MediaFileId, string FilePath,
    string Stage, string Error, DateTimeOffset OccurredAt);
```

**AI failure contract:** the sidecar never dead-letters an inference failure. Any error
(missing file, unreadable image, inference exception) produces
`AiAnalysisCompleted { Success = false, Error = ... }` and an ACK. One publish path in Python,
one saga event in .NET, and the saga is guaranteed to complete for every file.
`MediaProcessingFailed` is used only by .NET consumers for ingest-stage failures.

## 4. Queue Topology

All .NET services use `SetKebabCaseEndpointNameFormatter()`. The two Python-facing names are
pinned explicitly (`cfg.Message<AiAnalysisRequested>(m => m.SetEntityName("ai-analysis-requested"))`);
everything else uses MassTransit conventions. Exchanges are durable fanout; queues are durable
classic queues (single node — quorum queues buy nothing here).

| Queue | Consumer (service) | Prefetch / Concurrency | Notes |
|---|---|---|---|
| `media-scan` | ScanConsumer (MediaWorker) | 1 / 1 | one scan at a time |
| `media-ingest` | IngestConsumer (MediaWorker) | **2 / 2** | deliberately low: hashing 750 GB off one SATA spindle — more concurrency = seek thrash, not throughput |
| `media-enrichment-saga` | EnrichmentStateMachine (MediaWorker) | 8 / 8 | `UseMessageRetry(Intervals: 100ms, 500ms, 1s, 5s, 10s)` + `UseInMemoryOutbox` — absorbs optimistic-concurrency faults |
| `ai-analysis` | pika worker (AI Sidecar) | 1 / 1 | declared + bound by Python; `torch.set_num_threads(4)` |
| `ai-analysis-completed` | AiResultBridgeConsumer (MediaWorker) | 4 | **raw-JSON endpoint** — see §5 |
| `geo-lookup` | GeoLookupConsumer (GeoService) | 1 / 1 | Nominatim hard limit 1 req/s; cache hits dominate after warm-up |
| `media-apply-enrichment` | ApplyEnrichmentConsumer (MediaWorker) | 4 / 4 | pure DB updates, cheap |
| `media-job-status` | JobFailureConsumer (MediaWorker) | 4 | `MediaProcessingFailed` + `Fault<FileDiscovered>` |

**Backpressure model:** a scan of 190K files publishes 190K `FileDiscovered` messages up front
(RabbitMQ stores them on disk — tens of MB, not a problem). Consumers pull at their own pace via
prefetch. Nothing ever overloads the AI sidecar again: it takes exactly one message at a time.
Queue depth *is* the progress bar (visible in the management UI and Grafana).

## 5. Python ↔ MassTransit Interop

The AI sidecar is plain pika, not MassTransit, so the two directions need explicit treatment:

**.NET → Python (AiAnalysisRequested).** MassTransit publishes its standard JSON envelope
(content-type `application/vnd.masstransit+json`) to the fanout exchange
`ai-analysis-requested`:

```json
{
  "messageId": "…", "correlationId": "…",
  "messageType": ["urn:message:Contracts.Messages:AiAnalysisRequested"],
  "message": { "correlationId": "3f2a…", "filePath": "/shared-temp/3f2a….jpg" },
  "sentTime": "…", "headers": {}, "host": { … }
}
```

Python declares the exchange (fanout, durable), declares + binds queue `ai-analysis`,
sets `basic_qos(prefetch_count=1)`, and reads `json.loads(body)["message"]` (camelCase keys).

**Python → .NET (AiAnalysisCompleted).** Python publishes **plain JSON** — no envelope —
to the fanout exchange `ai-analysis-completed` with `content_type='application/json'`,
`delivery_mode=2`:

```json
{ "correlationId": "3f2a…", "success": true, "tags": [{"tag":"dog","confidence":0.91,"model":"yolov8n"}],
  "embedding": [0.012, …], "originClass": "photo", "originConfidence": 0.93, "error": null }
```

The .NET side receives it on a dedicated raw-JSON endpoint:

```csharp
cfg.ReceiveEndpoint("ai-analysis-completed", e =>
{
    e.ConfigureConsumeTopology = false;                    // no urn-based bindings
    e.DefaultContentType = new ContentType("application/json");
    e.UseRawJsonDeserializer(isDefault: true);
    e.Bind("ai-analysis-completed", b => { b.ExchangeType = "fanout"; b.Durable = true; });
    e.ConfigureConsumer<AiResultBridgeConsumer>(context);  // the ONLY consumer here
});
```

`AiResultBridgeConsumer : IConsumer<AiAnalysisCompleted>` does one thing:
`await context.Publish(context.Message)` — re-publishing the payload as a proper MassTransit
envelope that the saga consumes normally. **The saga must never sit on the raw endpoint
directly**: raw JSON carries no `messageType`, and a saga endpoint registers multiple event
types — the lenient deserializer could bind the payload ambiguously. The bridge is a
one-message-type anti-corruption layer (the AWS analogue: a Lambda normalizing webhook input
before it hits Step Functions).

## 6. Enrichment Saga

`EnrichmentStateMachine` / `EnrichmentState` in `src/MediaWorker/Sagas/`. This is the
Step-Functions-style coordinator: it exists because AI and geo run in parallel and finish in
any order, and something must decide "both are done — apply the update".

**State row** (`enrichment_sagas` table in `homemediahub_db`, EF-persisted):
`CorrelationId` (= MediaFileId, PK), `CurrentState`, `Version` (`uint`, mapped
`.IsRowVersion()` → Npgsql `xmin` system column — optimistic concurrency without an extra
column), `JobId`, `WorkFilePath`, `AiDone`, `AiSuccess`, `TagsJson`, `EmbeddingJson`,
`OriginClass`, `OriginConfidence`, `AiError`, `GeoDone`, `GeoSuccess`, `Address`, `GeoError`,
`StartedAt`.

**Transitions:**

| State | Event | Action | Next |
|---|---|---|---|
| Initial | `MediaEnrichmentRequested` | store JobId/WorkFilePath; publish `AiAnalysisRequested`; if HasGps publish `GeoLookupRequested` else `GeoDone = true` | Enriching |
| Enriching | `AiAnalysisCompleted` | store AI result (success **or** failure), `AiDone = true`; if `GeoDone` → publish `ApplyEnrichmentRequested`, **Finalize** | Enriching / Final |
| Enriching | `GeoLookupCompleted` | symmetric | Enriching / Final |
| Enriching | `MediaEnrichmentRequested` (retry re-trigger) | re-publish whichever of AI/geo is not yet done | Enriching |

- `SetCompletedWhenFinalized()` — the saga row is deleted on finalize; the table only ever
  holds in-flight items (bounded by the AI backlog during a big scan).
- `.OnMissingInstance(m => m.Discard())` on the AI/Geo events — duplicate deliveries after
  finalize are dropped silently.
- Repository: `EntityFrameworkRepository` with `ConcurrencyMode.Optimistic` +
  `ExistingDbContext<WorkerDbContext>()`. Endpoint-level `UseMessageRetry` retries the losing
  side when AI and geo results land simultaneously.
- **No scheduler-based timeout in this phase** (would require the RabbitMQ delayed-message
  -exchange plugin). The always-publish AI contract (§3) makes orphaned sagas unlikely; the
  Admin page surfaces jobs stuck in `enriching` > 24 h as *stale* with a retry button.
  Accepted, revisitable risk.

## 7. Job Tracking — `processing_jobs`

The pipeline's user-facing ledger (n8n's execution list replacement). One row per discovered
file per scan.

**Lifecycle:** `queued → processing → enriching → completed | failed | skipped_duplicate`

| Transition | Written by |
|---|---|
| insert `queued` (+ `scan_id`) | ScanConsumer (or retry endpoint resets to `queued`) |
| → `processing` | IngestConsumer (start) |
| → `skipped_duplicate` | IngestConsumer (dedup hit) |
| → `enriching` (+ `media_file_id`) | IngestConsumer (after save + saga start) |
| → `completed` | ApplyEnrichmentConsumer (both parts OK) |
| → `failed` (+ `failed_step` = `ingest`/`ai`/`geo`, `error_message`) | JobFailureConsumer (ingest faults) or ApplyEnrichmentConsumer (enrichment partial failure — the media row stays visible in the gallery) |

**Retry — one uniform path.** `POST /api/admin/jobs/{id}/retry` resets the job to `queued`,
increments `retry_count`, and publishes `FileDiscovered(Reprocess: true)`. IngestConsumer with
`Reprocess = true`: if a media row already exists for the path it reuses the Id, regenerates
the converted temp file / thumbnail if missing, and goes straight to publishing
`MediaEnrichmentRequested`; otherwise it runs the full ingest. This one flag covers both
"failed before save" and "failed during enrichment", and the saga tolerates being re-triggered
(§6).

**Idempotency & dedup (at-least-once delivery is a given):**
- Unique indexes on `media_files(file_path)` and `media_files(file_hash)` — the database is
  the final arbiter; `IngestConsumer` catches `PostgresException 23505` and treats it as a
  duplicate.
- `ApplyEnrichmentConsumer` is a pure idempotent UPDATE/upsert — consuming the same message
  twice is harmless.
- ScanConsumer excludes paths with an active job (`queued`/`processing`/`enriching`), plus a
  partial unique index `processing_jobs(file_path) WHERE status IN ('queued','processing','enriching')`
  as a backstop against rescans while files are in flight.

## 8. Projects & Code Layout

```
src/
├── Contracts/            message records only, zero deps (§3)
├── Data/                 shared entity POCOs + one copy of the EF fluent mapping
│                         MediaModelConfiguration.ConfigureMediaEntities(builder, excludeFromMigrations)
├── Api/                  frontend-facing REST + JWT auth + admin endpoints + ScanScheduler
│                         AppDbContext (Identity + media entities) — OWNS media schema migrations
├── MediaWorker/          Worker Service: consumers, saga, ported processing services
│   ├── Services/         FileHashService, DedupService, FileConvertService,
│   │                     MetadataService, ThumbnailService, ScanService
│   ├── Consumers/        ScanConsumer, IngestConsumer, AiResultBridgeConsumer,
│   │                     ApplyEnrichmentConsumer, JobFailureConsumer
│   ├── Sagas/            EnrichmentState, EnrichmentStateMachine
│   └── WorkerDbContext   media entities ExcludeFromMigrations; owns ONLY enrichment_sagas,
│                         separate history table __ef_migrations_history_worker
├── MediaWorker.Tests/    xunit + NSubstitute + EFCore.InMemory + MassTransit test harness
├── GeoService/           HTTP /reverse + GeoLookupConsumer; own DB (geoservice_db)
├── AiSidecar/            inference.py (models) + app.py (FastAPI) + worker.py (pika entrypoint)
├── Frontend/             React + TS SPA (adds /admin page)
├── Api.Tests/, GeoService.Tests/
```

**Version pins that matter:**
- **MassTransit 8.5.x** — pinned `[8.*,9.0)`. MassTransit v9 moved to a commercial license;
  v8 is the last Apache-2.0 major. 8.x targets net8.0 and runs on net10.
- **RabbitMQ 4** (`rabbitmq:4-management-alpine`) — 3.x is EOL.
- OTel Prometheus exporter packages are pre-release — pin exact versions.

## 9. Database Schema

| Database | Host | Purpose |
|---|---|---|
| `homemediahub_db` | postgres:5432 | media, tags, embeddings, jobs, identity, saga state |
| Immich DB | immich-postgres:5433 | Immich internal — never touched by custom services |
| `geoservice_db` | geoservice-postgres:5434 | geo cache (lat/lon rounded 4dp → address) |

**Schema ownership moves to EF Core migrations** (the raw `infra/postgres/init/*.sql` scripts
are deleted; the database itself is created by `POSTGRES_DB=homemediahub_db`):

- **Api's `AppDbContext`** owns Identity (existing `CreateIdentitySchema` migration) **and** the
  media schema (new `AddMediaSchema` migration): `media_files`, `media_metadata`, `media_tags`,
  `media_embeddings` (`vector(512)` via `HasPostgresExtension("vector")` + Pgvector), `media_thumbnails`,
  `albums`, `album_media`, `photo_series`, `series_media`, `video_transcodes`, `processing_jobs`.
  Notable in `AddMediaSchema`:
  - **unique** `media_files(file_path)` and `media_files(file_hash)`
  - `processing_jobs`: `n8n_execution_id` dropped; `scan_id`, `media_file_id` added; indexes on
    `(status)` and `(scan_id, status)`; partial unique on `(file_path)` for active statuses (raw
    `migrationBuilder.Sql`)
  - ivfflat index on embeddings (`vector_cosine_ops, lists=100`) via `migrationBuilder.Sql` —
    created on an empty table, so **recreate it once after the full archive import** for recall
  - `media_files.created_at` mapped (existed in old init SQL, was unmapped)
- **MediaWorker's `WorkerDbContext`** maps the same entities via the shared
  `ConfigureMediaEntities(…, excludeFromMigrations: true)` and owns exactly one migration:
  `AddEnrichmentSaga` (the `enrichment_sagas` table) recorded in its own history table
  `__ef_migrations_history_worker`. Two migrators, one database, zero collisions — media tables
  only ever change via Api migrations.
- Startup ordering is enforced by compose healthchecks: postgres → api (migrates identity +
  media) → media-worker (migrates saga table).

## 10. API Surface (after Phase 8)

Frontend-facing endpoints are unchanged from Phase 6/7.5 (media list/detail/stats/on-this-day/
geo/review, semantic + tag search, albums, series, auth, thumbnail/original/display/stream).
On-demand HEIC display conversion and video transcoding **stay in the API** — they are
interactive request-path features, not pipeline steps.

**Removed:** all `/api/tools/*` endpoints, `POST /api/media` (SaveMedia) — the worker writes
directly via EF Core.

**Admin (JWT-protected):**

| Endpoint | Behavior |
|---|---|
| `POST /api/admin/scan` `{path?}` | publish `ScanRequested` (default path `/photo`) → `202 { scanId }` |
| `GET /api/admin/pipeline/status` | `{ scanId, startedAt, counts: {queued, processing, enriching, completed, failed, skippedDuplicate}, total, completedLastHour, staleCount }` — computed from `processing_jobs` |
| `GET /api/admin/jobs?status=&page=&pageSize=` | job list (DTO: `mediaFileId`, `scanId`; no more `n8nExecutionId`) |
| `GET /api/admin/jobs/{id}` | job detail |
| `POST /api/admin/jobs/{id}/retry` | reset to `queued`, `retry_count++`, publish `FileDiscovered(Reprocess: true)` |
| `POST /api/admin/jobs/retry-failed` | bulk retry of all failed jobs |

The frontend `/admin` page consumes these: scan trigger, polling progress card
(5 s `refetchInterval`), failed-jobs table with retry.

## 11. Observability

- **.NET services (api, media-worker, geoservice):** OpenTelemetry Metrics with the Prometheus
  exporter. MassTransit v8 emits its instrumentation as **OTel Meters** (`AddMeter("MassTransit")`)
  — *not* prometheus-net; the older MassTransit.Prometheus package is dead. Api and GeoService
  expose `/metrics` on their existing HTTP ports (nginx does not route them — scraping happens
  on the docker network only); MediaWorker has no HTTP API, so it uses the Prometheus
  HttpListener exporter on `:9090`.
- **AI sidecar:** `prometheus-client` on `:8001` — `ai_messages_total{status="ok|error"}`
  counter and `ai_inference_seconds` histogram.
- **RabbitMQ:** `rabbitmq_prometheus` plugin on `:15692` (queue depths, rates, acks).
- **Prometheus** (`:9090`, loopback-bound) scrapes all five targets at 15 s.
- **Grafana** (`:3001`, LAN) with provisioned datasource + one **custom pipeline dashboard**
  (`infra/grafana/dashboards/pipeline.json`): queue depth per queue, publish/deliver rates,
  MassTransit consume duration & faults, AI inference P95, jobs completed/hour, error-queue
  depth. Community dashboard IDs from the MassTransit-Prometheus era (15467) don't match OTel
  metric names — don't use them.

## 12. Environments

Two isolated environments from one `docker-compose.yml`, selected by env file. Only one runs at
a time (shared ports).

| Environment | Command | Photo library | Data root |
|---|---|---|---|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/storage/Photo` (SATA HDD) | `/opt/homemediahub/data/` |

Env vars (examples in `.env.stage.example` / `.env.prod.example`): `COMPOSE_PROJECT_NAME`,
`POSTGRES_USER/PASSWORD`, `IMMICH_DB_USER/PASSWORD`, `GEOSERVICE_DB_PASSWORD`,
`MIKE_PASSWORD`, `BADGER_PASSWORD`, `PHOTO_DIR`, `THUMBNAILS_DIR`, `VIDEO_CACHE_DIR`,
`DISPLAY_CACHE_DIR`, `WORK_DIR`, `DATA_DIR`, **`RABBITMQ_USER`, `RABBITMQ_PASS`,
`GRAFANA_PASSWORD`** (new). `SERVER_IP` is removed (only n8n's webhook URL used it).

## 13. Design Decisions

- **RabbitMQ + MassTransit replaces n8n as the orchestrator.** The old design hid the work
  queue inside n8n's engine with no observability and uncontrolled W1 parallelism that
  overloaded the API and AI sidecar. Now every stage is an explicit durable queue with its own
  prefetch — backpressure by construction, progress visible in RabbitMQ mgmt UI and Grafana.
  This mirrors the AWS patterns being studied: SQS/SNS ≈ RabbitMQ exchanges/queues,
  Step Functions ≈ MassTransit saga, Lambda ≈ consumers.
- **Save-early, enrich-later.** Ingest (fast, I/O-bound) and enrichment (slow, CPU-bound) are
  decoupled; the gallery never waits for AI.
- **MassTransit pinned to v8.** v9 is commercial; v8 is Apache-2.0.
- **MediaWorker hosts all pipeline processing.** Hashing, HEIC/video conversion
  (`heif-convert` + `exiftool -TagsFromFile --Orientation`, `ffmpeg` frame extract via
  `Process.Start`), metadata, thumbnails, scanning, dedup — ported from the old `/api/tools/*`
  handlers. Its Docker image installs the same libheif PPA package set as the API image.
- **AI Sidecar stays Python** (YOLOv8n + CLIP have no .NET equivalents), gains a pika consumer
  loop; FastAPI is kept only for `/embed/text` (semantic search) + `/health` on a side thread.
  Inference is single-flight (`prefetch 1`, threading lock, `torch.set_num_threads(4)`).
- **Bridge consumer as anti-corruption layer** between raw-JSON Python output and typed
  MassTransit envelopes (§5).
- **EF Core over raw SQL** — including schema ownership now (§9). Raw SQL only where EF cannot
  express it (partial/ivfflat indexes inside migrations, `ExecuteUpdateAsync` bulk ops).
- **On-demand display/stream conversion stays in the API** — interactive latency, cached on
  disk, not pipeline work.
- **Thumbnails served by nginx** via `X-Accel-Redirect` — no .NET/Postgres per request.
- **Separate Postgres per concern** (app, Immich, geo cache). **Samba native**, not
  containerized. **All AI local**, CPU-only; face recognition out of scope.

## 14. Out of Scope

- Face / people recognition
- Remote access (LAN only) · web upload (use Samba + scan)
- RAW camera files (CR2/ARW/NEF)
- Per-user library separation (shared pool)
- Telegram notifications / Photo-of-the-Day posts (dropped with n8n; future: dead-letter
  consumer or scheduled worker)
- Saga scheduler timeouts (needs delayed-message-exchange plugin; revisit if stale sagas
  actually occur)
