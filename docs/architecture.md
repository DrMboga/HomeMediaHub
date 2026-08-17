# HomeMediaHub — Architecture (Phase 8 Target)

> **Status:** This document describes the architecture of the Phase 8 migration
> (n8n → RabbitMQ + MassTransit). Implementation is **in progress** — Steps 1–8 are done: the
> `Contracts`/`Data` shared libraries (§3/§8), EF Core ownership of the media schema (§9, the
> `AddMediaSchema` migration + deleted init SQL), the `MediaWorker` service (§6/§8 — consumers,
> the enrichment saga, ported processing services, `IJobStore`, MassTransit 8.5 bus), the Api
> rework (§10 — `/api/tools/*` and `POST /api/media` removed, publish-only MassTransit bus, Admin
> endpoints on `processing_jobs`, `ScanScheduler`, OTel metrics, semantic search accuracy fix), the
> GeoService `GeoLookupConsumer` (§4/§5 pattern mirrored for geo), the AiSidecar pika worker
> (§5 — `worker.py`/`app.py`/`inference.py`, raw-JSON publish, always-ACK), all five services wired
> into `docker-compose.yml` + RabbitMQ/Prometheus/Grafana added (Step 7), and the frontend Admin
> page (Step 8, §10 — scan trigger, pipeline progress, failed-job retry). n8n is fully removed.
> The first stage rollout ran green on **2026-08-02** — a scan ingested the full TestPhoto library
> and the saga applied geo addresses and AI labels, so this document now describes running
> software, not just a target. Not yet done: the checklist's failure-path and observability items,
> and the prod/full-archive run (Phase 9). The step-by-step migration guide lives in
> [`delivery-plan.md`](delivery-plan.md).

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
│   │                     AppDbContext (Identity + media entities) — OWNS media schema migrations
│   ├── Services/         ScanScheduler, PhotoOfTheDayScheduler (§14), FailedJobsCollector
│   └── Features/         …, PhotoOfTheDay/ (pick handler, send handler, Telegram client)
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
  `albums`, `album_media`, `photo_series`, `series_media`, `video_transcodes`, `processing_jobs`,
  and `potd_sends` (added later by `AddPotdSends`, Phase 11 — §14).
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
| `POST /api/admin/potd/send` `{month?, day?, mediaFileId?, dryRun?}` | (§14) run the Photo-of-the-Day pick + send once, now. Ignores the schedule, `Potd:Enabled`, and the already-sent guard; does **not** write `potd_sends`. → `{ sent, tier, mediaFileId, filePath, caption, skippedReason }` |
| `GET /api/admin/potd/last?limit=` | (§14) most recent rows from `potd_sends` — what the Admin card shows |

The frontend `/admin` page consumes these: scan trigger, polling progress card
(5 s `refetchInterval`), failed-jobs table with retry, and the Photo of the Day card
(dry-run preview with the picked thumbnail, tier and caption; Send Now; last-send date).

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

Photo of the Day (§14): `POTD_ENABLED` (**`false` in `.env.stage`** — stage must never post to the
family channel), `POTD_BOT_TOKEN`, `POTD_CHAT_ID`, `POTD_TIMEZONE`. All four map onto `Potd__*`
in compose with `:-` defaults, so a variable that is *present but blank* still falls back —
the trap that made the `RABBITMQ_*` vars dead in Program.cs.

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

## 14. Photo of the Day — scheduled Telegram post

> **Status: built 2026-08-17 (Phase 11).** The design below is what shipped; the two places the
> implementation departs from it are flagged inline as **Built**. Setup and troubleshooting:
> [`telegram-potd-setup.md`](telegram-potd-setup.md). Step-by-step record and the prod
> measurements: [`delivery-plan.md`](delivery-plan.md) → Phase 11.

Once a day at **15:00 local time**, post one randomly-chosen `person`-tagged photo from the
current calendar day across all years ("On This Day") to a private Telegram channel.

The same feature ran under n8n as Workflow 3 and was deleted with it in Step 7. It is **not**
coming back as a workflow, a queue, or a consumer — it is a `BackgroundService` inside the Api,
built exactly like `ScanScheduler`.

### Why a BackgroundService in the Api, not a message

There is no pipeline work here and nothing to back-pressure: one timer, one row, one HTTPS POST,
once per day. Routing it through RabbitMQ would mean inventing a message, an exchange, a queue,
and a consumer to move a tick from the Api to a service that would then need the Api's media
queries anyway. `ScanScheduler` publishes a message only because the *scan itself* belongs to
MediaWorker; here the work belongs where the data is.

The Api is also the only service with the pieces already built: the media queries, the
`processing_jobs`/media schema ownership, and — critically — the on-demand HEIC→JPEG conversion
(§10). MediaWorker has no HTTP egress and none of the query surface.

**This is the Api's first outbound internet call.** The stack is LAN-only for *inbound*; the
container already has egress (GeoService reaches Nominatim), but nothing in `api` has used it
before. Worth stating explicitly since "LAN-only" appears throughout these docs.

### Shape

```
PhotoOfTheDayScheduler : BackgroundService        (src/Api/Services/)
  gate on Potd:Enabled (default false)            ← same pattern as Scan:Enabled
  loop:
    delay until next 15:00 in Potd:TimeZone
    ── per-run body wrapped in try/catch ──
    create a DI scope                             ← AppDbContext is scoped; the service is not
    already sent today?  (potd_sends)  → skip
    pick  → SendPhotoOfTheDayHandler (MediatR, in-process)
    record the send
```

Five things that differ from `ScanScheduler` and are easy to get wrong:

1. **Scope creation.** `ScanScheduler` injects `IPublishEndpoint`, which is a singleton, so it can
   hold it directly. This one needs `AppDbContext`, which is **scoped** — a `BackgroundService` is
   a singleton and cannot inject it. Inject `IServiceScopeFactory` and open a scope per run.
2. **Local time, not UTC.** `ScanScheduler.NextRunTime` hardcodes `TimeSpan.Zero` (02:00 UTC)
   because a monthly maintenance scan does not care. 15:00 must be 15:00 *for Mike*, and the
   containers run UTC. Resolve `Potd:TimeZone` via `TimeZoneInfo` and convert. Verify tzdata and
   ICU are present in the `aspnet:10.0-noble` image and that `InvariantGlobalization` is not set —
   `FindSystemTimeZoneById` throws without them.
3. **Unhandled exceptions kill the host.** Since .NET 6 the default
   `BackgroundServiceExceptionBehavior` is `StopHost` — an exception escaping `ExecuteAsync` takes
   the whole Api down. A Telegram outage must not do that. Wrap the per-run body in try/catch, log
   at Error, continue the loop. (`ScanScheduler` has the same exposure today; a publish failure
   would stop the Api. Worth fixing there at the same time.)
4. **Restart idempotency.** A sleep-until-next-run loop re-sends if the Api restarts across the
   boundary — deploys tend to happen during the day. Guard on a persisted last-sent date.
5. **`NextRunTime` stays `internal static`.** That is why `ScanScheduler`'s is testable without a
   host; keep the same shape so the 15:00/timezone/rollover arithmetic can be unit-tested.

### The pick — three tiers, best available wins

**The intent is that something goes out every day.** Mike's call (2026-08-14): a person photo is
the ideal, but an empty channel is worse than a lesser photo. So the candidate set is built by
falling back through three tiers, and the **random choice happens inside whichever tier matched** —
not across the union.

Every tier shares the same base filter, which is the On This Day set minus what can't be posted:

```
BASE = media_files
  where not IsDeleted
    and OriginClass != 'meme'
    and MimeType not like 'video/%'          ← sendPhoto takes a still
    and TakenAt.Month = today.Month
    and TakenAt.Day   = today.Day
```

| | Tier | Candidate set |
|---|---|---|
| 1 | **person** | `BASE` and `exists (Tags where Tag = 'person' and Confidence >= Potd:MinConfidence)` |
| 2 | **geotagged** | `BASE` and `Metadata.Latitude != null and Metadata.Longitude != null` |
| 3 | **anything** | `BASE` |

First non-empty tier wins; pick uniformly at random from it. If tier 3 is also empty the calendar
day genuinely holds no postable photo — **send nothing and log it.** That is the only silent case.

Rationale for tier 2: a geotag means the photo was taken somewhere, which correlates with trips and
events rather than screenshots and receipts, and it is the one signal already on the record that
costs nothing to filter on. It also makes the caption worth reading, since the caption is built
from `Address` (below).

**The tag is `person`, singular.** The old Raspberry Pi implementation matched `TagLabel ==
"People"` because ML.NET produced that vocabulary; HomeMediaHub's tags are YOLOv8n COCO classes.
Measured against prod 2026-08-14: `person` = 16,876 rows and **no `people` tag exists at all**.

**`Potd:MinConfidence` starts at 0.5** and applies to tier 1 only. A daily single-photo pick is a
precision problem, not a recall one — the opposite of Phase 10's tag browsing, which deliberately
rejected a confidence floor because it thinned an already-starved dataset. Here, dropping a
marginal `person` hit costs nothing: tier 2 catches the day anyway.

**Measured on prod 2026-08-17, post-Phase-12 (the re-judgement this section asked for): 0.5 stands,
and the fallback never fires.** All **366** calendar days clear tier 1 — minimum **31** candidates,
mean **281** — so tiers 2 and 3 are insurance against a smaller library, not everyday behaviour.
The floor is also cheap: of 114,641 `person` tags, 106,549 are ≥ 0.5 and the distribution peaks
hard at 0.85–0.90 (34,724), so 0.5 discards a thin 7% tail rather than cutting into the mass. A
higher floor would be defensible on this data; it would not change what gets posted.

Select **ids only**, then pick in memory. A single calendar day's slice is small (hundreds of rows
even at full archive scale), and it sidesteps translating `random()` — this codebase has been
bitten by EF translation before (see the Phase 10 notes on `TagHandlers.cs`). Use
`Random.Shared.Next(0, count)`; note the old implementation's `rand.Next(0, len - 1)` was an
off-by-one that could never pick the last element.

Query the tiers lazily — tier 2 only runs if tier 1 came back empty. Three round trips in the worst
case, once a day, against a day-sized slice.

Log which tier supplied the photo. It is the cheapest possible read on whether the AI tagging is
actually earning its place: a channel running on tier 3 most days means `person` coverage is worse
than assumed.

`OnThisDayHandler` (`src/Api/Features/Media/OnThisDay/`) is **not** reusable for any tier: no tag
filter, no video exclusion, and it orders by date, so it is not a uniform sample. Write a sibling
handler — do not widen it, `/on-this-day` depends on its current shape. (Its `Take(200)` is gone as
of Phase 13, which does not change the conclusion.)

**Built:** the base filter pins the month/day boundary to UTC with
`TimeZoneInfo.ConvertTimeBySystemTimeZoneId(…, "UTC")`, exactly as `OnThisDayHandler` does — so the
pick is drawn from the same set the `/on-this-day` page shows, and neither depends on the session
`TimeZone`. Note this is *not* `Potd:TimeZone`: that decides which calendar day it is **now**, the
UTC pin decides which photos belong to that day.

### The send

Reuse `DisplayMediaRequest` (§10) in-process via MediatR — it returns a **file path plus content
type**, handling HEIC→JPEG conversion and caching. No HTTP call to itself, and none of the old
implementation's hand-rolled convert/thumbnail code. Prefer this over the stored WebP thumbnail
(`media_thumbnails.thumbnail_path`): higher quality, and Telegram treats WebP inconsistently.

`AddHttpClient("TelegramBotApi")` with base address `https://api.telegram.org/`, then
`POST bot{token}/sendPhoto` as `multipart/form-data` (`chat_id`, `caption`, `photo`).

Caption = `TakenAt` + `MediaMetadata.Address` when present. Simpler than the Pi version, which
assembled locality/region/country separately — `Address` here is one formatted string.

**Built:** `dd.MM.yyyy HH:mm · Address`, e.g. `17.08.2019 14:32 · Berlin, Deutschland`. The
timestamp is formatted **as stored, with no timezone conversion** — `taken_at` holds the camera's
naive wall-clock time (`MetadataService` tags EXIF `DateTimeOriginal` as UTC without shifting it),
so a photo taken at 17:00 in Crete must read 17:00. Converting to `Potd:TimeZone` here would
reintroduce the +2h error found on stage; it is the same reason
`Frontend/src/utils/mediaDate.ts` reads everything through UTC getters.

**Telegram's `sendPhoto` caps uploads at 10 MB** (and 10,000px combined width+height). A q90 JPEG
off a 12 MP iPhone HEIC lands well inside both, but full-size originals from other sources can
exceed 10 MB. Check length before posting and downscale rather than letting the API reject it.

### Persistence

One new table, owned by the Api's `AppDbContext` like every other media table (§9):

| `potd_sends` | |
|---|---|
| `sent_on` `date` **PK** | the local calendar date — the idempotency guard |
| `media_file_id` `uuid` | which photo went out |
| `sent_at` `timestamptz` | when |

The primary key *is* the guard: insert before/with the send and let a 23505 mean "already sent
today", the same duplicate-as-control-flow idiom the ingest consumers use. It also gives a send
history, which makes "don't repeat a photo across years" cheap to add later.

**Built:** the row is inserted *before* the send as a claim (with `media_file_id` = `Guid.Empty`),
then filled in with the chosen photo once Telegram accepts it. If the send fails or throws, the
claim is **released** — otherwise a Telegram outage would leave a row asserting a send that never
happened, and would burn the day even across a restart. The guard exists to prevent a double
*successful* send, not to spend the day on the first failure.

### Config

`Potd:Enabled` (default **false**), `Potd:BotToken`, `Potd:ChatId`, `Potd:SendAtLocal`
(default `15:00`), `Potd:TimeZone`, `Potd:MinConfidence` (default **0.5**).

Off by default for the same reason `Scan:Enabled` is: stage and prod share ports and run from one
compose file, and stage must never post to the family channel.

### Manual trigger

The scheduler must never be the only way to fire this — waiting until 15:00 to find out whether a
token is wrong is not a debug loop. The old implementation had the same escape hatch
(`WorkerController.SendPhotoOfTheDay`).

**`POST /api/admin/potd/send` (JWT, §10)** — runs the pick-and-send once, immediately.

```
POST /api/admin/potd/send
  { "month": 8, "day": 14,          ← optional, defaults to today in Potd:TimeZone
    "mediaFileId": "…",             ← optional, skip the pick and send this exact photo
    "dryRun": false }               ← optional, pick and report but do not post

→ 200 { sent, tier, mediaFileId, filePath, caption, skippedReason? }
```

Four behaviours that make it useful rather than just present:

1. **It ignores `Potd:Enabled`.** That flag exists to stop the *scheduler* posting from stage; an
   explicit button press is not an accident. Bot token and chat ID must still be configured — if
   they are not, fail with a clear message rather than a null-reference.
2. **It ignores the already-sent-today guard**, so it can be run repeatedly while debugging.
3. **It does not write `potd_sends`** — a manual test at 11:00 must not cause the real 15:00 run to
   skip the day. Only the scheduler records sends. (`dryRun` additionally skips the Telegram call.)
4. **It returns the chosen tier and caption**, so a failure is diagnosable from the response
   instead of the logs — which tier fired, which photo, what the caption would read.

`month`/`day` matter more than they look: they make every branch testable on demand. Today's date
may well have no `person` photo, and picking a date that does is the only way to exercise tier 1
without waiting a year.

**A button on the `/admin` page** (§10, "Photo of the Day" card) alongside it — Send Now, plus the
last-send info read from `potd_sends`. The endpoint alone still means curl and a hand-copied JWT;
the Admin page already holds the JWT and already has the Scan card as a precedent for
button-triggers-endpoint.

### Settled 2026-08-14

Both of this section's open questions were answered by Mike and are folded into the design above:

- **Fallback:** there should be a photo of the day *anyway*. Person → geotagged → any non-video,
  first non-empty tier wins. Silence only when the day holds nothing postable.
- **Confidence floor:** yes, start at `0.5`, and pick at random from whatever the matching tier
  selects.

Nothing is blocking implementation.

## 15. Out of Scope

- Face / people recognition
- Remote access (LAN only) · web upload (use Samba + scan)
- RAW camera files (CR2/ARW/NEF)
- Per-user library separation (shared pool)
- Telegram **error / dead-letter alerts** (n8n's Workflow 4; no replacement planned.
  Photo-of-the-Day is back in scope — §14)
- Saga scheduler timeouts (needs delayed-message-exchange plugin; revisit if stale sagas
  actually occur)
