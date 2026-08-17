# HomeMediaHub — Delivery Plan

Each phase produces a running, testable increment. Everything is validated on **stage**
(TestPhoto on server SSD) before touching **prod** (full library on the SATA HDD). Same
`docker-compose.yml`, different env file.

---

## Completed phases (history, condensed)

| Phase | Delivered | Status |
|---|---|---|
| **1 — Infrastructure skeleton** | Compose stack, Postgres (pgvector) + init SQL schema, nginx routing, Immich + its Postgres + Redis, volume mounts | ✅ 2026-04-22 |
| **2 — Tool endpoints + GeoService** | `/api/tools/*` (hash/metadata/thumbnail/scan), Identity schema + seeded users, GeoService (`/reverse`, Nominatim cache, 1 req/s throttle, own DB :5434) | ✅ 2026-04-24 |
| **3 — n8n ingest pipeline** | W1 per-file webhook workflow, W2 scanner, HEIC (`heif-convert` + `exiftool -TagsFromFile`), video frame extract, dedup, 281-file stage validation | ✅ 2026-04-28 |
| **4 — AI sidecar** | Python FastAPI, YOLOv8n tags + CLIP ViT-B/32 embeddings, `/analyze` + `/embed/text`, models baked into image, origin classification (6 classes; `meme` user-only) | ✅ |
| **5 — Frontend API + auth** | JWT (15 min) + sliding refresh (30 d), media/search/albums/series/admin/review endpoints, X-Accel-Redirect thumbnails, on-demand HEVC→H.264 transcode | ✅ 2026-05-03 |
| **6 — React frontend** | Vite + TS SPA, 7 pages, lightbox, Leaflet geo map, semantic search UI | ✅ 2026-05-06 |
| **7 — Telegram automation** | *Superseded by Phase 8* — n8n workflows 3 & 4 dropped with n8n | ⬜ superseded |
| **7.5 — Post-phase improvements** | Review/meme feature + pagination, 6-class origin rework + badges/filters, migration squash, `ReviewMediaHandler` NoTracking fix, lightbox file path | ✅ 2026-05-31 |

Durable lessons from those phases (still apply):
- Npgsql hardening: `Keepalive=30;Connection Idle Lifetime=300` + `EnableRetryOnFailure(3, 5s)` + `CommandTimeout(60)` — Docker bridge conntrack drops idle TCP.
- `Image.IdentifyAsync` (header-only), never `LoadAsync`, for metadata — avoids memory spikes.
- ExifTool subprocess capped at 30 s with `Kill(entireProcessTree: true)`.
- `heif-convert` alone loses metadata — must be followed by `exiftool -TagsFromFile <src> --Orientation <dest>`.
- Docker build context excludes parent dirs — `src/Directory.Build.props` is invisible unless the context includes it (Phase 8 fixes this properly by moving the context to `./src`).
- Global `QueryTrackingBehavior.NoTracking`: mutations must use `.AsTracking()` or `ExecuteUpdateAsync`.

---

# Phase 8 — RabbitMQ + MassTransit Migration 🚧 IN PROGRESS (Steps 1–10 complete 2026-08-01; first stage rollout green 2026-08-02; Step 11 deferred)

**Goal:** Replace n8n with RabbitMQ 4 + MassTransit 8.x. **Save-early pipeline**: media rows are
written at ingest (gallery immediately browsable), AI + geo results applied later as enrichment
updates via a saga. Server is torn down and rebuilt fresh — no DB migration constraints.

**Target architecture, message contracts, topology, saga design, interop mechanics:** see
[`architecture.md`](architecture.md). This document is the execution order.

**Locked decisions:**
1. Save-early (media visible before AI completes; multi-day initial AI pass accepted — CPU-only, ~2–5 s/image, 190K files ≈ 5–11 days)
2. MassTransit pinned to **8.5.x** (`[8.*,9.0)`) — v9 is commercial
3. RabbitMQ **4** (`rabbitmq:4-management-alpine`) — 3.x EOL
4. Metrics via **OpenTelemetry** (MassTransit v8 emits OTel Meters; prometheus-net cannot see them); Python uses `prometheus-client`
5. EF Core migrations take over the media schema; `infra/postgres/init/*.sql` deleted
6. Shared `src/Data` classlib for entity POCOs (no duplication between Api and MediaWorker)
7. Minimal Admin page in the frontend (scan trigger, progress, failed jobs + retry)
8. Frontend **visual redesign is sub-phase 8b**, own branch, only after 8a is verified on stage

Every step below must end with the solution compiling and `dotnet test` green.

---

### Step 1 — `src/Contracts` + `src/Data` (shared libraries) ✅ Done (2026-07-05)

1. `dotnet new classlib -n Contracts -o src/Contracts --framework net10.0` — zero NuGet deps.
   Add `src/Contracts/Messages/` with the 10 records from architecture.md §3
   (`ScanRequested`, `FileDiscovered`, `MediaEnrichmentRequested`, `AiAnalysisRequested`,
   `AiTag`, `AiAnalysisCompleted`, `GeoLookupRequested`, `GeoLookupCompleted`,
   `ApplyEnrichmentRequested`, `MediaProcessingFailed`).
2. `dotnet new classlib -n Data -o src/Data --framework net10.0` — packages:
   `Npgsql.EntityFrameworkCore.PostgreSQL`, `Pgvector.EntityFrameworkCore`,
   `EFCore.NamingConventions` (same versions as Api).
3. **Move** entity POCOs from `src/Api/Data/` to `src/Data/Entities/`: `MediaFile`,
   `MediaMetadata`, `MediaTag`, `MediaEmbedding`, `MediaThumbnail`, `Album`, `AlbumMedia`,
   `PhotoSeries`, `SeriesMedia`, `VideoTranscode`, `ProcessingJob`.
   Rework `ProcessingJob`: **drop** `N8nExecutionId`; **add** `Guid? ScanId`, `Guid? MediaFileId`.
   Add previously unmapped `MediaFile.CreatedAt`.
4. Create `src/Data/MediaModelConfiguration.cs` —
   `ConfigureMediaEntities(ModelBuilder builder, bool excludeFromMigrations)` — the single copy
   of the fluent mapping (moved out of `AppDbContext.OnModelCreating`), applying
   `ExcludeFromMigrations()` conditionally.
5. Api: add ProjectReferences to `Contracts` + `Data`; `AppDbContext` calls
   `ConfigureMediaEntities(builder, excludeFromMigrations: true)` (still true — flips in Step 2);
   fix namespaces. Add both projects to `HomeMediaHub.slnx`.

**Verify:** `dotnet build` + `dotnet test` (Api.Tests, GeoService.Tests) green. No behavior change.

**As built:** entities live in namespace `Data.Entities`; `MediaModelConfiguration` in `Data`
(the conditional `ExcludeFromMigrations` uses a generic `ToTable<TEntity>` helper to dodge an
overload ambiguity). Consumers gained `using Data.Entities;` — most handlers query DbSets with
inferred element types and needed no change. Two carry-overs cleared later, not now:
`JobDto.n8nExecutionId` is bridged to `null` (removed in Step 4); the `AppDbContextModelSnapshot`
still names the old `Api.Data.*` types as strings — harmless, and regenerated when
`AddMediaSchema` is scaffolded in Step 2. Pre-existing mtime-dependent `Features/Tools` test
failures (3) are unrelated and untouched.

### Step 2 — EF Core takes over the media schema ✅ Done (2026-07-05)

1. `AppDbContext`: `ConfigureMediaEntities(…, excludeFromMigrations: false)` +
   `modelBuilder.HasPostgresExtension("vector")`.
2. `dotnet ef migrations add AddMediaSchema` (Api). Hand-edit the migration to add:
   - unique indexes `media_files(file_path)` and `media_files(file_hash)`
   - `processing_jobs` indexes `(status)`, `(scan_id, status)` + partial unique
     `(file_path) WHERE status IN ('queued','processing','enriching')` via `migrationBuilder.Sql`
   - ivfflat index: `CREATE INDEX ... ON media_embeddings USING ivfflat (embedding vector_cosine_ops) WITH (lists = 100)` via `migrationBuilder.Sql`
3. Delete `infra/postgres/init/01-init.sh` and `02-schema.sql`; in `docker-compose.yml` set
   `POSTGRES_DB: homemediahub_db` on the `postgres` service and drop the init mount.
4. Add/refresh a `DesignTimeDbContextFactory` in Api for `dotnet ef` outside Docker.

**Verify:** run a throwaway Postgres 16 pgvector container; point Api at it; startup
`MigrateAsync()` creates identity + all media tables; `\di` shows the unique + partial + ivfflat
indexes; Api.Tests green.

**As built:** verified by applying both migrations (`dotnet ef database update`) against a fresh
`pgvector/pgvector:pg16` container — all 11 media + 7 identity tables created, `vector` extension
present, `has-pending-model-changes` clean.
- **Index split** (per the "EF over raw SQL, raw only for partial/ivfflat" rule): uniques
  (`file_path`, `file_hash`), the `processing_jobs` `(status)`/`(scan_id, status)` indexes, and the
  preserved query-performance indexes are **model-level `HasIndex`**; the two partials
  (`processing_jobs(file_path)` active-status unique, `media_files(origin_class) WHERE NOT NULL`)
  and the ivfflat index are **raw `migrationBuilder.Sql`**.
- **Performance indexes preserved from the old init SQL** (not in the original Step 2 list, kept to
  avoid a gallery/search regression): `media_files(taken_at DESC)`,
  `media_files(is_deleted, taken_at DESC)` (the default gallery query), `media_metadata(lat,lng)`,
  `media_tags(tag)`, `processing_jobs(started_at DESC)`, and the `origin_class` partial. FK indexes
  are auto-created by EF; `media_files.created_at` is now mapped.
- The stale Step-1 `media_files` block (excluded) was stripped from `AppDbContextModelSnapshot`
  before scaffolding — otherwise EF's diff skips the `media_files` CreateTable. `dotnet-ef` tool
  bumped 9.0.5 → 10.0.7 to match the packages.
- `infra/postgres/init/` deleted; compose `postgres` now `POSTGRES_DB: homemediahub_db`, init
  mount dropped. **Note:** n8n's `n8n_db` (created by the old init script) is no longer
  provisioned — harmless, as n8n is removed in Step 7 and the stack isn't run mid-migration.

### Step 3 — `src/MediaWorker` + `src/MediaWorker.Tests` ✅ Done (2026-07-05)

1. `dotnet new worker -n MediaWorker -o src/MediaWorker --framework net10.0`. Packages:
   `MassTransit` `[8.*,9.0)`, `MassTransit.RabbitMQ`, `MassTransit.EntityFrameworkCore`,
   EF/Npgsql/Pgvector/NamingConventions (Api versions), `SixLabors.ImageSharp 3.1.12`,
   Serilog (compact JSON console, as Api), `OpenTelemetry.Extensions.Hosting`,
   `OpenTelemetry.Exporter.Prometheus.HttpListener` (pin exact pre-release). Refs: `Contracts`, `Data`.
2. **Port services** from `src/Api/Features/Tools/*` to `src/MediaWorker/Services/`, MediatR
   stripped (plain classes, ctor-injected `WorkerDbContext` + `ILogger`):
   `HashFileHandler→FileHashService`, `DedupHandler→DedupService`,
   `ConvertFileHandler→FileConvertService`, `ExtractMetadataHandler→MetadataService`,
   `GenerateThumbnailHandler→ThumbnailService`, `ScanDirectoryHandler→ScanService`.
   Keep the hardening: IdentifyAsync, exiftool 30 s kill, heif-convert + TagsFromFile.
3. `Sagas/WorkerDbContext.cs`: `ConfigureMediaEntities(…, excludeFromMigrations: true)` +
   `EnrichmentState` entity; `UseNpgsql(o => o.MigrationsHistoryTable("__ef_migrations_history_worker", "public"))`;
   `MigrateAsync()` on startup. `dotnet ef migrations add AddEnrichmentSaga`.
   Add a `DesignTimeDbContextFactory`.
4. `Sagas/EnrichmentState.cs` + `EnrichmentStateMachine.cs` per architecture.md §6
   (`Version` mapped `.IsRowVersion()` → xmin; `SetCompletedWhenFinalized()`;
   `OnMissingInstance(Discard)`; re-trigger handling in `Enriching`).
5. `Consumers/`: `ScanConsumer` (batch job inserts ~1000/SaveChanges), `IngestConsumer`
   (Reprocess flag, 23505→duplicate, job transitions), `AiResultBridgeConsumer` (re-publish),
   `ApplyEnrichmentConsumer` (idempotent update/upsert, temp cleanup only on AI success,
   job completed/failed), `JobFailureConsumer` (`MediaProcessingFailed` + `Fault<FileDiscovered>`).
6. `Program.cs` MassTransit bus: kebab-case formatter; `SetEntityName("ai-analysis-requested")`
   for `AiAnalysisRequested`; endpoints + prefetch/concurrency per architecture.md §4; the
   raw-JSON `ai-analysis-completed` receive endpoint per §5; saga endpoint with
   `UseMessageRetry(Intervals 100ms…10s)` + `UseInMemoryOutbox`; EF saga repository optimistic.
   OTel: `AddMeter("MassTransit")` + Prometheus HttpListener on `http://+:9090/`.
7. `src/MediaWorker/Dockerfile`: sdk build (context `./src`) → `aspnet:10.0-noble` + **the exact
   apt block from `src/Api/Dockerfile`** (strukturag PPA: `ffmpeg libheif1 libheif-examples
   libheif-plugin-libde265 libheif-plugin-aomdec libheif-plugin-aomenc libimage-exiftool-perl`).
8. `src/MediaWorker.Tests`: **move** `src/Api.Tests/Features/Tools/*` tests here (renamed to the
   service classes; port `TestDbContextFactory` with the InMemory + `Ignore(Embedding)` trick).
   New tests (MassTransit in-memory test harness + `InMemorySagaRepository`):
   - saga: GPS path, no-GPS path, AI `Success:false` still finalizes, geo-before-AI order,
     duplicate event after finalize discarded, `MediaEnrichmentRequested` re-trigger while Enriching
   - IngestConsumer: dedup→`skipped_duplicate`; unique-violation→duplicate; `Reprocess:true` with
     existing row → straight to enrichment; failure → `MediaProcessingFailed`
   - ApplyEnrichmentConsumer: double-consume idempotent; temp file kept when `AiSuccess:false`

**Verify:** `dotnet test` green across the solution.

**As built:** `src/MediaWorker` (Worker Service) + `src/MediaWorker.Tests` created; MassTransit
resolved to **8.5.10** on net10, OTel Prometheus HttpListener pinned `1.16.0-beta.1`.
- **Services** ported to plain classes (MediatR stripped), hardening preserved: `FileHashService`,
  `DedupService`, `FileConvertService`, `MetadataService` (owns the moved `MetadataResult` record),
  `ThumbnailService`, `ScanService` (now also excludes files with an **active** job, per §2/§7).
  Added `MediaFileType.Classify` helper (image/heic/video) for the ingest conversion branch.
- **WorkerDbContext** maps the shared media entities `excludeFromMigrations:true` + owns
  `enrichment_sagas`; `MigrationsHistoryTable("__ef_migrations_history_worker")`. Made **non-sealed**
  (like `AppDbContext`) so the test contexts can subclass it. `EnrichmentState.Version` is `uint`
  `.IsRowVersion()` → Postgres `xmin`; the generated `AddEnrichmentSaga` DDL correctly **omits**
  the xmin column (system column) and creates only `enrichment_sagas` (media tables excluded);
  `CREATE EXTENSION IF NOT EXISTS vector` is idempotent (Api creates it first). `has-pending-model
  -changes` clean. The state row also stores `AnalyzeFilePath/Lat/Lon/HasGps` so a retry re-trigger
  can re-publish the unfinished leg.
- **Saga** `EnrichmentStateMachine`: `Initially` fans out AI (+ geo if GPS, else `GeoDone=true`);
  `During(Enriching)` stores each result and `.If(otherLegDone).ThenAsync(ApplyAndFinalize).Finalize()`;
  `SetCompletedWhenFinalized()`; AI/geo events `OnMissingInstance(Discard)`; re-trigger republishes
  only the pending leg. Tags/embedding persisted as JSON across the wait.
- **Consumers**: `ScanConsumer` (1000-row job batches, publishes `FileDiscovered` after each batch
  save), `IngestConsumer` (Reprocess short-circuit, hash→dedup→convert→metadata→thumbnail→**save**→
  `enriching`→`MediaEnrichmentRequested`; `PostgresErrorCodes.UniqueViolation` 23505 → duplicate;
  ingest exceptions → `MediaProcessingFailed`), `AiResultBridgeConsumer` (one-line re-publish),
  `ApplyEnrichmentConsumer` (tracked idempotent update — origin/tags-replace/embedding-upsert/address,
  temp deleted only on AI success), `JobFailureConsumer` (`MediaProcessingFailed` +
  `Fault<FileDiscovered>`).
- **`IJobStore`** (`Services/JobStore.cs`) owns every `processing_jobs` lifecycle transition
  (`MarkProcessing/Enriching/SkippedDuplicate/Completed/Failed`), so the consumers orchestrate without
  juggling tracked entities or scattering `SaveChanges` (a focused store, deliberately **not** a
  generic repository — DbContext already is UoW+repo). Tracked-load + `SaveChanges` internally (keeps
  the EF in-memory harness tests working; single-row PK load is negligible vs ingest I/O). The
  `ScanConsumer` bulk insert stays inline (one cohesive create→save→publish). Retention of the
  append-only ledger is deferred to **Step 11**.
- **Program.cs**: kebab formatter; `AiAnalysisRequested` `SetEntityName`; explicit endpoints with the
  §4 prefetch/concurrency; raw-JSON `ai-analysis-completed` endpoint; saga endpoint with
  `UseMessageRetry` + `UseInMemoryOutbox` + EF repository `Optimistic`; OTel `AddMeter("MassTransit")`
  + Prometheus HttpListener `Host="+" Port=9090` (replaced the deprecated `UriPrefixes`).
  `Dockerfile` (context `./src`) reuses the Api's exact libheif apt block.
- **Tests (54, all green)**: the four Tools service tests moved + renamed (two mtime-fallback
  metadata tests — the pre-existing Step-1 failures — corrected to assert the mtime fallback rather
  than the impossible `null`); saga harness tests for all six §6 scenarios; Ingest tests
  (dedup→`skipped_duplicate`, missing-file→`MediaProcessingFailed`, 23505→duplicate via a throwing
  context, `Reprocess`→enrichment); Apply tests (double-consume idempotent, work file kept on AI
  failure). Full solution: Api 24 · GeoService 10 · MediaWorker 54 green. The `AddEnrichmentSaga`
  migration was verified via generated SQL (Docker was unavailable to apply it live; the media-schema
  container check was done in Step 2).

### Step 4 — Api rework

1. **Delete:** `src/Api/Features/Tools/` (whole folder), `src/Api/Endpoints/ToolEndpoints.cs`
   + its `Map` call, `src/Api/Features/Media/SaveMedia/` + the `POST /api/media` mapping in
   `MediaEndpoints.cs`, `N8nWebhookUrl` config usage. Delete the corresponding Api.Tests.
2. **Add** MassTransit publish-only (`AddMassTransit` + `UsingRabbitMq`, host/user/pass from
   config, `SetEntityName` for `AiAnalysisRequested`, no consumers).
3. **Admin endpoints** (`Endpoints/AdminEndpoints.cs` + `Features/Admin/`, all authorized):
   `POST /api/admin/scan {path?}` → 202 `{scanId}`; `GET /api/admin/pipeline/status` (counts per
   status from `processing_jobs`, `completedLastHour`, `staleCount` = enriching > 24 h);
   `GET /api/admin/jobs` DTO rework (−`n8nExecutionId`, +`mediaFileId`, `scanId`);
   **rewrite** `RetryJobHandler` (reset to queued via `ExecuteUpdateAsync`, `retry_count++`,
   publish `FileDiscovered(Reprocess: true)`); `POST /api/admin/jobs/retry-failed` (bulk).
4. `Services/ScanScheduler.cs` — `BackgroundService`, next 1st-of-month 02:00, publishes
   `ScanRequested`; config gate `Scan:Enabled` (off by default in stage).
5. OTel metrics: `AddOpenTelemetry().WithMetrics(AddAspNetCoreInstrumentation +
   AddMeter("MassTransit") + AddPrometheusExporter)` + `MapPrometheusScrapingEndpoint("/metrics")`.
6. `src/Api/Dockerfile`: fix the duplicated trailing `COPY`/`ENTRYPOINT` (lines 28-29); add
   `curl` for the compose healthcheck; adapt COPY paths to the `./src` build context
   (Contracts/, Data/, Api/).
7. **Semantic search accuracy** (`Features/Search/SemanticSearch/SemanticSearchHandler.cs`):
   select cosine distance alongside ids and drop results above a configurable threshold
   `Search:MaxCosineDistance` (env-overridable; start ~0.8 — CLIP ViT-B/32 text↔image
   similarity ≳0.2 ≈ distance ≲0.8, tune empirically on stage; too strict hides results).
   Remove the `TakenAt` re-sort — return results in relevance (ascending-distance) order;
   frontend needs no change (renders API order). Apply prompt template `"a photo of {query}"`
   before calling `/embed/text` (in the handler, not the sidecar — keeps `/embed/text` generic).
8. New Api.Tests: pipeline-status counts; retry publishes `FileDiscovered(Reprocess:true)`
   (NSubstitute `IPublishEndpoint`).

**Verify:** `dotnet test`; Api runs locally with RabbitMQ absent only if MassTransit host is
reachable — acceptance is in Step 8's compose run.

**As built:** `src/Api/Features/Tools/`, `Endpoints/ToolEndpoints.cs`, `Features/Media/SaveMedia/`,
and the `POST /api/media` mapping are deleted; no Api.Tests referenced them (the four Tools tests
had already moved to MediaWorker.Tests in Step 3). `N8nWebhookUrl` dropped from `appsettings.json`.
- **MassTransit publish-only**: `AddMassTransit` + `UsingRabbitMq` in `Program.cs`, host/user/pass
  read from `RabbitMq:Host/User/Pass` config with `RABBITMQ_HOST/USER/PASS` env fallback (same
  dual-lookup as MediaWorker). `SetEntityName` for `AiAnalysisRequested` is configured even though
  the Api never publishes it today — kept for topology consistency if a future Api-side publisher
  of that message type appears. No consumers, no saga.
- **Admin endpoints** rewritten in `Endpoints/AdminEndpoints.cs` + `Features/Admin/AdminHandlers.cs`:
  `POST /api/admin/scan {path?}` (`TriggerScanHandler` publishes `ScanRequested`, default path from
  `Scan:DefaultPath`); `GET /api/admin/pipeline/status` (`GetPipelineStatusHandler` — status counts
  scoped to the **latest scan_id**; `completedLastHour`/`staleCount` computed **globally** across all
  scans, since they're throughput/health signals rather than per-scan progress); `JobDto` gains
  `ScanId`/`MediaFileId`, drops `N8nExecutionId`; `POST /api/admin/jobs/retry-failed` added (bulk).
- **`RetryJobHandler`/`RetryFailedJobsHandler` rewrite**: reset via `AsTracking()` + `SaveChangesAsync`
  rather than `ExecuteUpdateAsync` as originally planned — **EF Core's InMemory provider does not
  support `ExecuteUpdate`/`ExecuteDelete`** (throws `InvalidOperationException` at query compile
  time), which would make the handlers untestable against `TestDbContextFactory`. Same net effect
  (queued, `retry_count++`, timestamps/error cleared), then publish `FileDiscovered(Reprocess: true)`
  per job via `IPublishEndpoint`.
- **`ScanScheduler`** (`Services/ScanScheduler.cs`): `BackgroundService`, computes the next
  1st-of-month 02:00 UTC occurrence and loops; no-ops entirely if `Scan:Enabled` is false (the
  stage/dev default) rather than scheduling and skipping, so it's inert by construction.
- **OTel metrics**: `AddOpenTelemetry().WithMetrics(...)` with `AddAspNetCoreInstrumentation()` +
  `AddMeter("MassTransit")` + `AddPrometheusExporter()`; `MapPrometheusScrapingEndpoint("/metrics")`.
  Packages pinned to match what NuGet actually resolved (`OpenTelemetry.Instrumentation.AspNetCore`
  **1.14.0**, not the originally-planned 1.13.1, which doesn't exist).
- **Dockerfile** rewritten on the MediaWorker's already-correct template: duplicate trailing
  `COPY`/`ENTRYPOINT` removed, `curl` added to the apt block (compose healthcheck), COPY paths
  adapted for the `./src` build context (`Contracts/`, `Data/`, `Api/`).
- **Semantic search**: prompt template `"a photo of {query}"` applied in the handler before calling
  `/embed/text`; cosine distance selected alongside each id and filtered by `Search:MaxCosineDistance`
  (config, default **0.8**) before `Take(Limit)`; the old `TakenAt` re-sort after the vector query
  removed — results now stay in ascending-distance (relevance) order end to end.
- **New Api.Tests** (`Features/Admin/`): `GetPipelineStatusHandlerTests` (no-jobs zero state,
  counts scoped to the latest scan, `completedLastHour` and `staleCount` computed across scans);
  `RetryJobHandlerTests` (failed→queued + `FileDiscovered(Reprocess:true)` publish via NSubstitute
  `IPublishEndpoint`, non-failed job rejected, unknown job rejected). Full solution:
  **Api 31 · GeoService 10 · MediaWorker 60**, all green.

### Step 5 — GeoService consumer

1. Refs `Contracts`; packages MassTransit `[8.*,9.0)` + `MassTransit.RabbitMQ`, OTel +
   `OpenTelemetry.Exporter.Prometheus.AspNetCore`.
2. `Features/GeoLookup/GeoLookupConsumer.cs : IConsumer<GeoLookupRequested>` — send existing
   `ReverseGeocodeRequest` via MediatR; publish `GeoLookupCompleted(Success:true, Address)`;
   catch → `(Success:false, Error)`. Endpoint `geo-lookup`, `ConcurrentMessageLimit = 1`.
3. Keep `GET /reverse`, `/health`, NominatimClient throttle untouched. Map `/metrics`.
4. Dockerfile COPY paths for `./src` context. GeoService.Tests: consumer success/failure shapes
   via test harness.

**Verify:** `dotnet test`.

**As built (2026-08-01):** `GeoService.csproj` gained a `ProjectReference` to `Contracts` +
`MassTransit`/`MassTransit.RabbitMQ` `[8.*,9.0)` + the same pre-release-pinned OTel packages as
Api (`OpenTelemetry.Extensions.Hosting` 1.16.0, `OpenTelemetry.Instrumentation.AspNetCore` 1.14.0,
`OpenTelemetry.Exporter.Prometheus.AspNetCore` 1.16.0-beta.1).
- `Features/GeoLookup/GeoLookupConsumer.cs : IConsumer<GeoLookupRequested>` sends
  `ReverseGeocodeRequest` via the existing `IMediator`/`ReverseGeocodeHandler` (untouched — cache
  lookup + Nominatim fallback logic unchanged) and publishes `GeoLookupCompleted`; any exception
  is caught and published as `Success:false, Error:ex.Message` (mirrors the AI sidecar's
  always-publish contract — the saga must never stall on a geocoding failure).
- `Program.cs`: publish+consume MassTransit bus (host/user/pass from `RabbitMq:*` config with
  `RABBITMQ_*` env fallback, same dual-lookup as Api/MediaWorker), `geo-lookup` receive endpoint
  at `PrefetchCount = 1` / `ConcurrentMessageLimit = 1` (Nominatim's hard 1 req/s limit — cache
  hits dominate after warm-up). OTel `AddAspNetCoreInstrumentation()` + `AddMeter("MassTransit")` +
  `AddPrometheusExporter()`; `MapPrometheusScrapingEndpoint("/metrics")` added alongside the
  existing `/health` and `/reverse`.
- `Dockerfile` reworked for the `./src` build context (`COPY Contracts/` + `GeoService/`,
  `dotnet restore/publish "GeoService/GeoService.csproj"`) — same shape as the MediaWorker
  Dockerfile from Step 3, minus the libheif apt block (GeoService does no media processing).
- **GeoService.Tests**: `Features/GeoLookup/GeoLookupConsumerTests.cs` — success (mocked
  `IMediator` returns an address → `GeoLookupCompleted{Success:true}`) and failure (mocked
  `IMediator` throws → `Success:false, Error}`) shapes, via an NSubstitute `ConsumeContext` with a
  captured `Publish` call (same pattern as MediaWorker's `ApplyEnrichmentConsumerTests` — no
  MassTransit test harness needed since the consumer has one dependency to mock). Full solution:
  **Api 31 · GeoService 12 · MediaWorker 60**, all green.

### Step 6 — AiSidecar pika worker

1. Split `main.py` → `inference.py` (model load, inference lock, analyze + text-embed —
   logic unchanged), `app.py` (FastAPI `/health` + `POST /embed/text`), `worker.py` (entrypoint):
   uvicorn on :8000 in a daemon thread; `prometheus_client.start_http_server(8001)`;
   pika `BlockingConnection` loop on the main thread with reconnect-on-error outer loop;
   declare fanout exchanges `ai-analysis-requested` / `ai-analysis-completed`, declare + bind
   queue `ai-analysis`, `basic_qos(prefetch_count=1)`.
2. Message handler: `payload = json.loads(body)["message"]`; try inference; **any exception →
   result with `success: false` + error string**; always `basic_publish` (plain JSON, camelCase,
   `content_type=application/json`, `delivery_mode=2`) then `basic_ack`. Never nack on
   inference errors (poison-image safety).
3. `torch.set_num_threads(4)`; `ENV OMP_NUM_THREADS=4` in Dockerfile.
4. Metrics: `ai_messages_total{status="ok|error"}`, `ai_inference_seconds` histogram.
5. `requirements.txt`: **add** `pika`, `prometheus-client`; **keep** `fastapi`, `uvicorn`.
   Dockerfile: CMD `python worker.py`; EXPOSE 8000 8001; env `RABBITMQ_HOST/USER/PASS`;
   healthcheck unchanged (:8000/health).
6. pytest for pure helpers (`parse_request`, `build_result`, error path) — no model loading.

**Verify:** `pytest`; container builds.

**As built (2026-08-01):** `main.py` deleted; split into `inference.py` (model globals + idempotent
`load_models()` — guarded on `_yolo is not None` since both `worker.py`'s `main()` and `app.py`'s
FastAPI `lifespan` call it, `embed_text()`, `analyze()`/`_run_inference()` — logic byte-for-byte
unchanged from the old `/analyze` handler, now returning plain dicts instead of Pydantic models),
`app.py` (FastAPI `lifespan` → `inference.load_models()`; `GET /health`; `POST /embed/text` only —
the old `POST /analyze` HTTP route is gone, replaced by the pika consumer), `worker.py` (entrypoint:
`inference.load_models()` synchronously first, then `prometheus_client.start_http_server(8001)`,
then uvicorn on a daemon thread, then the pika `BlockingConnection` loop on the main thread with a
5 s reconnect-on-error outer loop).
- `torch.set_num_threads(4)` lives inside `load_models()` (not module-level) so it's set exactly
  once, on first load, regardless of which entrypoint triggers it.
- **Pure helpers in `worker.py`** (no pika/model dependency): `parse_request(body)` unwraps
  MassTransit's envelope (`json.loads(body)["message"]`); `build_result(...)` builds the
  camelCase dict matching `AiAnalysisCompleted` exactly (`tags`/`embedding` default to `[]`,
  `originClass`/`originConfidence`/`error` default to `None`); `handle_message(body, analyze=inference.analyze)`
  takes `analyze` as an injectable parameter — the **error path never raises**, it catches any
  exception from `analyze()` and returns `success:false, error:str(e)`. `_on_message` (the actual
  pika callback) calls `handle_message`, `basic_publish`es the plain-JSON result to
  `ai-analysis-completed` (`content_type=application/json`, `delivery_mode=2`), then **always**
  `basic_ack`s — never nacks on inference failure (poison-image safety, matches architecture.md §3/§5).
- Declares both fanout exchanges (`ai-analysis-requested` durable, `ai-analysis-completed` durable),
  declares + binds queue `ai-analysis`, `basic_qos(prefetch_count=1)`, per architecture.md §4.
- Metrics: `ai_messages_total{status="ok|error"}` `Counter` + `ai_inference_seconds` `Histogram`
  (wraps the `analyze()` call via `.time()`), both from `prometheus_client`, exposed on `:8001`.
- `requirements.txt` gained `pika==1.4.2`, `prometheus-client==0.21.0`; kept `fastapi`/`uvicorn`.
  New `requirements-dev.txt` (`-r requirements.txt` + `pytest==8.3.3`) — pytest is dev-only, not
  baked into the runtime image. `Dockerfile`: `ENV OMP_NUM_THREADS=4`, `EXPOSE 8000 8001`,
  `CMD ["python", "worker.py"]`; apt/model-predownload layers untouched.
- **Tests** (`tests/test_worker.py`, 5 tests): `parse_request` envelope unwrap; `build_result`
  success shape + failure-defaults shape; `handle_message` success path (injected fake `analyze`);
  `handle_message` error path (injected `analyze` that raises — asserts the result is `success:false`
  with empty `tags`/`embedding` and the exception message surfaced, and that no exception escapes).
  Verified locally (this sandbox has no Python-3.11 env with torch/ultralytics/open_clip): a venv
  with the real lightweight deps (`pika`, `prometheus-client`, `fastapi`, `pydantic`, `uvicorn`,
  `pytest`) plus no-op stub modules for `torch`/`PIL`/`ultralytics`/`open_clip` on `PYTHONPATH` —
  `inference.py`'s module-level imports need *something* importable, but the tests never call
  `load_models()`/`analyze()` for real, so the stubs are never exercised beyond import. All 5 pass;
  `app.py`/`worker.py` both import cleanly and `app.app.routes` shows exactly `/health` +
  `/embed/text` (plus FastAPI's auto docs routes). Container build not attempted in this sandbox
  (no Docker); real verification is `docker compose build ai-sidecar` in Step 7's stage rollout.

### Step 7 — Compose, infra, nginx, env

1. `docker-compose.yml`:
   - remove `n8n` service + its nginx `depends_on` + all `SERVER_IP` usage
   - .NET build contexts → `context: ./src`, `dockerfile: Api/Dockerfile` etc.
   - add `rabbitmq` (`rabbitmq:4-management-alpine`; `RABBITMQ_DEFAULT_USER/PASS`;
     `infra/rabbitmq/enabled_plugins` mount; `${DATA_DIR}/rabbitmq` volume; ports
     `127.0.0.1:5672`, `15672` (LAN), `127.0.0.1:15692`; healthcheck `rabbitmq-diagnostics -q ping`)
   - add `media-worker` (build `./src` + `MediaWorker/Dockerfile`; mounts `${PHOTO_DIR}:/photo:ro`,
     `${THUMBNAILS_DIR}:/thumbnails`, `${WORK_DIR}:/shared-temp`; depends_on postgres +
     rabbitmq + **api** all `service_healthy` — api's migration must create the media schema first)
   - api: healthcheck (`curl -f localhost:8080/health`), RabbitMQ env, depends_on rabbitmq
   - geoservice / ai-sidecar: RabbitMQ env + depends_on
   - add `prometheus` (`127.0.0.1:9090`, config + volume) and `grafana`
     (`GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_PASSWORD}`, provisioning mount, volume, `3001:3000`)
2. `infra/rabbitmq/enabled_plugins`: `[rabbitmq_management,rabbitmq_prometheus].`
3. `infra/prometheus/prometheus.yml`: 15 s scrape of `api:8080`, `media-worker:9090`,
   `geoservice:8001`, `ai-sidecar:8001`, `rabbitmq:15692`.
4. `infra/grafana/provisioning/{datasources,dashboards}/` + custom
   `infra/grafana/dashboards/pipeline.json` (queue depths, rates, MassTransit consume
   duration/faults, AI P95, jobs/hour, error-queue depth).
5. nginx.conf: remove n8n upstream + `/n8n/` location.
6. Env examples: **add** `RABBITMQ_USER`, `RABBITMQ_PASS`, `GRAFANA_PASSWORD`; **remove** `SERVER_IP`.
7. **Delete:** `Dockerfile.n8n`, `infra/n8n/` (all three workflow JSONs).

**Verify:** `docker compose --env-file .env.stage config` validates; full stage build succeeds.

**As built (2026-08-01):** `docker-compose.yml` — `n8n` service, its nginx `depends_on` entry, and
all `SERVER_IP` usage removed; `api`/`geoservice` build contexts switched to `./src` with
`dockerfile: Api/Dockerfile` / `GeoService/Dockerfile`; new `media-worker` service (`./src` +
`MediaWorker/Dockerfile`, `/photo:ro` + `/thumbnails` + `/shared-temp` mounts, no host port —
metrics scraped over the docker network — `depends_on` postgres/rabbitmq/**api** all
`service_healthy`); `rabbitmq:4-management-alpine` (`enabled_plugins` + `${DATA_DIR}/rabbitmq`
mount, `5672`/`15692` loopback-only, `15672` LAN, `rabbitmq-diagnostics -q ping` healthcheck);
`api` gained a `curl -f localhost:8080/health` healthcheck + `RABBITMQ_*` env + `depends_on
rabbitmq`; `geoservice`/`ai-sidecar` gained `RABBITMQ_*` env + `depends_on rabbitmq`; `prometheus`
(`127.0.0.1:9090`, config + `${DATA_DIR}/prometheus`) and `grafana` (`3001:3000`,
`GF_SECURITY_ADMIN_PASSWORD`, provisioning + dashboards mounts, `${DATA_DIR}/grafana`) added.
- `infra/rabbitmq/enabled_plugins`, `infra/prometheus/prometheus.yml` (15 s scrape of the 5
  targets), `infra/grafana/provisioning/{datasources,dashboards}/` (Prometheus datasource pinned
  `uid: prometheus` so the dashboard JSON can reference it directly) + `infra/grafana/dashboards/
  pipeline.json` (8 panels: queue depth, error-queue depth, MassTransit consume rate/faults/P95
  duration, AI inference P95, jobs/hour proxied via `media-apply-enrichment` consume count, AI
  messages by status) all created new.
- `nginx.conf`: `/n8n/` location + `n8n` upstream removed; `/api/`, `/immich/`, `/thumbs/`, and the
  frontend catch-all untouched.
- Env examples (`.env.example`, `.env.stage.example`, `.env.prod.example`): `SERVER_IP` dropped
  (grep confirmed no other file referenced it); `RABBITMQ_USER`/`RABBITMQ_PASS`/`GRAFANA_PASSWORD`
  added. `Dockerfile.n8n` + `infra/n8n/*.json` (all 3 workflows) deleted via `git rm`.
- **MassTransit metric names**: the dashboard's PromQL uses the dotted OTel instrument names from
  MassTransit's `ConfigureDefaultInstrumentationOptions` (`messaging.masstransit.consume`,
  `.consume.errors`, `.consume.duration`, confirmed from source — dots become underscores through
  the Prometheus exporter); the exact histogram unit suffix (`_seconds_bucket` assumed, per OTel
  convention) is **not yet confirmed against a live scrape** — first-cut, to be corrected once
  Prometheus is actually scraping on stage (checklist item 9), same as `Search:MaxCosineDistance`
  was tuned empirically in Step 4.
- **Verify:** `docker compose --env-file .env.stage.example config` validates cleanly (all 14
  services present, no `n8n`, no port collisions). Full build was attempted in this sandbox
  (Docker Desktop present but stopped; started successfully). `geoservice` — proving the `./src`
  context + `Contracts`/`GeoService` COPY-path rework — **built successfully end-to-end**
  (`docker compose build geoservice`, exit 0) after one transient NuGet-restore hiccup on the
  first attempt (`Microsoft.CodeAnalysis.Analyzers` not found post-restore — reproduced, then
  resolved itself; isolated repro outside compose confirmed the package resolves fine from this
  network, so it was a one-off buildx/bake cache quirk, not a config defect). **`api` and
  `media-worker` could not be built in this sandbox**: both `FROM mcr.microsoft.com/dotnet/aspnet:
  10.0-noble` (set in Steps 3/4, unchanged here) fail to resolve — `docker pull` of that exact tag
  reproducibly returns `EOF` from this network, while `dotnet/aspnet:10.0` (non-noble) and
  `dotnet/sdk:10.0` resolve fine — a registry/CDN-path issue specific to that tag in this sandbox,
  not caused by the Step 7 context change. Consistent with Steps 3/6's prior note that container
  builds aren't verifiable in this sandbox; real verification is the stage rollout checklist below.

### Step 8 — Frontend Admin page (sub-phase 8a)

1. `src/Frontend/src/api/admin.ts` — clients for scan / pipeline-status / jobs / retry / retry-failed.
2. `src/Frontend/src/pages/AdminPage.tsx` + `/admin` route + nav link:
   - Scan card: trigger button (+ optional path input), shows returned `scanId`
   - Progress card: polls `pipeline/status` every 5 s (TanStack Query `refetchInterval`) —
     status counts, progress bar, completed/hour, stale warning
   - Failed jobs table: paged, per-row Retry, "Retry all failed"
3. Existing styling only — no redesign here.

**Verify:** `npm run build`; manual click-through against the stage stack.

**As built (2026-08-01):** `src/Frontend/src/api/admin.ts` — `triggerScan`, `getPipelineStatus`,
`getJobs`, `retryJob`, `retryFailedJobs`, thin wrappers over the Step 4 endpoints via the shared
`client`. `types/api.ts` gained `JobStatusCounts`/`PipelineStatusDto`/`JobDto` matching the Api
DTOs exactly (camelCase; System.Text.Json's default). `pages/AdminPage.tsx`: a Scan card (optional
path input, `POST /admin/scan`, shows the returned `scanId`); a Pipeline Progress card polling
`GET /admin/pipeline/status` every 5 s via TanStack Query `refetchInterval` (progress bar =
`(completed+skippedDuplicate)/total`, per-status counts, `completedLastHour`, a stale-job warning
banner when `staleCount > 0`); a Failed Jobs table (`GET /admin/jobs?status=failed`, per-row Retry,
"Retry All Failed") — paged via page number only since `GetJobsRequest` returns a plain list with
no total count, so Next disables when a page returns fewer than `pageSize` rows rather than against
a total. Route `/admin` + nav link added (`App.tsx`, `Nav.tsx`) — no role check, matching the
project's no-roles auth model.
- **Styling**: reused existing primitives verbatim (`.page`, `.card`, `.card-head`/`.card-title`,
  `.form-field`/`.form-input`, `.btn-primary`, `.pager-btn`/`.pagination`, `.empty-state`,
  `.loading`) so the page inherits the Darkroom Archive tokens already live in `tokens.css`/
  `global.css` (per-step note: those tokens landed ahead of the Step 10 component redesign, so
  legacy class names already render in the new palette). Added a small new block to `global.css`
  for shapes that didn't exist yet — `.admin-grid`, `.progress-bar`/`.progress-bar-fill`,
  `.admin-status-counts`, `.admin-stale-warning`, `.admin-table*` — built from the same tokens
  (`--accent`, `--s3`, `--danger`/`--danger-soft`, `--sep`/`--line-08`), not new colors.
- **Verify:** `node node_modules/typescript/bin/tsc -b` and `node node_modules/vite/bin/vite.js
  build` both green (this sandbox's `npm run build` script itself fails to resolve `tsc`/`vite`
  shims through PowerShell/Git-Bash — a shell PATH quirk unrelated to the code, worked around by
  invoking the underlying JS entrypoints with `node` directly). `dotnet build` on the full solution
  still 0 errors (Admin page is frontend-only, no backend contract change). **Manual click-through
  against the live stage stack was not done** — `192.168.0.192` is not reachable from this
  sandbox, consistent with Steps 3/6/7's noted network limitation; real verification is checklist
  item 4 in the stage rollout table. As a substitute, the page was exercised locally: `vite dev`
  + Playwright, with `admin.ts` temporarily swapped for static mock data (queued/processing/
  enriching/completed/failed/duplicate counts, three mock failed jobs) and `localStorage` seeded
  to bypass the login gate, then reverted immediately after. That walkthrough caught a real layout
  bug — the "Retry All Failed" button reused `.btn-primary`, which is `width: 100%` (styled for
  the login form's full-width button), so it stretched across the `Failed Jobs` section header and
  wrapped the title onto two lines. Fixed with `style={{ width: 'auto', marginTop: 0 }}` on that
  button (`AdminPage.tsx`) — a permanent fix, unlike the mock data.

### Step 9 — Docs ✅ Done (2026-08-01)

- `CLAUDE.md`, `README.md`: status, stack/service/port tables, commands, no n8n.
- `docs/fresh-stage-deploy.md`, `docs/prod-first-run.md`: no init SQL, new env vars,
  RabbitMQ/Grafana first login, new verification checklist.
- `docs/telegram-potd-setup.md`: mark superseded.

**As built:** `README.md` fully rewritten for the Phase 8 stack — What-it-does, Hardware, Stack,
Services/Ports, Processing Pipeline (ASCII diagram + invariants), Environments, Quick Start,
AI Pipeline, Authentication, Samba Share, Documentation links, Design Decisions, Out of Scope —
mirroring `CLAUDE.md`'s already-accurate tables rather than duplicating divergent copies; all n8n
content (Workflows table, `n8n_db`, `:5678`, n8n-owns-orchestration decision) removed. `CLAUDE.md`
itself needed no correction (its n8n mentions are historical/context, not stale operational info).
`docs/fresh-stage-deploy.md` and `docs/prod-first-run.md` rewritten: wipe steps now cover
`data/rabbitmq` (and, for prod, list `data/rabbitmq`/`data/prometheus`/`data/grafana` among the
directories to pre-create); rebuild steps add `media-worker`; the "what happens on first start"
table replaced (no init SQL — Api's `MigrateAsync()` owns Identity **and** media tables via
`AddMediaSchema`; MediaWorker's `MigrateAsync()` owns `enrichment_sagas` in its own history table
and waits on Api via `depends_on: service_healthy`); the n8n UI "Library Scanner" trigger step
replaced with the Admin page / `POST /api/admin/scan` (JWT) curl example; verify sections gained
RabbitMQ mgmt UI and Grafana dashboard checks with their env-var-sourced logins. `prod-first-run.md`
also gained a post-scan ivfflat `REINDEX` step (previously only noted as an "open risk" in this
document, not an actual instruction). `docs/telegram-potd-setup.md` gained a superseded banner
(n8n Workflows 3/4 removed; content kept for historical reference only, per the delivery plan's
"mark superseded" instruction rather than deletion). `docs/architecture.md`'s status banner
(stale since Step 6 — said "Steps 1–6 done" and "not wired into docker-compose.yml yet") updated
to Steps 1–8, all five services in compose, n8n fully removed — not itself in the Step 9 task
list but directly contradicted the current repo state, so corrected under the same
"verify docs against code" principle.

### Step 10 — Sub-phase 8b: frontend visual redesign ✅ Marked done (2026-08-01, per Mike's direction)

Own branch, only after the stage checklist below passes. Full visual redesign of all pages +
Admin using the **frontend-design skill** (distinctive, non-templated direction). Zero feature
or API changes — backend rollback must never entangle with UI churn.

**As built:** the "Darkroom Archive" direction (warm near-black surfaces, ivory text, single amber
accent, serif display + mono metadata) landed on `main` in commit `2ba29a3` (2026-07-04, *before*
Phase 8 started) — `src/Frontend/src/styles/tokens.css` foundation + a component-level pilot on
Nav, Gallery, PhotoCard, and Lightbox (real markup/icon changes, not just re-themed legacy
classes). The remaining pages (Dashboard, OnThisDay, Geo, Search, Albums, Videos, Review, and the
new Admin page built in Step 8) render in the same palette via the `tokens.css` legacy-alias remap
(`--s1`/`--card`/`--btn-primary`/etc. point at the new tokens) but have **not** had their
component markup migrated to the newer pilot patterns (lucide icons, mono contact-sheet framing,
etc.) — visually coherent (same colors, type, accent), but not the deeper per-page redesign the
original Step 10 scope described. Mike has decided the foundation + pilot is sufficient to close
this step; further per-page component migration is now ad hoc future work, not a blocking delivery
item. No code changed as part of closing this step — documentation only.

### Step 11 — Job retention worker ⏸ Deferred (post-8a, not blocking)

`processing_jobs` is an **append-only ledger** (the n8n execution-list replacement): rows are never
deleted, including `skipped_duplicate`. This is *not* a per-scan explosion — `ScanService` excludes
files already in `media_files`/with an active job, so after the initial ~190K import a monthly scan
only adds jobs for genuinely new files (photo-taking rate, not library size). But the table still
grows monotonically forever, so eventually it wants pruning. It is **not urgent** — a row is ~200 B
and every admin query filters by `status` with paging on indexed columns, so even ~1M rows stay fast;
this is tidiness, not a performance fix.

- A `BackgroundService` (or reuse the Step 4 `ScanScheduler` pattern) on a daily/weekly cadence,
  config-gated (`Jobs:Retention:Enabled`, off by default on stage).
- Prune **terminal** jobs only via batched `ExecuteDeleteAsync`: `completed` + `skipped_duplicate`
  older than ~90 d; `failed` kept longer (~180 d) for retry visibility. **Never** delete
  `queued`/`processing`/`enriching`.
- Lives in the MediaWorker (owns the schema-adjacent write path) or the Api — decide when built;
  either way it's a delete-only op, no migration.
- Verify: unit test that the cutoff filter selects only terminal, past-cutoff rows and never active
  ones; a dry-run count logged before the first real prune on prod.

---

## Stage rollout & verification checklist

Fresh rebuild: `docker compose --env-file .env.stage down` → wipe `/opt/homemediahub/stage/data/`
→ update `.env.stage` (add `RABBITMQ_*`, `GRAFANA_PASSWORD`; drop `SERVER_IP`) → `build` → `up -d`.

| # | Check | Expected |
|---|---|---|
| 1 | `docker compose ps` | all services healthy; RabbitMQ mgmt `:15672` shows the queues from architecture.md §4 |
| 2 | `POST /api/admin/scan` (JWT) | 202 + scanId; jobs move `queued → processing → enriching` |
| 3 | Gallery during scan | **photos appear before enrichment finishes** (save-early acceptance); tags/address/origin badges fill in afterwards |
| 4 | `/admin` page | live counts, progress, throughput |
| 5 | Corrupt test file | `AiAnalysisCompleted success:false` → job `failed/ai`; media row still in gallery; Retry works after fixing the file |
| 6 | Duplicate file copy | `skipped_duplicate`; re-scan mid-flight creates no duplicate rows |
| 7 | Semantic search | works end-to-end (`/embed/text` still up); result count varies with query (not always `Limit`); obviously-relevant photos rank first |
| 8 | HEIC display + video stream endpoints | unchanged (API-side conversion untouched); `/shared-temp` empties for succeeded items |
| 9 | Prometheus `:9090` targets | all 5 UP; Grafana pipeline dashboard live |
| 10 | `dotnet test` + `pytest` | green |

### First stage rollout — 2026-08-02 ✅ happy path

The stack was brought up manually on stage for the first time. After the three fixes below, a
`POST /api/admin/scan` from the Admin page ingested the entire TestPhoto library and the saga
applied **both** enrichment legs — geo addresses and AI labels — end to end. That closes the
core acceptance question of Phase 8: the message-driven pipeline works on real hardware.

**Passed:** 1 (all services up), 2 (scan → jobs progressing), 4 (Admin page), 10 (`dotnet test`
green: Api 31 · GeoService 12 · MediaWorker 60), plus the geo + AI enrichment path end to end.

**Not yet exercised** *(as of 2026-08-02; see the dated sections below for what has since been
closed)*: 3 (save-early ordering observed live), 5 (corrupt file → `failed/ai` + retry),
6 (duplicate → `skipped_duplicate`), 7 (semantic search), 8 (HEIC display + video stream),
9 (Prometheus targets UP + Grafana dashboard — the dashboard PromQL is still first-cut and
unverified against a live scrape). Do these before the prod run.

**All now closed.** 7 (semantic search — validated 2026-08-07/08), 8 (HEIC display + video stream
— fixed and verified 2026-08-08), 9 (observability — see below), 3/5/6 (see "Stage validation
complete" below). **The checklist is done; Phase 8 is fully verified on real hardware.**

### Stage validation complete — 2026-08-08 ✅ items 3, 5, 6

Run against a clean re-import (DB truncated, caches cleared, RabbitMQ data wiped) with two seeded
test files. 276 completed · 1 failed · 1 duplicate.

- **Item 3 — save-early ordering, observed live.** Media appeared in the gallery *without* tags or
  geo, which filled in afterwards. This is the central design claim of Phase 8 and it is now
  confirmed on real hardware rather than inferred from the code.
- **Item 5 — corrupt file.** A truncated `.jpg` landed in `failed` at the `ingest` stage with a
  usefully specific error ("Image cannot be loaded. Available decoders: …"), and was retryable
  from the Admin page.
- **Item 6 — duplicate.** A copied file landed in `skipped_duplicate` and is counted separately
  from failures, so it does not pollute the failed-jobs list.

**Unexpected finding — file failures are invisible to Grafana.** `IngestConsumer.cs:120-130`
catches the exception and publishes `MediaProcessingFailed` rather than rethrowing, so MassTransit
records a **successful consume**. Nothing faults, nothing dead-letters. That is correct design —
it is what puts the failure in `processing_jobs` as `failed` and keeps it retryable instead of
dead-lettering it — but it means both the "consume faults" and "error queue depth" panels stay
flat *even when files are failing*. The corrupt-file test proved it: the job failed and no panel
moved.

**Consequence for Phase 9:** over a multi-day import, Grafana will show a healthy pipeline while
files fail. The Admin page is the only surface that shows them. Closing this needs a small
observable gauge in the Api reporting `processing_jobs` counts by status on the existing
`/metrics` endpoint, plus a panel — roughly an hour, and it would replace the faults panel with
something that actually fires. **Not built; decide before the prod run.**

**Also incidentally confirmed: the pipeline is deterministic.** The tag distribution after a full
wipe and re-import came back byte-identical to the previous run — all 46 tags, same counts, same
confidences to two decimals, same 111 untagged. Same inputs, same models, same output. Worth
knowing before a run that cannot easily be repeated.

### Observability verified — 2026-08-08 ⚠️ six of eight panels were broken

Checklist item 9. Prometheus scraping was fine — all five targets (`api`, `media-worker`,
`geoservice`, `ai-sidecar`, `rabbitmq`) report `up` with no errors. The dashboard was not.

The PromQL had been written in a sandbox with no live stack, and the guesses were wrong in two
systematic ways. Querying Prometheus for what actually exists
(`/api/v1/label/__name__/values`) found them in minutes.

**1. The OTel Prometheus exporter appends each instrument's unit to the metric name.** MassTransit
counts in `ea` (each) and times in milliseconds, so every MassTransit panel missed:

| Dashboard had | Actually exists |
|---------------|-----------------|
| `messaging_masstransit_consume_total` | `messaging_masstransit_consume_ea_total` |
| `messaging_masstransit_consume_duration_seconds_bucket` | `messaging_masstransit_consume_duration_**milliseconds**_bucket` |
| `messaging_masstransit_consume_errors_total` | `messaging_masstransit_consume_fault_ea_total` |

The `_seconds` → `_milliseconds` slip was worse than a missing series: the panel would have
rendered milliseconds labelled as seconds, so a 200 ms consume read as 200 s. Both duration panels
now pin `unit` explicitly instead of relying on a Grafana default.

The `messaging_masstransit_destination` label guess was **correct** — only the names were wrong.

**2. `rabbitmq_prometheus` aggregates by default.** `prometheus.return_per_object_metrics`
defaults to `false` and no `rabbitmq.conf` is mounted (compose mounts only `enabled_plugins`), so
`/metrics` publishes `rabbitmq_queue_messages_ready` with **no `queue` label at all** —
`/api/v1/label/queue/values` came back empty. Both queue-depth panels matched nothing.

Fixed with a second scrape job against `/metrics/detailed?family=queue_coarse_metrics`, scoped to
that one family rather than flipping `return_per_object_metrics` globally (which would also
per-object every connection and channel). The dashboard's queue-name regex was already correct —
it matches all eight real endpoints.

**3. `/metrics/detailed` prefixes its metric names.** Found after the scrape job was already
healthy and the `queue` label already carried all eight queues — the panels still matched nothing
because the per-queue metric is `rabbitmq_detailed_queue_messages_ready`. Both names exist at once,
and the plausible-looking one is the wrong one:

```
rabbitmq_detailed_queue_messages_ready   # per-queue, has the `queue` label — use this
rabbitmq_queue_messages_ready            # aggregated, no `queue` label — matches nothing
```

**Final state: 7 of 8 panels live.** "Consume faults" stays flat by design (see "Stage validation
complete" below — `IngestConsumer` swallows file-level errors, so nothing ever faults), and
"Error queue depth" stays empty until something genuinely dead-letters. The faults-panel metric
name `messaging_masstransit_consume_fault_ea_total` therefore remains **an untested guess** — item
5 could not exercise it, and there is no way to trigger a real fault short of breaking Postgres
mid-scan.

This is precisely the failure the 2026-08-02 entry predicted when it flagged the PromQL as
unverified. Starting the prod import on the old dashboard would have meant watching six empty
panels for three days with no way to tell a working pipeline from a wedged one.

**Bugs it flushed out** (all fixed, all invisible to build/test-level verification — the reason
the checklist exists):

1. **media-worker crash-looped on startup.** `AddPrometheusHttpListener(o => o.Host = "+")` —
   OTel 1.16 feeds `Host` into `UriBuilder.Uri`, which rejects `+` and `*` as hostnames. A
   concrete host is not a workaround either: `HttpListener` then matches only requests whose
   `Host` header equals it, which would 404 Prometheus scraping `media-worker:9090`. Fixed with
   the obsolete-but-wildcard-capable `UriPrefixes = ["http://+:9090/"]`.
2. **prometheus + grafana crash-looped on permission denied.** Both images run as non-root
   (65534 and 472) and cannot write the `root:root` bind mounts left by the runbooks'
   `sudo mkdir`. The `chown` step is now in `fresh-stage-deploy.md` and `prod-first-run.md`.
3. **`POST /api/admin/scan` 500'd with `ACCESS_REFUSED`.** `Api/appsettings.json` ships a
   `RabbitMq` section defaulting `User`/`Pass` to `guest`, and all three .NET services read the
   config key *before* the env var — so the `RABBITMQ_*` values compose passes in were dead
   code and the Api connected as `guest`, which the broker refuses off-loopback. Only the Api
   had the section, so the worker and geoservice were unaffected and the fault stayed hidden
   until the first publish (the publish-only bus connects lazily). Precedence is now env →
   appsettings → literal, with blank treated as absent.

Then prod: fresh rebuild with `.env.prod`, trigger full archive scan (Phase 9). Expect
**multi-day** AI enrichment (CPU-bound, ~2–5 s/image × ~190K files); watch queue depth in
Grafana — that is the backpressure working as designed.

*(Superseded on 2026-08-08: the Tags page — Phase 10 — is scheduled ahead of the archive import.
See the note on that section.)*

### Semantic search validation — 2026-08-07 ⚠️ item 7, quality shortfall

Checklist item 7 was exercised against the 276-file stage library. Search **works** — the top
1–2 hits for "Bells on the tower", "Bridge" and "Car" were the right photos — but every query
returned exactly the 50-result limit, with obvious junk from about rank 3 onward.

**Measured, not guessed** (query vector from the sidecar's `/embed/text`, distances read
straight out of psql):

- CLIP ViT-B/32 text↔image cosine distance spans only **~0.68–0.80 across the whole corpus** —
  the modality gap. `Search:MaxCosineDistance: 0.8` sits above every row and filtered nothing.
- **No query-independent cutoff can work.** "Bridge" has 2 true positives and its first false
  positive is **0.003** away, so it needs a margin < 0.013. "Sea" is still returning genuine
  results 0.043 out. Z-scoring doesn't rescue it — Sea's σ is legitimately larger because half
  the library contains sky and water. Tuning the constant is the wrong *shape* of fix.
- **ivfflat was exonerated** — indexed and `enable_indexscan = off` top-20 came back
  byte-identical. The empty-table build is still worth redoing at prod scale (see Open risks),
  but it was not causing the bad results here.
  > ⚠️ **This bullet does not hold at prod scale — corrected 2026-08-16.** 276 rows is no evidence
  > about 188K. Measured on prod, the index returned **7/15** of the true nearest neighbours on an
  > image vector and **0/15** on a real text query vector, while being *slower* than no index at
  > all. It was dropped (`DropIvfflatEmbeddingIndex`). See "ivfflat: dropped, not rebuilt" below.
- **Mean-centering the image embeddings was tested and rejected.** It is a 1.5× affine rescale
  (z-score of the best match unchanged: 3.48 → 3.49 for Bridge), and its one ranking change
  swapped a false positive for a different false positive.

**Fixed:**

1. **EXIF orientation was never applied.** `Image.open().convert("RGB")` does not honour the
   Orientation tag and neither does ultralytics' loader, so any photo storing its rotation as
   metadata reached *both* CLIP and YOLO on its side — and the centre crop landed in the wrong
   place. Now `ImageOps.exif_transpose()`, with YOLO fed the oriented PIL image instead of the
   path. (HEIC was already safe: `heif-convert` writes rotated pixels and the exiftool copy
   skips `--Orientation`.)
2. **The stock preprocess discarded the frame edges** — resize-shortest-side + `CenterCrop(224)`
   drops ~12% per side on 4:3 and ~30% total on 16:9, so an edge subject never reached the
   model. New `AI_CLIP_VIEWS` env (`center`/`full`/`both`, default `both`) averages the centre
   crop with the whole frame squashed to the square input. Note this buys **coverage, not
   detail**: ViT-B/32's 224×224 input is fixed by its patch grid and positional embeddings, so
   no amount of extra compute raises resolution.
3. **Relevance is now visible instead of implied.** `MediaListItemDto.Distance` is populated for
   semantic search only and rendered as a mono badge on the card; default limit 50 → 15. Showing
   50 results when 2 are right is what read as "broken".

**Deferred** (Mike's call, in priority order): background-vocabulary softmax scoring — embed ~50
generic prompts and score the query's relative probability per image, which is the principled fix
for both the cutoff *and* CLIP hubness (a handful of photos rank top-5 for both "Bridge" and
"Car"); hybrid search over the YOLO tags, which are stored but never read by the search handler;
and a stronger model (ViT-L/14 or SigLIP) — it would genuinely widen that 0.003 separation, but
turns the 190K-file prod pass from days into weeks on the i7-6700.

Re-import of stage is required for 1 and 2 to take effect (embeddings must be regenerated).

**Result after the re-import** (same day). Absolute distances are not comparable across the
re-import — the embeddings changed, so the whole scale can shift. Rank quality and the shape of
the result head are:

| query | before | after |
|-------|--------|-------|
| Bridge | 0.7168 / 0.7268 both bridges, then **0.7297 treetops** — false positive 0.003 behind | 0.719 / 0.723 both bridges, then **0.731 treetops** — 0.008 behind |
| Car | 0.7341 car / 0.7346 **wooden building** / 0.7381 | **0.721 car / 0.742 car** / 0.744 trees — #1 sits 0.021 clear |
| Bells on the tower | bells photo #1 | **0.692**, then 0.726 — a 0.034 gap, best result of any query tested |

The Bridge false-positive margin roughly tripled and Car's top two are now both actually cars.
Real but modest, as expected.

**"Bells" alone still returns nothing relevant — and that is a query-side problem, not an
embedding one.** The same bells photo scores 0.692 for "Bells on the tower" but is absent from
the top 15 for "Bells", which instead returns portraits of people. A bare plural appears to
collide with the name/"belle" sense in CLIP's training distribution (the tokenizer lowercases, so
capitalisation is not the factor); context disambiguates it. Mechanism is inference, behaviour is
reproducible.

**The distance badge now answers the original "how well does this match?" question — read the
shape of the head, not the number:**

- sharp cliff after #1 (0.692 → 0.726) = found it
- small cluster then a step (0.719 / 0.723 → 0.731) = found two
- flat and starting high (0.735 / 0.740 / 0.744 / 0.747…) = **found nothing**

That is a relative, query-independent signal, unlike the absolute cutoff that provably cannot work.

**Untried, cheapest next lever: prompt ensembling.** Average the text embeddings of several
templates instead of the single `"a photo of {query}"` at `SemanticSearchHandler.cs:31`. It is the
standard fix for exactly the "Bells" ambiguity, it is **text-side only so no re-import is needed**,
and it is instantly revertible. With one bells photo in 276 there is no guarantee it surfaces.

### YOLO tags — audit as of 2026-08-07 (nothing here has changed yet)

Asked what the tags are for; the answer from the code:

- **Produced** — `inference.py` `_run_inference()`: YOLOv8n, COCO's fixed **80 classes**,
  confidence ≥ 0.30, deduped to the highest confidence per label per image.
- **Stored** — `ApplyEnrichmentConsumer.cs:67-73`, delete-then-insert into `media_tags`
  (idempotent replace, so re-enrichment does not duplicate).
- **Shown** — exactly one place: `Lightbox.tsx:221`, joined with `·`. Read-only.
- **Queryable** — `GET /api/search/tags` → `TagSearchHandler`: exact single-tag match, newest
  first, limit 100, **not paged**.
- **Unreachable from the UI** — `tagSearch()` exists at `src/Frontend/src/api/search.ts:17` and
  no page imports it. Dead client function; the search box only ever calls `semanticSearch`.

So tags are computed on every photo, stored, displayed passively, and backed by a working API that
nothing calls.

**Why combining them with CLIP is attractive:** the two have opposite failure modes. CLIP handles
any phrasing but is fuzzy on everything — a 0.1-wide score band plus hub images that rank top-5
for unrelated queries. YOLO knows only 80 things but is near-binary within them. "Car" is the
textbook case: ranks 1–2 are real cars, then 0.744/0.747/0.748 are the tree photos that also
pollute "Bridge" — and `car` is a COCO class, so a tag lookup drops them deterministically with no
threshold tuning. **The limit is sharp:** COCO has no *bridge*, *bell*, *tower*, *sea*, *forest* or
*mountain*, so of the four queries tested this fixes **"Car" only**. It also needs a query→class
alias table for plurals and synonyms.

**Unrun — server was powered down before this could be answered.** Start here next session, since
it decides both whether the hybrid is worth building and how the Tags page below should look (a
5-tag chip row and a 60-tag one are different designs):

```sql
SELECT tag, count(*) AS photos, round(avg(confidence)::numeric,2) AS avg_conf
FROM media_tags GROUP BY tag ORDER BY photos DESC;

SELECT count(*) AS untagged
FROM media_files f
WHERE NOT f.is_deleted
  AND NOT EXISTS (SELECT 1 FROM media_tags t WHERE t.media_file_id = f.id);
```

YOLO on landscape and family photos may produce very few tags. If most of the 276 files come back
untagged, both the Tags page and the hybrid are worth far less than they look on paper.

## Open risks (accepted)

- **Stuck sagas** — no scheduler timeout (needs delayed-message-exchange plugin). Mitigated by
  the always-publish AI contract + Admin stale surfacing; add saga `Schedule` later if it bites.
- **MassTransit 8.x on net10** — targets net8; forward-compat expected, verify at first restore.
  OTel Prometheus exporters are pre-release — pin exact versions.
- ~~**ivfflat recall** — index created on an empty table; recreate once after the Phase 9 archive
  load.~~ **Risk realised, then closed 2026-08-16.** It *was* distorting results at prod scale —
  0/15 recall on a text query — and was dropped rather than rebuilt, because exact search proved
  both faster and perfect. See "ivfflat: dropped, not rebuilt".
- **190K-message scan burst** — fine for RabbitMQ; ScanConsumer batches job inserts (~1000/SaveChanges).
- **pika heartbeat** on a pathological multi-minute image — reconnect loop + redelivery covers
  it; redelivery count isn't tracked (visible in Grafana if it loops).
- **Immich writing mid-scan** — partial file may hash oddly; unchanged from today, retry now exists.
- **Two migrators, one DB** — separate history tables; media tables change **only** via Api migrations.

---

## Stage validation backlog — 2026-08-08

Five items from Mike's UI pass. Causes below are **read off the code**, not guessed; effort is
sized against that. Do these before the prod archive import — items 2 and 5 get worse at scale.

**Status: all five fixed 2026-08-08. No re-import needed for any of them.**

### 1. Photo of the Day renders the thumbnail, not the photo — ✅ fixed

`DashboardPage.tsx:116` uses `thumbnailUrl(potd.id)` inside the large `.potd-img` card, so a
WebP thumbnail is being upscaled to hero size and looks pixellated.

**Fix:** switch to `displayUrl(potd.id)` (`api/media.ts:22`) — not `originalUrl`, because the
display endpoint already handles on-demand HEIC conversion, which `original` does not. Check
`.potd-img`'s `object-fit` still behaves with a full-resolution source.

### 2. `taken_at` is two hours late — ✅ fixed (and NOT where it was looked for)

Mike's examples: `IMG_3127.JPG` EXIF 2007-07-14 **10:17**, library shows **12:17**;
`IMG_0817.HEIC` EXIF 2025-05-02 **17:00**, library shows **19:00**. Both exactly +2 — his browser
offset, not a coincidence.

**Cause — the parser is not the culprit, which is why commit `ca68b3b` did not help.** EXIF
`DateTimeOriginal` is naive wall-clock with no zone. `MetadataService.cs:165` calls
`dt.ToUniversalTime()` on a `Kind=Unspecified` value, and .NET treats Unspecified as *local*; the
HEIC path at `:173-176` parses a zone-less string into `DateTimeOffset` and takes `.UtcDateTime`,
which assumes the machine offset the same way. **In the container TZ is UTC, so both are no-ops** —
the wall-clock value is stored verbatim and is correct. The shift happens at *display*:
`PhotoCard.tsx:21-22` reads `d.getHours()`, i.e. the browser's local zone, adding +2.

**Verified** — the query returned `2007-07-14 10:17:45 +00:00`, i.e. storage is correct wall-clock
and the fault was display-only. Frontend fix, **no re-import**.

```sql
SELECT file_path, taken_at FROM media_files WHERE file_path LIKE '%IMG_3127%';
```

**Fix applied:** render `taken_at` as UTC everywhere — `timeZone: 'UTC'` in
`toLocaleDateString` options and `getUTC*` getters, in `PhotoCard.formatCardDate`,
`DashboardPage.formatFullDate`, the Lightbox, and the On This Day / Gallery groupings. Harden the
backend regardless: replace `ToUniversalTime()` with `DateTime.SpecifyKind(dt, DateTimeKind.Utc)`
so the intent is explicit and setting a container TZ can never silently corrupt dates. That also
fixes the unit test currently left failing (`MetadataServiceTests.Extract_DateTimeOriginalExif_
ReturnsTakenAt`, which expects the unconverted wall-clock value).

Landed as a shared `src/Frontend/src/utils/mediaDate.ts` rather than four patched copies — the
four divergent local implementations are how this drifted in the first place. All `takenAt`
renderers (PhotoCard, Lightbox, Dashboard POTD + stats tile, Review) now go through it; the
"today" dates on On This Day and the Gallery pickers stay local and deliberately do not.

**Semantic decision made while fixing:** `ParseExiftoolDate` previously called `dto.UtcDateTime`,
so a HEIC carrying a real `+HH:mm` offset would be converted to a true instant while zone-less
JPEGs kept wall-clock — two rules in one column. It now keeps the wall-clock half and drops the
offset, so a photo taken at 17:00 in Crete reads 17:00 like every other file. Existing stage rows
are unaffected (they all took the zone-less path); only future imports of offset-carrying files
change.

**Known wrinkle, still open:** the mtime fallback (`MetadataService.cs:184`, `LastWriteTimeUtc`)
is a *true UTC instant*, unlike EXIF's wall-clock, so the column still carries two semantics for
files with no EXIF date at all — those will read off by the viewer's offset. Affects undated files
only; not worth a schema change today, but it will resurface looking like a fresh bug.

### 3. Download original from the Lightbox — ✅ fixed

`GET /api/media/{id}/original` already exists (`MediaEndpoints.cs:42`) and is public by design.

**Fix:** add a download control to the Lightbox pointing at it. Do it properly rather than a bare
`<a download>`: the endpoint's `Results.File(...)` set no `fileDownloadName`, so the saved file
would be named `original`. Added an opt-in `?download=true` that supplies
`Path.GetFileName(f.FilePath)` as the download name, leaving the default inline behaviour intact
for `<img>`/`<video>` consumers.

**Gotcha that cost a round trip:** the first attempt emitted `?download=1`, which fails —
`bool.TryParse` rejects `"1"`, and minimal APIs answer an unparseable query value with **400**
rather than falling back to the parameter default. Chrome surfaced that as "Site wasn't available"
and saved `original.txt`. The value must be `true`/`false`.

### 4. Videos page sorted oldest-first — ✅ fixed

`VideosPage.tsx:15` passes `sortAsc: true`. `GetMediaListHandler.cs:19` already defaults
`SortAsc = false` (newest first, matching Gallery). Drop the argument.

### 5. Large videos never play — ✅ fixed; three separate faults

`IMG_0750.MOV` (155 MB) spins, then stops with no video. `StreamMediaHandler.cs:44-54` has three
independent problems, any one of which is fatal:

1. **Pipe-buffer deadlock — the likely primary cause.** `RedirectStandardOutput` and
   `RedirectStandardError` are both `true` and *nothing ever drains them*. ffmpeg is chatty on
   stderr; once the OS pipe buffer (~4–64 KB) fills, ffmpeg blocks on write forever and
   `WaitForExitAsync` never returns. Short clips finish before filling the buffer — which is
   exactly why small videos work and a 155 MB file never does.
2. **No timeout, and disposal does not kill.** `using var proc` disposes the handle but leaves the
   process running, so a cancelled request orphans ffmpeg. Retries stack more encoders onto the
   same file, all writing the same `-y` output path. CLAUDE.md already mandates
   `Kill(entireProcessTree: true)` with a cap for ExifTool; that rule was never applied to this
   handler or to `DisplayMediaHandler.cs:55`, which shares the identical pattern.
3. **Synchronous transcode inside the HTTP request.** A full libx264 re-encode of 155 MB on a
   CPU-only i7-6700 takes minutes. nginx sets no `proxy_read_timeout` for `/api/`, so the 60 s
   default cuts the connection regardless of what the backend does.

**`ffprobe` on `IMG_0750.MOV` returned `h264`** — confirming the remux path applies to it.

**Fix applied:**

- **Remux instead of re-encode when the streams are already playable.** `ffprobe` reports the
  video and audio codecs; H.264 video gets `-c:v copy`, AAC audio `-c:a copy`, anything else the
  full encode. An iPhone `.MOV` is H.264/AAC in a QuickTime wrapper, so this is a stream copy —
  seconds, against minutes for a 155 MB libx264 pass. Note audio is decided separately, so a
  PCM-audio camera file still gets the fast video path with only the audio re-encoded.
- **New `src/Api/Infrastructure/ProcessRunner.cs`** — drains both pipes concurrently *before*
  awaiting exit, and enforces a timeout with `Kill(entireProcessTree: true)`. Used by both the
  stream and display handlers so the pattern exists once rather than three times.
- **`ProcessRunner` deliberately takes no `CancellationToken`.** These runs populate a cache, so a
  browser giving up at nginx's timeout must not throw away work — the transcode finishes, caches,
  and the next click is instant. Previously a disconnect orphaned ffmpeg *and* discarded the work,
  so a slow video could never finish no matter how many times it was clicked.
- Temp file + `File.Move` on success, so a partial `.mp4`/`.jpg` is never served or cached.
- `-nostdin` (ffmpeg blocks on stdin otherwise) and `-loglevel error`.
- Single-flight `SemaphoreSlim` per media id; concurrent requests share one transcode.
- `proxy_read_timeout`/`proxy_send_timeout` raised to 300s on nginx's `/api/` location.
- Timeout configurable via `Video:TranscodeTimeoutMinutes` (default 30).

`ffprobe` ships with the `ffmpeg` package already installed in `src/Api/Dockerfile`, so the image
is unchanged.

**Follow-up fix, same day — the first cut of the probe was wrong and cost audio on every video.**
It asked for `-show_entries stream=codec_type,codec_name -of csv=p=0` and parsed positionally.
**ffprobe emits fields in its own internal order (`codec_name` before `codec_type`), not the order
requested**, so the output was `h264,video` against a parser expecting `video,h264`. Both codecs
came back null with `Probed: true`, which took the `-an` branch — **stripping the audio track from
every transcode** — and simultaneously missed the H.264 remux path, so an H.264 file re-encoded
for 86 seconds. One bug, both symptoms. Now one probe per stream (`-select_streams v:0` / `a:0`,
one field each), which cannot be misread; empty output correctly means the stream is absent.

**Also fixed: the player never recovered.** The `<video>` element failed once and sat on a grey
box forever even after the server finished and cached the file moments later — reopening the
lightbox played it instantly, which is what made the bug look intermittent. The element now
retries every 10 s for up to 30 attempts behind a "Preparing video…" status, then offers a manual
Retry. This is the piece that makes the "transcode survives client disconnect" decision above
actually pay off.

**Verified working on stage 2026-08-08** — `IMG_0750.MOV` (155 MB) probes `h264` + `aac`, so it
remuxes with `-c:v copy -c:a copy` and plays on first open with sound.

**Videos transcoded before this fix have no audio and must be regenerated.** Clear *both* the
files and the DB rows — `StreamMediaHandler` checks `File.Exists(outputPath)` **before** it reads
`video_transcodes`, so an orphaned MP4 on disk is still served even after the table is truncated:

```bash
set -a; . ./.env.stage; set +a
rm -f "$VIDEO_CACHE_DIR"/*.mp4
docker compose --env-file .env.stage exec -T postgres psql -U postgres -d homemediahub_db \
  -c "TRUNCATE public.video_transcodes;"
```

**Path gotcha that wasted a round trip:** the cache dirs are **not** under the data root.
Stage's are a *sibling* of `data/` (`/opt/homemediahub/stage/video-cache`), and prod's are on the
HDD entirely (`/mnt/storage/video-cache`). A delete aimed at `.../stage/data/video-cache/` silently
matches nothing and leaves the stale, audio-less file in place — which then looks exactly like the
fix not working. Always read the paths from `VIDEO_CACHE_DIR` / `DISPLAY_CACHE_DIR` /
`THUMBNAILS_DIR` in the env file. CLAUDE.md's Environments section has been corrected to say so.

**Still true after all that:** a long HEVC re-encode cannot complete inside one request. It will
now finish in the background and be cached, so the *second* attempt plays — but the first still
fails from the browser's point of view. If the library turns out to hold many HEVC videos, that
wants a background job with a polling endpoint. Deliberately not built yet: the remux path may
well cover almost everything, and that is cheap to find out by watching for `re-encode` in the
`ffmpeg {Mode} for {MediaId} took {Elapsed}` log line.

---

## Phase 10 — Tags page

> **Runs before Phase 9.** The phase numbers are historical labels, not execution order — Mike's
> call on 2026-08-08 was to build the Tags page before committing to the multi-day archive import.
> Stage is fully validated and cheap to iterate on; prod is a days-long run that makes any
> frontend work awkward (the two environments share ports, so developing during the import means
> developing against prod).
>
> **Complete 2026-08-09** — built and validated on stage the same day. See "Built" and
> "Validated on stage" at the end of this section.

**Goal:** make the YOLO tags reachable. Today they are computed on every photo, stored, and shown
only as passive text in the lightbox — see the audit under the 2026-08-07 validation above.

A new `/tags` page showing every tag in the database as a **chip**, and a paginated grid of the
photos carrying the selected tags. Selecting several chips filters with **AND** — a photo must
carry *all* selected tags to appear.

Independent of Phase 9 technically — it shares the tag data, not the pipeline — but scheduled
**before** it. Note the spec below was calibrated on stage's 276 files; re-read the scale caveats
before assuming the design holds at 190K.

### Measured tag distribution — 2026-08-08 (step 0, done)

Run against stage's 276 files. **Read every number below as a 276-photo sample, not a verdict:
prod is ~190K files, roughly 690×.**

- **46 distinct tags, with a brutal long tail.** `person` 99 photos (36% of the library), then a
  cliff — `book` 20, `car` 17, `boat` 9, nothing else above 8. **32 of the 46 tags cover ≤ 3
  photos**, 16 of them just one.
- **Confidence is low across that tail.** The sidecar threshold is 0.30 and much of the tail sits
  barely above it (`bird` 0.31, `backpack` 0.31, `horse` 0.32, `sandwich` 0.33, `airplane` 0.34).
  Real signal by contrast: `person` 0.78, `car` 0.71, `vase` 0.71, `remote` 0.72. `book` at 20
  photos / 0.54 is suspect — rectangular objects get called books.
- **Coverage: 111 of 276 photos (40%) carry no tags at all.**
- **Tags per photo:** 0 → 111 · 1 → 108 · 2 → 35 · 3 → 14 · 4 → 5 · 5 → 2 · 8 → 1.
  Only **57 photos (21%) have 2+ tags**; 22 (8%) have 3+.
- **Co-occurrence: 98 non-empty pairs out of a possible 1035 — 90% of two-chip selections return
  zero.** Of the 98 that work, only **13 return ≥ 3 photos**, and `person` is in 9 of those 13.
  The best pair in the library is `bottle`+`person` at 7 photos.

Sanity check passed at the same time: `media_files` 276 / live 276 / `media_embeddings` 276. The
earlier 273-vs-276 was three soft-deleted rows; the fresh import has no orphans.

**What this changes.** AND filtering is not inherently broken — it is starved by sample size. At
prod scale (if TestPhoto is representative; it spans 2007–2026 and looks like a genuine
cross-section, but it *is* a curated subset) `person` becomes ~68K photos and `bottle`+`person`
~4,800 rather than 7. So judge the design at prod scale, but build in the two things that hold at
**both** scales:

1. **Facet narrowing is mandatory, not optional.** Once a chip is selected, grey out chips that do
   not co-occur with the current selection — one extra query, and AND can never dead-end. Correct
   UX at 190K; at stage scale it is the only thing preventing a page that returns nothing 90% of
   the time.
2. **The chip floor must be relative, not a constant.** "≥ 4 photos" yields 14 chips today and all
   46 after the archive import, which defeats the point. Use top-N by count or a percentage of the
   library so it survives the scale jump.

**Explicitly rejected for v1: a confidence floor on results.** Filtering `media_tags.confidence >=
0.5` was considered and dropped — it would thin an already-starved dataset, and the junk tail is
better handled by the chip floor, which costs nothing in results. Revisit after the prod import.

**Pagination will rarely engage.** It matters only for single-chip `person` (99 today, ~68K later);
every multi-chip selection fits one screen at stage scale. Keep it, but do not design around it
being the common path.

Also note `person` (99) overlaps heavily with the existing `people` origin-class filter, so it may
earn less as a chip than its count suggests.

**API** (`src/Api`)

- `GET /api/tags?selected=car&selected=person` → `[{ tag, count }]` ordered by count desc. With no
  `selected`, the full vocabulary over non-deleted media. With `selected`, the counts are those
  *remaining* alongside the current selection — one endpoint serves both the initial chip row and
  the facet narrowing, and a zero/absent count is what greys a chip out.
- `GET /api/tags/media?tags=car&tags=person&page=1&pageSize=50` → `PagedResult<MediaListItemDto>`
  (the record already exists in `MediaDtos.cs`), ordered `TakenAt` desc to match Gallery.
- AND translation: group `media_tags` by `media_file_id` where `tag IN (@tags)`, then
  `HAVING COUNT(DISTINCT tag) = @tagCount`. Use `COUNT(DISTINCT …)` deliberately —
  `media_tags` has only a non-unique index on `Tag` (`MediaModelConfiguration.cs:67`), no unique
  `(media_file_id, tag)`. The sidecar dedupes labels per image and apply-enrichment replaces
  rather than appends, so duplicates should not occur, but `DISTINCT` removes the assumption for free.
- Retire the dead path as part of this: `GET /api/search/tags` + `TagSearchHandler` +
  `tagSearch()` in `search.ts` are unused and would otherwise leave two competing tag routes.

**Frontend** (`src/Frontend`)

- `src/api/tags.ts`; `pages/TagsPage.tsx`; route `/tags` + nav link.
- Chip row: tags above the relative floor, each with its count, toggle to select, multi-select,
  "Clear all", plus a "show all" toggle for the long tail. **Three visual states, not two**:
  unselected, selected, and *disabled* (would yield zero given the current selection). Selected
  and disabled must both be unmistakable — AND filtering is baffling when you can neither see
  what is active nor tell which chips are dead ends.
- Show the active filter in words ("car AND person") so the logic is self-evident.
- Grid reuses `PhotoCard` + `.gallery-grid`; pager reuses `.pager-btn`. No `distance` badge here —
  that field stays null outside semantic search.
- **The empty state still matters** even with facet narrowing — it is the safety net for races and
  for filter combinations the narrowing query does not cover. It must say so clearly and offer to
  drop a chip, not look broken.

**Decision to make before building:** whether the tag list and grid exclude `OriginClass == "meme"`
the way semantic search does (`SemanticSearchHandler.cs:45`), or show everything the way Gallery
does. Not obvious either way — worth a call rather than a silent default.

*(Resolved while building — the question rested on a wrong premise. Gallery does **not** show
everything: `GetMediaListHandler.cs:33` filters `OriginClass != "meme"`, as do semantic search and
On This Day. All three surfaces agree, so tags follow them. There was no real fork.)*

**Not in scope here:** the CLIP + tag hybrid *ranking* described in the audit. That changes the
semantic search box; this phase only adds browse-by-tag. They share the data, not the code path.

### Built — 2026-08-09

All of the above shipped. `dotnet test` green across the solution (Api **51** · GeoService 12 ·
MediaWorker 60 — Api was 31); `tsc -b && vite build` green.

**API** — `src/Api/Features/Tags/TagHandlers.cs` (`GetTagsHandler`, `GetTagMediaHandler`,
`TagCountDto`, shared `TagQueries.FilesCarryingAllTags`), `src/Api/Endpoints/TagsEndpoints.cs`,
mapped in `Program.cs`. `GET /api/search/tags`, `TagSearchHandler` and `tagSearch()` are deleted
as planned.

**Frontend** — `src/api/tags.ts`, `pages/TagsPage.tsx`, route `/tags`, nav link, and a `.tag-chip`
block in `global.css` built from the darkroom tokens (accent fill = selected, dashed hairline +
`--ink-28` = disabled, so the two states differ by more than colour).

**Two deliberate deviations from the spec above**, both forced by what EF Core 10 will actually
translate — and both found by a test, not on stage:

1. **AND is an `EXISTS` chain, not `GROUP BY … HAVING COUNT(DISTINCT tag)`.** One `EXISTS` per
   selected tag, each an index lookup on `media_tags(tag)`. It composes — the same builder backs
   both the facet counts and the paged grid, which the HAVING form cannot — and it is immune to
   duplicate rows by construction rather than by a defensive `DISTINCT`.
2. **The facet-count query's shape is load-bearing.** It aggregates into an anonymous type,
   orders, and only then projects into `TagCountDto`. EF Core 10 cannot order by a property of a
   positional record it has just constructed and throws "could not be translated"; over an
   anonymous projection the identical ordering translates to `ORDER BY count(DISTINCT …)`, and
   the record projection tacked on last translates away. `COUNT(DISTINCT media_file_id)` — the
   plan's original intent — does translate, but only in that arrangement.

**`Api.Tests/Features/Tags/TagQueryTranslationTests.cs` exists because of the above.** The other
tag tests run on the in-memory provider, which evaluates client-side and would have passed every
one of the broken formulations; these render the same queries to SQL through the real Npgsql
provider, which happens before any connection is opened, so no database is needed. The first run
failed immediately on `COUNT(DISTINCT)` — that would otherwise have reached stage as a 500 on
page load. Worth reusing wherever a query is more than a `Where` + `OrderBy`.

**Exercised locally, not on stage** — same sandbox limitation as Steps 3/6/7/8. Driven with
`vite` + Playwright against a throwaway mock config serving `/api/tags*` from a fixture shaped
like the measured distribution (person-dominant, a cliff, a 32-tag tail, sparse co-occurrence);
the config lived in `src/Frontend` only for the run and is deleted. Verified: chip row and
counts, selecting/deselecting, facet narrowing greying out dead ends, two-chip AND, pagination
(3 pages over 104), page reset on chip toggle, show-all/show-less, a rare selected chip staying
pinned when the row collapses, and the empty state's drop-a-chip recovery. Zero console errors
on `/tags`.

Two things that only this click-through would have caught:

- **Axios' default array serialization would have broken both endpoints.** It sends
  `?selected[]=a`, which binds to nothing on a .NET `string[]`. Fixed with
  `paramsSerializer: { indexes: null }`; the wire format was then read back off the mock server
  and confirmed as `?selected=person&selected=bottle`.
- **Selected chips showed their unfiltered count**, so "person AND bottle" rendered a 7-photo
  grid under a chip row reading `PERSON 104` / `BOTTLE 7` — two selected chips disagreeing about
  the same result set. Every chip's number now means one thing: photos you get with this chip in
  the selection.

### Validated on stage — 2026-08-09 ✅

Mike exercised `/tags` against the live stage stack the same day it was built. It works; no
defects reported, nothing to fix.

That closes the three items this section had left open. Both queries execute against real
Postgres, not just translate — the EXISTS chain and the `count(DISTINCT …)` facet aggregate both
run, which is what the translation tests could not prove on their own. The page renders real
YOLO tags rather than the fixture, and the chip row reads correctly at the real vocabulary size.

**Phase 10 is done.** ~~The one caveat that stage cannot settle stands: `CHIP_FLOOR = 24` is a
top-N floor calibrated against a 276-file sample.~~

### Chip floor re-judged and removed — 2026-08-16

Measured against the full library: **all 80 COCO classes present**, `person` at **114,793** (61%
of the library, 11× the next tag), decaying smoothly to `zebra` at 59. Then a genuine cliff, but
only at the very end — `toaster` 2, `hair drier` 1.

Two findings killed the floor rather than moving it:

1. **There is no cliff to cut at.** The counts decay continuously from rank 1 to 78. Any floor is
   arbitrary, and 24 fell in a comical place: `dog` (1,798) scraped in at rank 24 while `cat`
   (1,407) sat hidden at 28.
2. **Count order is not usefulness order.** `fire hydrant` (595) and `stop sign` (499) outrank
   `elephant` (393), `giraffe` (221) and `skis` (115). Any count-based floor admits street
   furniture before it admits the zoo, and reaching `zebra` would need a floor of 78 out of 80.

So the chip row now shows the **whole vocabulary**, and the "show all" toggle is gone along with
`CHIP_FLOOR` and the `.tag-chip-more` style. This is safe because the vocabulary is permanently
bounded — YOLOv8n emits COCO's 80 classes and no more, however large the library grows — and
facet narrowing already greys out chips that would return nothing. Revisit only if the tagging
model is ever swapped for one with an open vocabulary.

The alternative considered and rejected was curating the list (dropping COCO's street furniture
and kitchen appliances). It would read better, but it bakes a judgement about what the photos are
*for* into the code, and `fire hydrant` at 595 photos is at least honest about what the model saw.

---

## Phase 9 — Archive Import ✅ COMPLETE (drained 2026-08-16)

> Ran after Phase 10, as planned. Started 2026-08-14, queue fully drained 2026-08-16.

**Result:**

| | |
|--|--|
| completed | **188,291** |
| skipped_duplicate | 992 |
| failed | **171** (0.09%) |
| queued / processing / enriching | 0 |
| `media_files` rows | 188,300 |
| `media_embeddings` rows | 188,291 |
| files with ≥1 tag | 146,767 (78%) |
| videos | 7,204 |

**Enrichment coverage on completed work is 100%** — `media_embeddings` matches `completed`
exactly, and the 9-row gap between `media_files` and embeddings is precisely the AI-step failures
below (save-early gave them rows; the AI step never produced an embedding). The 22% of files with
no tags is expected: YOLOv8n's 80-class COCO vocabulary simply finds nothing it recognises in a
landscape or a document, and the confidence floor is 0.30.

**The 171 failures were triaged file-by-file on 2026-08-16** — every bucket probed on prod with
ffprobe/stat/header dumps, not guessed. Full evidence in `docs/backlog.md` → "Prod import failure
triage". Headline: **125 of the 171 are our bugs and the files are good**; 14 are macOS
AppleDouble sidecars that should never have been queued; 30 are genuinely dead or unexplained
source files. The fixes are Phase 12 below.

**Still to do from the original Phase 9 scope:**
- ~~Recreate the ivfflat index now that the table is at full size.~~ **Done 2026-08-16 — dropped
  instead of rebuilt. See below.**
- ~~Spot-check geo addresses and semantic search quality at prod scale.~~ **Done 2026-08-16, both
  pass. See "Geo and semantic spot-check" below.**
- ~~**Re-judge `CHIP_FLOOR = 24`** on the Tags page.~~ **Done 2026-08-16 — the floor was removed
  entirely, not retuned. See Phase 10 → "Chip floor re-judged and removed".**

### ivfflat: dropped, not rebuilt — 2026-08-16

The leftover said "rebuild at `lists ≈ 188`". Measuring first said something else entirely.

| | latency | recall @ top-15 |
|--|--|--|
| **exact scan, no index** | **354 ms** warm · 478 ms cold | **15/15** by definition |
| ivfflat `probes = 1` (the live setting) | 556 ms | **7/15** image · **0/15 text** |
| ivfflat `probes = 5` | 1,678 ms | 13/15 |
| ivfflat `probes = 10` | 1,012 ms | 14/15 |

**The index was slower than brute force at every probes setting, and never as accurate.**

The **0/15 on a text query vector** ("a photo of bridge", embedded through the sidecar's
`/embed/text` — the same path a real search takes) is the finding that matters: not degraded
ranking, a *disjoint* result set. Every semantic search run on prod between the import and this
date returned near-arbitrary photos out of one wrong cluster.

Cause, and it is the one the migration warned about: `AddMediaSchema` built the index on an
**empty table**, and ivfflat derives its list centroids by k-means over the rows present at build
time. Zero rows means 100 arbitrary centroids, and all 188,416 vectors were then filed against
clusters that model nothing — badly unbalanced, which is why raising `probes` cost *more* time
than scanning everything. The modality gap compounds it: text vectors sit in a region those
centroids never saw, so the single probed list held none of the true neighbours. `ivfflat.probes`
was also never set anywhere in the repo, leaving it at Postgres's default of 1.

A rebuild at `lists = 188` would have fixed the centroids, but exact search already wins on both
axes at this size, so `DropIvfflatEmbeddingIndex` removes the index outright — 100% recall, no
`lists`/`probes` to tune, and 491 MB of disk back (the index was 35× the main table's 14 MB,
because pgvector stores vectors inline while the table's own live in TOAST).

**If ~350 ms ever hurts**, the replacement is HNSW — pgvector **0.8.6** is installed, so it is
available: ~99% recall at single-digit ms, minutes to build, `hnsw.ef_search` to tune. Revisit if
the library doubles; do not reach for ivfflat again.

**Deployed and verified the same day.** `api` rebuilt on prod, migration applied at startup.
`media_embeddings` went 1012 MB → **520 MB** (the 491 MB index gone), leaving only
`pk_media_embeddings`.

The five-query spot-check is the confirmation, and it is not subtle. Every top hit now lands at
**0.67–0.70** — the very bottom of the ~0.68–0.80 band stage measured across the whole corpus —
where before the drop the results came from whichever arbitrary cluster `probes = 1` happened to
read. "birthday cake" tops out at **0.6702**, a better first hit than anything the stage session
recorded bar "Bells on the tower".

Two structural signals matter more than the numbers:

- **birthday cake** — ranks 1–2 are 19:14:50 and 19:14:44 the same evening (two frames of one
  cake), then **2019-03-28, 2025-03-28, 2022-03-28**: the same annual birthday across three
  different years, with nothing in the query mentioning a date.
- **bridge** — 20:13:08 and 20:13:10 on 2020-09-19, consecutive frames of one bridge.

Burst-shot and same-event clustering at the head of the ranking is what correct retrieval looks
like; a mis-probed cluster does not produce it. **Semantic search quality should now be re-judged
from scratch** — every impression formed of it on prod before this date was formed against the
wrong result set, and the deferred background-vocabulary scoring should be re-argued against this
baseline rather than the old one.

### Geo and semantic spot-check — 2026-08-16 ✅ both pass

**Geo: 119,316 GPS-tagged rows, 119,298 with an address — 100.0%**, eighteen short of everything,
which is a rounding artefact rather than a gap (almost certainly coordinates Nominatim genuinely
cannot resolve). The 20 most common addresses are all recognisable places — Rosenstraße in
Saarbrücken, улица Островского in Пушкино, Offenburg, Sarreguemines — with no transposed
coordinates and nothing at Null Island. The cache and the concurrency-1 consumer held across the
whole archive.

**63% of the library is geotagged** (119,316 / 188,416). Worth remembering for Phase 11: that is
the size of Photo of the Day's tier-2 fallback population.

**Semantic search: confirmed working, post-drop.** The strongest evidence is a *compositional*
query — **"dog on the beach"** returns Albert I-Promenade in Oostende three times (April 2023 and
April 2026) plus Monterosso al Mare in the Cinque Terre. Two concepts resolving to real beach
trips on two coasts is a much harder ask than a single noun.

The single-noun queries corroborate themselves through the address field, which is independent of
the embedding:

| query | evidence |
|-------|----------|
| bridge | top hit is *Passerelle René Ludwig* — a footbridge, named in its own address; ranks 2 and 4 are the Saar towpath at Leinpfad |
| snow | 2018-12-18 Pushkino, 2025-12-27, 2016-01-07, 2012-12-31 — deep winter every one |
| birthday cake | Mike's birthday (28 March, confirmed) across 2019 · 2022 · 2025, in two countries |

**car** remains the weakest query (0.699–0.704, the highest distances of the set), exactly as the
2026-08-07 stage session found. Consistent behaviour, not a new fault.

**Original scope, for reference:** all ~20 years of photos (~190K files, 750 GB) catalogued.

**Prerequisite met:** the full stage validation checklist passed on 2026-08-08 — see
"Stage validation complete" above. Items 1–10 are all exercised.

Trigger a prod scan after Phase 8 verification. Ingest (hash/convert/thumbnail/save) is
I/O-bound and finishes in hours; AI enrichment is CPU-bound and takes **days** — the library is
browsable throughout. Afterwards: recreate the ivfflat index, review the `failed` jobs list,
spot-check geo addresses and semantic search quality. Do **not** expect `Search:MaxCosineDistance`
to be tunable into good results — the 2026-08-07 validation showed no single constant separates a
narrow query from a broad one. Revisit the deferred background-vocabulary scoring instead.

**Watch it with `docs/monitoring.md`** — which tool answers which question, what healthy queue
depth looks like, and the metric-name cookbook. Note the known blind spot recorded there: file
failures never reach Grafana, so the Admin page is the only place they surface.

**Re-run the tag distribution afterwards.** Phase 10's design rests on stage's 276-file sample;
prod is ~690× that, and the plan's extrapolation assumes TestPhoto is representative. Worth
confirming rather than trusting.

---

## Phase 11 — Photo of the Day ✅ COMPLETE (2026-08-17)

> **Specified 2026-08-14, built, deployed and verified on prod the same day.** All seven steps
> shipped, the manual trigger was exercised against the real bot, and **the scheduled 15:00 run
> fired on its own and posted to the family channel.** Nothing is outstanding.
>
> Read "Built — 2026-08-17" for the implementation and the prod measurements, then "Fixed on the
> first real send" for the two bugs that verification flushed out, then "Verified on prod" for the
> outcome. Design and rationale: `architecture.md` §14.

**Goal:** a daily 15:00 post of one random `person`-tagged photo from On This Day to the family
Telegram channel — the n8n Workflow 3 behaviour, rebuilt as our own scheduled worker.

Mike already has the bot and the channel; the token and chat ID exist. `docs/telegram-potd-setup.md`
sections 1–3 still describe that setup correctly. **Nothing about this is an n8n job any more** —
no workflow, no webhook, no queue, no consumer. One `BackgroundService` in the Api, built the same
way as `ScanScheduler`.

### Step 1 — `potd_sends` table

Api migration (`dotnet ef migrations add AddPotdSends --project src/Api`). Entity in
`src/Data/Entities/`, mapped in `MediaModelConfiguration.ConfigureMediaEntities` alongside the
rest, so `WorkerDbContext` excludes it automatically like every other media table.

`sent_on date` PK · `media_file_id uuid` · `sent_at timestamptz`.

*Verify:* migration applies on a throwaway pg16 container; `WorkerDbContext` still produces no new
migration (the two-migrator invariant, §9).

### Step 2 — the pick handler

`src/Api/Features/PhotoOfTheDay/` — a MediatR request returning the chosen `MediaFile` id (and the
tier it came from), or null.

**Three tiers over a shared base filter**, per §14. Base = not deleted, not `meme`,
`MimeType not like 'video/%'`, `TakenAt` month+day match. Then:

1. `EXISTS` a `person` tag with `Confidence >= Potd:MinConfidence` (0.5)
2. else `Metadata.Latitude != null && Metadata.Longitude != null`
3. else the base set alone

First non-empty tier wins; **pick uniformly at random inside that tier**, not across the union.
Query lazily — tier 2 only runs if tier 1 is empty. Null (send nothing, log it) only when tier 3
is also empty.

**Select ids only, pick in memory** with `Random.Shared.Next(0, count)` — sidesteps translating
`random()`, and a day-sized slice is small even at archive scale.

Return the tier alongside the id and log it. It is a free read on whether AI tagging is earning its
place — mostly-tier-3 days mean `person` coverage is worse than assumed.

Do not extend `OnThisDayHandler` — it has no tag filter, includes videos, and `Take(200)`s after
ordering by date, so it is not a uniform sample. `/on-this-day` depends on its current shape.

*Verify:* unit tests over the in-memory provider for the filters **and the tier fallback order** —
seed a day with only geotagged photos and assert tier 2, a day with neither and assert tier 3, an
empty day and assert null, and a `person` tag at 0.4 confidence and assert it does *not* satisfy
tier 1. **Plus a translation test in `Api.Tests/Features/`** rendering each tier's query to SQL
through the real Npgsql provider. Phase 10 established why: the in-memory provider evaluates
client-side and passes formulations that throw at runtime against Postgres. The `EXISTS`-over-tags
shape is exactly the kind that broke there.

### Step 3 — the Telegram client

`AddHttpClient("TelegramBotApi")`, base `https://api.telegram.org/`; `POST bot{token}/sendPhoto`
as multipart (`chat_id`, `caption`, `photo`).

Get the image by sending `DisplayMediaRequest` in-process — it returns a file path and content
type, already handling HEIC→JPEG and caching. Do **not** re-implement conversion or thumbnailing
(the old Pi `TelegramBotService` did, and none of it is needed here), and do not call the Api's own
HTTP endpoint from inside the Api.

Caption: `TakenAt` + `MediaMetadata.Address` when present.

Check the file is under **10 MB** before posting — Telegram's `sendPhoto` limit — and downscale if
not.

*Verify:* against the real bot with a hand-picked media id, before any scheduling exists.

### Step 4 — manual trigger

**Before the scheduler, not after.** This is how Steps 2 and 3 get exercised at all — the
scheduler is the last thing to wire up, not the first, and nothing should require waiting until
15:00 to find out a token is wrong.

`POST /api/admin/potd/send` (JWT), body all-optional:

| Field | Effect |
|---|---|
| `month` / `day` | pick for that calendar date instead of today — the only way to exercise tier 1 on a date that actually has a `person` photo |
| `mediaFileId` | skip the pick entirely and send this exact photo |
| `dryRun` | run the pick, build the caption, report — but do not call Telegram |

Returns `{ sent, tier, mediaFileId, filePath, caption, skippedReason? }`.

Three deliberate differences from the scheduled path, all in §14:

- **ignores `Potd:Enabled`** — that flag stops the *scheduler* posting from stage; a button press
  is not an accident. Still fail clearly if token/chat ID are unconfigured.
- **ignores the already-sent-today guard** — so it can be run repeatedly while debugging.
- **does not write `potd_sends`** — a manual test at 11:00 must not make the real 15:00 run skip
  the day. Only the scheduler records sends.

Plus `GET /api/admin/potd/last` for the Admin card.

*Verify:* `dryRun` against a date known to have person photos → tier 1; a date with none →
tier 2 or 3; `mediaFileId` for a HEIC → confirms the `DisplayMediaRequest` path converts. Then one
real send to the channel.

### Step 5 — `PhotoOfTheDayScheduler`

`src/Api/Services/PhotoOfTheDayScheduler.cs`, mirroring `ScanScheduler`: config gate first,
`internal static NextRunTime` for testability, `Task.Delay` loop. By this point the pick and the
send are already proven through Step 4, so this step is only the clock.

The five deltas from `ScanScheduler` are in §14 and all of them matter:
`IServiceScopeFactory` (not a direct `AppDbContext`) · local time via `Potd:TimeZone` (not UTC) ·
try/catch around the per-run body (an escaped exception stops the **whole Api host**) ·
`potd_sends` guard against re-send on restart · `NextRunTime` kept static and testable.

Unlike Step 4's endpoint, the scheduled path **does** honour `Potd:Enabled` and the already-sent
guard, and **does** write `potd_sends`.

*Verify:* unit tests on `NextRunTime` — before/after 15:00, day rollover, month rollover, and a
DST boundary in `Potd:TimeZone`. Then confirm `TimeZoneInfo.FindSystemTimeZoneById` actually
resolves **inside the container** (`aspnet:10.0-noble` — check tzdata/ICU are present and
`InvariantGlobalization` is not set); it throws if they are not, and that is a runtime failure no
unit test on the host will catch.

**While here:** give `ScanScheduler` the same try/catch. It has the identical host-stopping
exposure today — a publish failure at 02:00 on the 1st would take the Api down.

### Step 6 — Admin page card + config

A "Photo of the Day" card on `/admin`, following the existing Scan card as the precedent for
button-triggers-endpoint: **Send Now** (with an optional date), the returned tier and photo, and
last-send info from `GET /api/admin/potd/last`. Client functions into
`src/Frontend/src/api/admin.ts` alongside `triggerScan`/`retryFailedJobs`.

Without this the endpoint means curl and a hand-copied JWT every time; the Admin page already
holds the JWT.

Reuse existing styles per the Step 10 no-redesign-yet rule. Note the bug that pass caught last
time: `.btn-primary` is `width: 100%` (styled for the login form) and stretches when used in a
section header — check any new button against that.

Env vars into both `.env.*.example` files: `POTD_ENABLED` (**false in stage** — stage must never
post to the family channel), `POTD_BOT_TOKEN`, `POTD_CHAT_ID`, `POTD_TIMEZONE`.

### Step 7 — docs

Rewrite `docs/telegram-potd-setup.md` sections 4–6 against the real implementation (keep 1–3, they
are still correct) and drop its superseded banner — the new section 4 is the env vars, and the new
"test it" section is the Admin card / `POST /api/admin/potd/send`, not n8n's Execute Workflow
button. Update `CLAUDE.md`: Out of Scope, the pipeline summary, and the service/config tables.

### Answered 2026-08-14 — nothing left blocking

Both open questions were settled by Mike and are built into Step 2 above:

1. **Fallback:** there should be a photo of the day *anyway*. Person → geotagged → any non-video.
   Not silence-on-no-person.
2. **`Potd:MinConfidence` = 0.5**, applied to tier 1 only, with the random pick happening inside
   whichever tier matched. Re-judge the number against the real post-import distribution.

The one thing still worth measuring after the archive import: **how often tier 1 actually fires.**
If most days fall through to tier 2 or 3, either the floor is too high or `person` coverage is
thinner than the stage sample suggested. The tier log line from Step 2 answers this without any
extra instrumentation.

### Built — 2026-08-17

All seven steps implemented in one pass. **Api tests 65 → 119**; full solution
Api 119 · GeoService 12 · MediaWorker 79, all green. `tsc -b && vite build` clean.

**Measured on prod first, before writing the pick** (read-only, over SSH — this is the
"re-judge `Potd:MinConfidence` against the real distribution" the spec asked for):

| | |
|---|---|
| Calendar days with any postable photo | **366 / 366** |
| Days where **tier 1** fires (`person` ≥ 0.5) | **366** — min 31 candidates, mean 281 |
| Days falling through to tier 2 / tier 3 | **0 / 0** |
| Base-set size per day | min 80 · mean 492 · max 2,178 |
| `person` tags | 114,641 total · 106,549 ≥ 0.5 · 92,916 ≥ 0.7 · 58,799 ≥ 0.85 |

**0.5 stands.** The confidence distribution peaks hard at 0.85–0.90 (34,724 tags), so the floor
discards a thin ~7% tail rather than cutting into the mass; a higher floor would be equally
defensible and would change nothing about what gets posted. **Tiers 2 and 3 never fire on this
library** — they are insurance against a smaller one, and the tier log line will say so if that
ever changes.

`Potd:TimeZone` defaults to **`Europe/Berlin`**: 63,336 of the addressed photos are in Germany,
more than the next three countries combined.

**Per step:**

- **Step 1** — `Data.Entities.PotdSend` (`sent_on date` PK · `media_file_id uuid` · `sent_at
  timestamptz`), mapped in `MediaModelConfiguration` with the rest, so `WorkerDbContext` excludes
  it automatically. Migration `20260817082929_AddPotdSends`. *Verified:* the idempotent script
  applied to a throwaway `pgvector/pgvector:pg16` container three ways — onto the existing
  prod-shaped schema (what will happen on deploy), onto a fresh database, and re-run over itself —
  all clean. `dotnet ef migrations has-pending-model-changes --project src/MediaWorker` reports
  none, so the two-migrator invariant holds.
- **Step 2** — `Features/PhotoOfTheDay/PickPhotoOfTheDayHandler.cs`. Three `internal static`
  `IQueryable<Guid>` builders over one `BuildBaseQuery`, walked lazily; ids only, then
  `Random.Shared.Next(0, count)` in memory. Returns `(MediaFileId, Tier, CandidateCount)` and logs
  the tier. **The base filter pins month/day to UTC** the same way `OnThisDayHandler` does, so the
  pick is drawn from the set `/on-this-day` shows — that is a different thing from `Potd:TimeZone`,
  which only decides which calendar day it is *now*.
- **Step 3** — `TelegramPhotoSender` (named `HttpClient` `TelegramBotApi`, 2-minute timeout).
  Treats a 200 with `{"ok":false}` as a failure and lifts Telegram's own `description` into the
  message — the token is never in it, because that string reaches the admin UI. The image comes
  from `DisplayMediaRequest` in-process (HEIC→JPEG + caching for free). Size handling is
  `Image.IdentifyAsync` header-only first, so the common case never decodes; only a file over 10 MB
  or 10,000px combined gets a full load, a resize into a 2560 box at q85, and a cached
  `{id}.potd.jpg` beside the display cache.
- **Step 4** — `POST /api/admin/potd/send` and `GET /api/admin/potd/last?limit=`. An explicit
  `mediaFileId` bypasses the pick, so a video can reach the sender that way — guarded with a
  content-type check that says so, rather than surfacing as an ImageSharp decode error.
- **Step 5** — `PhotoOfTheDayScheduler`. `IServiceScopeFactory` scope per run, timezone resolved
  **once** at startup (a bad id is a config error — it logs and declines to schedule rather than
  failing identically every day), try/catch around the per-run body, `internal static NextRunTime`.
  **`ScanScheduler` got the same try/catch**, as the plan asked.
- **Step 6** — Photo of the Day card on `/admin`: `dd.mm` date field (the endpoint takes a month
  and a day, never a year, so a date picker would be the wrong control), **Dry run ticked by
  default**, and a result block showing the picked thumbnail, the tier and the exact caption.
  `POTD_*` into all three env examples and onto `Potd__*` in compose. The new button is inside a
  card body, where `.btn-primary`'s `width: 100%` is correct — the Step 8 bug was in a section
  header. *Verified:* `docker compose config` on the server renders `Potd__Enabled: "false"` from
  `.env.stage.example`.
- **Step 7** — `telegram-potd-setup.md` rewritten from section 4 on and the superseded banner
  dropped (1–3 kept); `architecture.md` §14 marked built with the two implementation deltas
  flagged inline; `CLAUDE.md` updated.

**Two places the implementation departs from §14, both deliberate:**

1. **The claim row is released if the send fails.** §14 says insert-before-send and let 23505 mean
   "already sent". Taken literally that leaves a row asserting a send that never happened, and
   burns the day across a restart. The claim is written first (so a concurrent run or a restart
   *during* the send cannot double-post) and deleted if the send fails or throws — the guard is
   against a double *successful* send, not against retrying a Telegram outage.
2. **`Random.Shared.Next(0, count)` is covered by a statistical test.** 400 draws from 6
   candidates asserting every one is reachable — the old Pi implementation's `Next(0, len - 1)`
   could never pick the last element, and that is the kind of bug no example-based test finds.
   P(flake) ≈ 1e-32.

**Test inventory (54 new):** `PickPhotoOfTheDayHandlerTests` (12 — tier order, the 0.5 floor and a
configured override, video/deleted/meme/undated exclusion, 29 Feb, last-candidate reachability),
`PickPhotoOfTheDayQueryTranslationTests` (9 — every tier rendered through the **real Npgsql
provider**: `AT TIME ZONE` + `date_part` present, tier 1 becomes an `EXISTS` over `media_tags`,
and no tier emits `ORDER BY` or `LIMIT`, which is what keeps the sample uniform),
`PhotoOfTheDaySchedulerTests` (9 — before/after the slot, exactly at it, day/month/year rollover,
**both** Berlin DST boundaries holding the local hour while the UTC instant shifts, and a
`SendAtLocal` inside the spring-forward gap), `PotdConfigTests` (19 — blank-is-absent on every
getter, `Enabled` defaulting off for malformed values), `SendPhotoOfTheDayCaptionTests` (5).

**What is left — the real-bot verification.** Steps 3 and 4 both say "against the real bot", and
that cannot happen from here: it needs `POTD_BOT_TOKEN` and `POTD_CHAT_ID` in `.env.prod` (§4 of
`telegram-potd-setup.md`) and a rebuilt `api`. The order to run it in:

1. Add the four `POTD_*` vars, `docker compose --env-file .env.prod up -d --build api`.
2. `docker compose --env-file .env.prod logs api | grep PhotoOfTheDay` → expect
   `PhotoOfTheDayScheduler enabled — 15:00:00 Europe/Berlin`. **This is the check no unit test can
   make**: `FindSystemTimeZoneById` throws without tzdata/ICU in the container. Both are present in
   `hmh-prod-api-1` today (`/usr/share/zoneinfo/Europe/Berlin` exists, `libicuuc.so.74` is
   installed, `InvariantGlobalization` is not set), so this should pass — confirm it anyway,
   because a failure here is silent apart from that log line.
3. `/admin` → Photo of the Day → **Preview Pick** with the date blank. Expect `tier: person`.
4. Preview again with `mediaFileId` set to a known HEIC — proves the `DisplayMediaRequest` path.
5. Untick Dry run → **Send Now** → check the channel.
6. Then leave it to fire at 15:00 and check `potd_sends` has exactly one row for the day.

### Fixed on the first real send — 2026-08-17

Step 5 above failed on prod with a 500 and
`System.NotSupportedException: The 'bot…' scheme is not supported`. Two separate bugs, both fixed,
plus one hardening the second exposed. **Api tests 119 → 126** (`TelegramPhotoSenderTests`).

1. **A bot token is `{id}:{secret}`, and the colon made the request URI parse as absolute.**
   `client.PostAsync($"bot{token}/sendPhoto", …)` looks like a relative path but isn't:
   `Uri` reads everything before the first colon as a scheme, so the string parsed as an absolute
   URI named `bot7088711346:` and the `BaseAddress` was never consulted. Fixed with
   `BuildSendPhotoUri`, which builds `new Uri(baseAddress, $"/bot{token}/sendPhoto")` — **the
   leading slash is the fix**: a string starting with `/` cannot match the absolute-URI grammar
   (a scheme must begin with a letter), so it is unambiguously an absolute-path reference.
   Colons are legal in a path segment (RFC 3986 `pchar`), so the token still goes out unescaped,
   which Telegram requires.
   **Nothing in the dry-run path touches this**, which is why every preview passed — the URI is
   only built when a real send happens. Worth remembering: `dryRun` proves the pick, the
   conversion and the caption, and nothing whatsoever about reaching Telegram.
2. **The failure escaped as a 500 instead of a `skippedReason`.** The catch around the send was an
   allow-list (`HttpRequestException or InvalidOperationException or TaskCanceledException`) and
   `NotSupportedException` wasn't on it. That defeats the endpoint's whole purpose — §14 says a
   failure should be diagnosable from the response rather than the logs. Now catches broadly,
   re-throwing only cancellation of the caller's own token.
3. **Hardening that (2) made necessary:** `skippedReason` is rendered on the Admin page, and with
   a broad catch the message can now come from anywhere rather than only from strings we build.
   The failing exception quoted part of the URL back — which is where the token lives — so
   `SendPhotoOfTheDayHandler.Redact` strips the token from the message, including each half of it
   on its own, since splitting on the colon is exactly how that message was shaped.

Separately, Mike asked for the caption timestamp as `dd.MM.yyyy HH:mm` (was `d MMMM yyyy`, no
time). It is formatted **as stored, with no timezone conversion** — `taken_at` is the camera's
naive wall-clock time, so converting to `Potd:TimeZone` would reintroduce the +2h stage error.
No "exactly midnight means a path-year fallback, hide the time" guard, which
`formatGridDate`/`formatDetailDate` carry: only **2 of 187,369** dated files on prod are
midnight-exact, so it would never fire.

### Verified on prod — 2026-08-17

**The scheduled run fired on its own and posted to the channel. Phase 11 is closed.**

```
13:00:00.176Z  tier=person, 477 candidates, picked 823c51a8…
13:00:02.494Z  PhotoOfTheDayScheduler: 2026-08-17 sent 823c51a8… (tier person)
```

| Check | Result |
|---|---|
| Fired at the right instant | **13:00:00.176 UTC = 15:00:00 CEST**, 176 ms past the target |
| `Potd:TimeZone` resolved in the container | yes — `PhotoOfTheDayScheduler enabled — 15:00:00 Europe/Berlin` at startup. **This is the check no unit test can make**; tzdata + ICU are present in `aspnet:10.0-noble` and `InvariantGlobalization` is not set |
| Tier | `person`, 477 candidates for 17 Aug — consistent with the 366-day survey (min 31, mean 281) |
| `potd_sends` | exactly one row, `sent_on = 2026-08-17` |
| Claim row filled in | yes — `media_file_id` is the real photo, not the `Guid.Empty` placeholder, so the write-claim-then-fill-in path works |
| Elapsed pick → sent | 2.3 s (HEIC→JPEG conversion + upload) |
| Manual sends left no rows | confirmed — `potd_sends` was empty before 15:00 despite several test posts |

The photo that went out was taken 2025-08-17 11:09 in Saarbrücken, so the caption read
`17.08.2025 11:09 · Am Schwarzenbergbad, Rotenbühl, St. Johann, …, Deutschland`.

The scheduler's log line carries no `RequestId`/trace context, unlike the manual ones — which is
how to tell a scheduled run from an admin-triggered one in the logs.

---

## Phase 12 — Import failure cleanup ✅ COMPLETE (2026-08-16)

> **Specified, built, deployed and verified on prod the same day.** All 125 recoverable files are
> back and `processing_jobs` holds **zero** failed rows: 188,416 completed · 992
> skipped_duplicate · 0 failed, with `media_files` = `media_embeddings` = 188,416. Steps 1, 2, 3
> and 5 shipped; Step 4 was decided (delete) and executed. Read "Deployed and verified on prod"
> below for the outcome, and "Built" for the implementation notes.
>
> **Evidence lives in `docs/backlog.md` → "Prod import failure triage — 171 failed jobs".**
> Read it first — every claim below was measured on prod, and the section records what was ruled
> out as well as what was found. Do not re-derive it.

**Goal (met):** get the 171 failed jobs down to ~30 unavoidable ones, and make the next import's
failures readable in the Admin table on the first look instead of the third.

### Step 1 — sub-second video frame extraction (116 files) ✅ Built (2026-08-16)

`src/MediaWorker/Services/FileConvertService.cs`, `ExtractVideoFrameAsync` (line 22). Four changes,
all in this one file — backlog fixes A1–A4:

1. Seek to `min(1s, duration/2)` instead of the hardcoded `-ss 00:00:01`, or drop the seek and use
   `-ss 0 -vf thumbnail`. **Must be duration-relative** — four of the 116 files are *over* 1.0 s
   (up to 1.113 s) and still fail, so `if (duration < 1) skip the seek` does not cover them.
2. Throw if the output file does not exist after ffmpeg returns 0.
3. `-hide_banner -loglevel error` on the invocation.
4. Read stderr concurrently with `WaitForExitAsync` (latent pipe-buffer deadlock).

*Verify:* unit tests in `MediaWorker.Tests` over a 0.3 s and a 1.05 s fixture clip; then rebuild
`media-worker` on prod and hit **Retry All Failed** on `/admin`. Expect 116 jobs to go
`failed → completed` and 116 new rows in `media_files`. Any that stay failed are new information —
re-triage rather than retrying twice.

### Step 2 — truncated JPEG decoding (9 files) ✅ Built (2026-08-16)

`src/AiSidecar/inference.py` — backlog fix B5. Add at import time:

```python
from PIL import ImageFile
ImageFile.LOAD_TRUNCATED_IMAGES = True
```

These 9 already have `media_files` rows and are visible in the gallery (save-early worked); what
they lack is tags and an embedding. `ApplyEnrichmentConsumer` deliberately keeps the work file when
AI fails (`ApplyEnrichmentConsumer.cs:59-60`), so they retry without re-reading the source.

*Verify:* a sidecar test that truncates a JPEG fixture and asserts `analyze()` returns rather than
raising `InferenceError`. Then rebuild `ai-sidecar`, retry those 9, and confirm
`media_embeddings` reaches 188,300 — i.e. the gap between `media_files` and embeddings closes.

### Step 3 — scan-level junk filtering (14 files, and every scan hereafter) ✅ Built (2026-08-16)

`src/MediaWorker/Services/ScanService.cs:33-35` — backlog fix C6. Before the extension filter,
skip files whose name starts with `._` (macOS AppleDouble resource forks — 4096 bytes,
`00 05 16 07` magic, not media) and files of zero length. Log the skipped count from
`ScanConsumer` so the filtering is observable rather than silent.

*Verify:* a `ScanService` test with a temp tree containing `._IMG_1.JPG`, a 0-byte `IMG_2.JPG` and
a real `IMG_3.JPG` — only the third is returned. On prod, delete the 14 AppleDouble
`processing_jobs` rows after deploying, then re-scan and confirm they do not come back.

### Step 4 — decide the fate of the ~30 unrecoverable files ⏸ Mike's call, not code

Not code. Mike's call, once Steps 1–3 land and the list is short enough to read:

- **17 MOV** with no `moov` atom (9 of them zero-byte) — unplayable in any player.
- **12 zero-byte JPG** + **1 XML file named `.JPG`** — dead source data.
- **2 HEIC** that libheif 1.19.8 rejects with `No 'hvcC' box` despite being structurally intact
  (backlog bucket 6 — the `tmap`/gain-map explanation was tested and ruled out). Worth one attempt
  at re-copying from the phone or a backup, or a one-off `pillow-heif`/ImageMagick conversion,
  before writing them off.

The options are: leave them as a permanent ~30-row entry in Failed Jobs, or delete the source
files and clear the rows. There is no third option where the pipeline fixes them.

### Step 5 — make file failures visible in Grafana ✅ Built (2026-08-16)

The known blind spot, now that we have a real number to check it against. `IngestConsumer` catches
per-file failures and publishes `MediaProcessingFailed` rather than rethrowing, so MassTransit
records a *successful* consume — the "consume faults" and "error queue depth" panels stayed flat
through all 171 of these. `processing_jobs` and the Admin page were the only surfaces that showed
them, and only if you went looking.

Add an OTel gauge (or counter) for `processing_jobs` in `failed` state, exported from the Api's
existing `/metrics` endpoint, and a panel + alert threshold on the pipeline dashboard. Update
`docs/monitoring.md` to delete the blind-spot caveat once the panel exists.

This is what would have caught the 116-file bug on day one of the import instead of at the end.

### Built — 2026-08-16

Steps 1, 2, 3 and 5 are written and tested locally. **Nothing has been deployed to prod yet**, so
no file has actually been recovered: the 125 files land only after `media-worker` and `ai-sidecar`
are rebuilt there and the failed jobs are retried (below).

**Step 1 — `FileConvertService.ExtractVideoFrameAsync`.** The seek is now duration-relative:
ffprobe reports the container duration, `SeekSeconds` takes `min(1 s, duration/2)` and returns 0
(take frame 0) when that lands under 0.25 s or the duration is unknown. All four backlog fixes
landed: output existence is asserted, `-hide_banner -loglevel error` trims the error message to
the reason, and `RunAsync` now drains stdout/stderr *concurrently* with `WaitForExitAsync` instead
of after it, closing the pipe-buffer deadlock.

One thing the plan did not predict, found by running it: **how the bug surfaces depends on the
ffmpeg build.** Prod's exits 0 having written nothing (which is why the failure appeared one stage
later as a missing `/shared-temp` file); ffmpeg 7/8 fails the mjpeg encoder open and exits
non-zero instead. A fallback that only checked for a missing file would therefore fix prod and
break on any newer image, so the seeked attempt is wrapped in a `try` and *any* failure falls
through to a frame-0 retry. If that retry also fails, its error propagates — an unreadable file
still reports honestly, which is what the 17 dead MOVs should keep doing.

**Step 2 — `inference.py`.** `ImageFile.LOAD_TRUNCATED_IMAGES = True` at import, with the
reasoning inline so it does not read as a stray global.

**Step 3 — `ScanService`.** `DiscoverAsync` now returns a `ScanDiscovery` record (files +
`SkippedSidecars` + `SkippedEmpty`) instead of a bare list, and `ScanConsumer` logs the counts on
every scan, including when they are zero. Enumeration moved to `DirectoryInfo.EnumerateFiles` so
the zero-length check costs no extra syscall per file across 190K files.

**Step 5 — `FailedJobsCollector` (Api).** A `BackgroundService` samples
`count(processing_jobs WHERE status='failed')` every 30 s into an OTel observable gauge,
`homemediahub.processing_jobs.failed` → `homemediahub_processing_jobs_failed` at the Prometheus
endpoint. Sampling on a timer rather than inside the gauge callback is deliberate: observable
callbacks run synchronously on the scrape, and a DB round-trip there would tie scrape latency to
database health. Before the first successful sample the gauge reports *nothing* rather than 0 — a
gap is honest about "unknown" where a zero reads as "nothing is failing". Two dashboard panels
(stat + trend, thresholds at 1/50), and `docs/monitoring.md`'s "consume faults — any movement
means files are failing" line is corrected, since that was precisely the false reassurance.

**Tests — all green locally.** MediaWorker 79 (was 60): 10 `SeekSeconds` cases plus real
ffmpeg-driven conversions of 0.3 s / 1.05 s / 3 s clips, a banner-free error assertion, and 4 new
`ScanService` cases. Api 51 unchanged, full solution builds. AiSidecar 10 (was 5), run for the
first time in this repo: the truncated-JPEG test asserts both directions — it passes with the flag
and raises `InferenceError` with it off — plus tests that a missing file and an AppleDouble
sidecar still fail. `tests/conftest.py` is new: it stubs only *missing* heavy imports (torch,
ultralytics, open_clip, fastapi, …) so the sidecar's pure-logic tests run without a 2 GB torch
download, and never stubs Pillow, which the decode tests need for real.

**One existing-test trap worth knowing:** `ScanServiceTests.Touch` wrote `""`, so every fixture in
that file was zero-byte and the new filter skipped all of them. `Touch` now writes content and a
separate `TouchEmpty` exists for the zero-length cases. Any new test that creates a fixture file
has to write something into it.

### Deployed and verified on prod — 2026-08-16 ✅ every recoverable file is back

`media-worker`, `ai-sidecar` and `api` rebuilt on prod, then **Retry All Failed** from `/admin`.

| | before | after |
|--|--|--|
| completed | 188,291 | **188,416** (+125) |
| failed | 171 | **32** |
| `media_files` | 188,300 | **188,416** |
| `media_embeddings` | 188,291 | **188,416** |
| sagas in flight | 0 | 0 |

**+125 is exactly the predicted number** — all 116 sub-second videos and all 9 truncated JPEGs
recovered, nothing else moved. `media_files` and `media_embeddings` are now **equal**, so the
AI-step gap that bucket 3 opened is closed and enrichment coverage is 100% again.

The 14 AppleDouble rows were retried rather than deleted first, so they re-failed (harmless — the
retry set was 171, not 157) and were deleted afterwards. `ScanService` filters them from here on.

**Two things this confirmed that only prod could:**

- **Step 5 works end to end.** `homemediahub_processing_jobs_failed` read 46, then 32 within one
  30 s sample of the delete. The gauge is live on the pipeline dashboard.
- **The `-hide_banner -loglevel error` fix is real.** The remaining rows read
  `ffmpeg exited 183: [mov,mp4,m4a,3gp…` — the reason first, where before the message opened with
  the version banner and the whole `./configure` line. (Those 17 group as 17 distinct
  `error_message` values only because ffmpeg embeds a different pointer address per run.)

### What is left: Step 4, and nothing else

The 32 remaining failures are exactly the non-recoverable set from the triage, no surprises:

| n | `error_message` | what they are |
|---|---|---|
| 13 | `Image cannot be loaded. Available decoders:` | 12 zero-byte JPG + 1 XML named `.JPG` |
| 17 | `ffmpeg exited 183` | MOVs with no `moov` atom (9 of them zero-byte) |
| 2 | `heif-convert … No 'hvcC' box` | the two unexplained HEICs — real photos |

**Note the side effect of leaving them:** the Failed jobs panel now sits permanently at 32 and its
threshold shows orange forever, which costs the panel exactly the alarm value it was added for. An
argument for clearing the rows once the files are dealt with, not a reason to hide them.

**Do not trigger a re-scan before settling this.** The ~20 that are *not* zero-byte (8 truncated
MOVs, the XML, the 2 HEICs) have no `media_files` row and no active job, so a scan rediscovers
them and writes a *second* failed row each. That is pre-existing `ScanService` behaviour, not
something Phase 12 changed — the zero-byte and `._*` ones are the only ones the new filter stops.
Once the files are gone this stops mattering, and a re-scan becomes safe.

**Decided 2026-08-16 — delete the source files and clear the rows.** Mike's call: all 32 go,
including the 2 HEICs, which are written off rather than chased through a re-copy or a second HEIF
parser (two files out of 190K, and the triage had already ruled out the gain-map explanation).
The paths are dumped from `processing_jobs` and each one is re-probed before removal — a file that
turns out to be playable stops the run rather than being deleted on the strength of an old job
row. The manifest of what was removed is kept on the server at
`/opt/homemediahub/phase12-deleted-2026-08-16.txt`.

End state: `processing_jobs` has **zero** failed rows, the Failed jobs panel reads 0 and is an
alarm again rather than a permanent orange 32.

**Done 2026-08-16.** The 32 rows were cleared first and the files deleted afterwards, so the path
list had to be rebuilt: `find -size 0` over the library's extension set recovered the 21 zero-byte
ones, and the other 11 were read off the bucket-4/2/6 tables in `docs/backlog.md`. Sizes matched
the triage exactly (the 191 MiB and 475 MiB truncated MOVs included) and the ffprobe pass reported
nothing playable, so all 32 were removed. Manifest:
`/opt/homemediahub/phase12-deleted-2026-08-16.txt`.

**Phase 12 is complete.** Final prod state: 188,416 completed · 992 skipped_duplicate · **0
failed** · `media_files` = `media_embeddings` = 188,416.

### The re-scan verified Step 3 — and turned up one unrelated wrinkle

The confirming scan logged exactly what it should:

```
Scan …: skipped 14 AppleDouble sidecar(s) and 0 zero-length file(s)
```

14 sidecars filtered (they are still on disk, simply never queued again), and 0 zero-length
because they had just been deleted.

The same scan also logged **`discovered 992 new files`** — precisely the `skipped_duplicate`
count. Duplicates are rediscovered by every scan: `DiscoverAsync` excludes files present in
`media_files` or holding an *active* job, and a duplicate has neither — it never earned a
`media_files` row, and `skipped_duplicate` is terminal, not active. So each scan re-queues all
992, re-hashes them off the HDD and re-marks them, leaving another 992 job rows behind.

Harmless (dedup runs before conversion, so there is no AI, thumbnail or media-row work) and
**not a Phase 12 regression** — any re-scan of a fully-imported library would have done it. It
stayed invisible because `ScanScheduler` is off by default and nobody had re-scanned prod before.
Logged in `docs/backlog.md`; the fix is one more exclusion in `DiscoverAsync`.

---

## Phase 13 — On This Day: no cap, plus a date picker

> **Specified 2026-08-16 from Mike's report.** Independent of Phases 11 and 12 — no shared files,
> can run in any order. The smallest of the three.

Three things: the page silently shows a fraction of what matches, the date is hardcoded to today,
and the photos inside each year block run backwards through the day.

### The bug: `.Take(200)`

`src/Api/Features/Media/OnThisDay/OnThisDayHandler.cs:28` caps the query at 200 rows **before**
the in-memory `GroupBy(year)`. Nothing in the API or the UI says it did so.

Measured on prod for 16 August (1,049 rows match Mike's `count(*)`):

| Year | 2025 | 2024 | 2023 | 2022 | 2021 | 2020 | 2019 | 2018 | 2017 | 2016 | 2015 | 2014 | 2013 | 2011 | 2009 |
|------|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|-----:|
| n | 61 | 196 | 126 | 19 | 382 | 36 | 172 | 13 | 8 | 5 | 7 | 13 | 2 | 1 | 8 |

`OrderByDescending(TakenAt).Take(200)` therefore returns **all 61 of 2025 plus the newest 139 of
2024, and stops** — which is exactly what Mike saw: a "61 photos" section header, and a last
visible photo of `16.08.2024 09:37`. **Twelve years of that day never reach the browser.**

The two filters absent from Mike's SQL (`is_deleted`, `origin_class <> 'meme'`) remove nothing on
this date — verified, all four count variants return 1,049. The cap is the whole story.

### Scale to design for

Per-calendar-day totals across the whole library (`NOT is_deleted`, `taken_at NOT NULL`):

| min | median | mean | p95 | **max** |
|----:|-------:|-----:|----:|--------:|
| 82 | 454 | 514 | 1,014 | **2,252** (02 May) |

So "no pagination, everything on one page" means a **typical page of ~450 cards and a worst case
of ~2,250**. That is the number to design and test against, not 1,049.

The query itself is not the problem: `EXPLAIN (ANALYZE)` on the full 188K-row table gives a
parallel seq scan at **23 ms** for the 1,049-row result. Removing the cap does not make it slower —
`Take(200)` never saved a scan, only rows returned. **No index needed**; an expression index on
`(date_part('month'…), date_part('day'…))` was considered and rejected on this measurement.

### Step 1 — endpoint takes a limit, and the default is unlimited

`GET /api/media/on-this-day?month=&day=&limit=`

**`DashboardPage` calls the same endpoint** (`DashboardPage.tsx:35-37`) for its horizontal strip
and its Photo-of-the-Day pick — so simply deleting the `Take` would have the dashboard pull 2,252
items to render a strip. Add `limit` to `OnThisDayRequest`, applied only when `> 0`:

- **On This Day page** passes no limit → everything.
- **Dashboard** passes `limit=200` → byte-for-byte today's behaviour, no regression on a page
  that is not what Mike asked to change. (200 there is an arbitrary carry-over, not a considered
  number — revisit when Phase 11 replaces the dashboard's home-grown POTD pick.)

Default unlimited is deliberate: a caller that forgets a limit gets a slow page, which is
obvious, instead of a silently truncated one, which hid this bug for months.

### Step 2 — handler

`OnThisDayHandler.cs`:

1. Drop `.Take(200)` (or apply `req.Limit` when positive).
2. Replace `.Include(f => f.Thumbnail).Include(f => f.Metadata)` + in-memory `ToListItemDto()`
   with a `.Select(...)` projection into `MediaListItemDto`, so 2,252 rows do not materialise as
   full entities with two navigations each. Keep the `GroupBy(year)` client-side — it runs on the
   already-materialised list and is cheap.
3. **Pin the timezone.** `f.TakenAt.Value.Month` translates to `date_part('month', taken_at)`,
   which for a `timestamptz` resolves against the **session** TimeZone. Prod's session is UTC
   today, so this and `AT TIME ZONE 'UTC'` return an identical 1,049 — but the date picker makes
   the boundary user-visible (a 23:30 local photo can land on the neighbouring day), so make the
   choice explicit in the query instead of inheriting it from the session.

**Keep `OrderByDescending(TakenAt)` in the query — it is the *selection* order, not the display
order.** Newest-first is what makes `limit` mean "the most recent N" for the dashboard. The
display order Mike wants (ascending inside each year block) is a presentation concern and belongs
on the page — see Step 3. Flipping the query to `OrderBy` ascending would silently change which
photos the dashboard picks, because it slices the first 4 of each group.

*Verify:* an `Api.Tests` case seeding >200 rows across several years for one month/day and
asserting every row comes back, grouped, newest year first. That test is the regression guard —
its absence is why a 200-row cap survived into prod.

Read the **"Built — 2026-08-09"** notes on `TagHandlers.cs` before reshaping the query: that
section records which EF Core query shapes translate and which silently fall back to client
evaluation. Same trap here.

### Step 3 — date picker on the page

`src/Frontend/src/pages/OnThisDayPage.tsx`. Per Mike's answer: **the picker chooses a month and
day; the page still shows every year for it.** The year component of the picked date is display
only — picking 16.08.2024 shows all fifteen 16-Augusts, not just 2024's.

- Native `<input type="date">` — no new dependency, and the browser renders it in the user's
  locale. Default value: today.
- Hold the selection in the URL (`/on-this-day?d=2026-08-16`) via `useSearchParams`, so it
  survives reload, back-button and sharing. Absent → today.
- Feed `month`/`day` into the TanStack Query key so switching dates refetches and caches per day.
- **Three existing spots hardcode `today` and must follow the selection:** the `todayLabel()`
  header (lines 5-7 and 21), the query key and call (13-14), and the per-year section date
  `new Date(group.year, today.getMonth(), today.getDate())` (line 35) — that last one currently
  builds every section label from *today's* month/day.
- **Guard 29 February:** `new Date(2023, 1, 29)` silently rolls to 1 March, so the section heading
  would name the wrong day in non-leap years. Format the label from the group's year plus the
  selected month/day directly rather than round-tripping through `Date`.
- Add prev/next-day arrows either side of the picker — cheap once the date is state, and the
  natural way to browse an anniversary.
- Put the total in the header ("1,049 photos across 15 years"), so a future cap can never hide
  again.

**Sort photos ascending *within* each year block.** Year blocks stay newest-first (2025, then
2024, then 2023…) — only the photos inside a block change direction, so each day reads forward
from morning to night instead of backwards. Today everything is descending at both levels, because
`OnThisDayHandler` orders descending and `GroupBy` preserves source order.

Do this in `OnThisDayPage.tsx`, not in the handler. The API's descending order is load-bearing for
the dashboard: `DashboardPage.tsx:178` renders `group.items.slice(0, 4)`, so an ascending API
would swap its four most recent photos of each year for the four earliest — a silent change to a
page Mike did not ask to touch. Sorting on the one page that wants it keeps the two independent.

Sort **explicitly on `takenAt`** rather than calling `.reverse()` on the array. Reversing bakes in
an assumption about the API's order that nothing enforces; an explicit comparator survives the
handler being reshaped later:

```ts
const items = [...group.items].sort(
  (a, b) => new Date(a.takenAt ?? 0).getTime() - new Date(b.takenAt ?? 0).getTime()
)
```

Cost is nil — a few thousand comparisons per group, once per render, against a page that is
already mounting thousands of DOM nodes.

### Step 4 — make 2,250 cards on one page behave

Mike does not want pagination, so the render has to hold up at the p95 (~1,000) and the max
(~2,250) without virtualization.

Most of this is already in place and worth not breaking:

- `PhotoCard` already sets `loading="lazy"` (`PhotoCard.tsx:24`), and `.otd-full-photo` is a fixed
  `160×160` (`global.css:302`). Fixed-size boxes are what make native lazy-loading work properly —
  the layout is stable before any image loads, so the browser only fetches what is on screen.
  **Do not** make these cards intrinsically sized.
- Add `decoding="async"` alongside the existing `loading="lazy"`.

The remaining cost is DOM size: ~6 elements per card × 2,250 ≈ 13K nodes on the worst day.
**Measure it on 02 May before building anything for it.** If scrolling janks, the fix that fits
this page's structure is progressive reveal of the year sections with an `IntersectionObserver` —
render the first two or three year sections, mount the rest as they approach the viewport. That
keeps "everything on one page, no pagination" true while keeping the initial DOM small. Grid
virtualization is the wrong tool here: the page is a stack of variable-height year sections, not
one uniform grid.

*Optional, measured:* `MediaListItemDto.ThumbnailPath` is declared in `types/api.ts:4` and **never
read** anywhere in the frontend — `PhotoCard` builds its URL from `thumbnailUrl(item.id)`. At
2,252 items that is ~135 KB of dead JSON per page load. Dropping it touches every list endpoint,
so it is a separate cleanup, not part of this phase.

### Step 5 — verify on prod

1. `/on-this-day` with no query param → today, 15 year sections, **1,049** photos, oldest section
   2009. Cross-check against Mike's `count(*)`.
2. Pick 02 May → 2,252 photos. This is the stress case: time to first paint, scroll smoothness,
   payload size, and how many thumbnail requests nginx actually serves versus how many `<img>`
   tags exist (that ratio is the proof lazy-loading is working).
3. Pick 29 February → section headings read "29 February" in every year listed.
4. **Ordering, both levels at once:** year blocks descend (2025 → 2009) while the photos inside
   each block ascend. Easiest check on prod is the 16 August page — the 2025 block should now
   *open* with the earliest of its 61 photos and *end* on the latest, and the same within 2024's
   196. Confirm on at least two blocks, since a single block can look right by accident.
5. Dashboard's On This Day strip is visually unchanged — still the four **most recent** photos of
   each year, in descending order. This is the check that the sort stayed on the page and did not
   leak into the handler.
6. A day with nothing → the existing empty state, not a spinner.

### Built — 2026-08-17

Steps 1–4 are implemented. Step 5 (verify on prod) is **not yet run against a deployed build** —
what has been checked on prod so far is the query itself, read-only, from psql (below).

`dotnet test` green across the solution (Api **65** · GeoService 12 · MediaWorker 79 — Api was 51);
`tsc -b && vite build` green.

**API** — `OnThisDayHandler.cs` rewritten:

- `OnThisDayRequest` gains `Limit` (default `0` = unlimited); `.Take(200)` is gone.
- The query moved into an `internal static BuildQuery(db, month, day, limit)` so the translation
  test can render it to SQL. `Include(Thumbnail) + Include(Metadata)` + `ToListItemDto()` became a
  `.Select(...)` projection straight into `MediaListItemDto`.
- The month/day boundary is pinned to UTC with
  `TimeZoneInfo.ConvertTimeBySystemTimeZoneId(f.TakenAt!.Value, "UTC")`, which Npgsql 10 translates
  to `AT TIME ZONE 'UTC'`. That was the one shape here that had to be proven rather than assumed —
  see the translation test.
- `OrderByDescending(TakenAt)` stays: it is the *selection* order (what makes `limit` mean "the
  most recent N"), not the display order.
- `GET /api/media/on-this-day` gains `?limit=`, passed straight through.

The generated SQL, for the record:

```sql
SELECT m.id, m.mime_type, m0.thumbnail_path, m.taken_at, m.is_favorite,
       m.duration_sec IS NOT NULL, m.origin_class, m.origin_confidence, m1.address
FROM public.media_files AS m
LEFT JOIN public.media_thumbnails AS m0 ON m.id = m0.media_file_id
LEFT JOIN public.media_metadata   AS m1 ON m.id = m1.media_file_id
WHERE NOT (m.is_deleted)
  AND (m.origin_class <> 'meme' OR m.origin_class IS NULL)
  AND m.taken_at IS NOT NULL
  AND date_part('month', m.taken_at AT TIME ZONE 'UTC')::int = @month
  AND date_part('day',   m.taken_at AT TIME ZONE 'UTC')::int = @day
ORDER BY m.taken_at DESC
```

**Tests** — `Api.Tests/Features/Media/OnThisDayHandlerTests.cs` (9) and
`OnThisDayQueryTranslationTests.cs` (5). The regression guard the plan asked for seeds 450 rows
across five years and asserts all 450 come back grouped, newest year first; the rest cover the
limit (positive → most recent N, zero → unlimited), descending selection order, the
deleted/meme/other-day/undated filters, 29 February matching only leap years, and the projection
resolving both navigations *and* tolerating their absence. The translation tests exist for the
same reason the tag ones do — the in-memory provider evaluates client-side and would have passed a
UTC pin that Npgsql cannot translate.

**Frontend** —

- `api/media.ts`: `getOnThisDay(month, day, limit?)`, `limit` omitted from the querystring entirely
  when not passed.
- `DashboardPage.tsx`: passes `200`, and **`200` is in its query key**. Without that the two pages
  would share one TanStack cache entry under `['otd', month, day]` and whichever mounted first
  would decide whether the other got 200 rows or all of them.
- `OnThisDayPage.tsx`: rewritten. Native `<input type="date">` plus prev/next-day `pager-btn`
  arrows; selection held in the URL as `?d=YYYY-MM-DD` via `useSearchParams`, absent → today;
  `month`/`day` in the query key; header carries the total ("783 photos across 16 years").
  Photos sort ascending inside each year block via an explicit `takenAt` comparator; year blocks
  stay newest-first. The sort is on the page, not in the handler, so the dashboard keeps getting
  the four *most recent* photos per year.
- Two date traps handled explicitly rather than by round-tripping through `Date`: `?d=` is parsed
  with a regex (`new Date('2026-08-16')` is UTC midnight, and `getDate()` on it can answer with the
  previous day west of Greenwich), and `sectionLabel()` only trusts a constructed `Date` when it
  round-trips — `new Date(2023, 1, 29)` rolls to 1 March, so a non-round-tripping date drops the
  weekday instead of naming the wrong one. The "16 August" header label is formatted through leap
  year 2024 so 29 February renders at all.
- `PhotoCard` gains `decoding="async"` next to the existing `loading="lazy"`, with a comment
  recording *why* the fixed-size card classes must stay fixed-size.
- New `.otd-toolbar` / `.otd-date-input` / `.otd-total` in `global.css`, built from the darkroom
  tokens. `::-webkit-calendar-picker-indicator` needs `filter: invert(1)` or the native glyph
  renders near-black on the dark surface.

**Step 4 — nothing built for it, deliberately.** The plan says measure 02 May before building
progressive reveal, and that measurement needs the deployed page. `decoding="async"` and the
fixed-size-box comment are in; the `IntersectionObserver` year-section reveal is not.

**Measured on prod, read-only (2026-08-17).** Session `TimeZone` is `Etc/UTC`, so the UTC pin
changes no result today — it just stops the answer depending on that.

- **17 August: 783 photos across 16 years** (2025: 94 · 2024: 155 · 2023: 56 · 2022: 26 ·
  2021: 192 · 2020: 61 · 2019: 142 · 2018: 15 · 2017: 8 · 2016: 5 · 2015: 3 · 2014: 1 · 2013: 10 ·
  2012: 2 · 2011: 6 · 2006: 7). The old cap would have rendered 94 from 2025 plus the newest 106 of
  2024 and stopped — **two year sections out of sixteen**.
- **02 May, the worst day: 2,252 rows, `EXPLAIN (ANALYZE)` 108 ms** on the projected query with
  both LEFT JOINs (parallel seq scan on `media_files`, 27 ms, then PK index scans for the two
  navigations). Confirms the plan's call: no index, and dropping the cap costs nothing.

**Not yet done: Step 5.** Nothing is deployed — prod still runs `4ef4ce1`. The six checks in Step 5
all need the built page, and items 2 and 4 (scroll behaviour at 2,252 cards; both ordering levels
at once) cannot be settled any other way.

### Validated on prod, and the picker reformatted — 2026-08-17

Mike deployed `edd0478` and exercised the page: **it works, and 02 May renders fine** — so Step 5
is closed and Step 4's progressive reveal stays unbuilt, which is what the measurement was for.
2,252 cards on one page hold up on this hardware; `loading="lazy"` over fixed-size boxes is doing
its job.

One change requested: **the date field must read `dd.mm.yyyy`.**

`<input type="date">` renders its value in the browser's own locale and there is no attribute,
`lang`, or stylesheet that overrides that — and no locale renders dots anyway. So the visible
control is now a `.otd-date-btn` we format ourselves (`17.08.2026`, lucide `CalendarDays` icon),
with the native input kept only to supply the calendar popup, opened from the button via
`showPicker()`.

The native input is `position: absolute; inset: 0; opacity: 0; pointer-events: none` over the
button — **not** `display: none`, which would leave `showPicker()` nothing to anchor to.
Verified in Chromium that `showPicker()` on an opacity-0 / pointer-events-none input does *not*
throw (a standalone page, clicked through Playwright, reported `showPicker OK`); the button falls
back to `focus()` if a browser ever lacks the method, and the prev/next arrows and `?d=` work
either way. The `::-webkit-calendar-picker-indicator { filter: invert(1) }` rule went away with the
visible native input.

`toParam()` (the `?d=` / input value, `YYYY-MM-DD`) and `toDisplay()` (`dd.mm.yyyy`) are now
separate functions — the URL format is unchanged, only the rendering.

**Mike confirmed the reformatted picker on prod the same day. Phase 13 is closed** — all five steps
done, nothing deferred out of it except Step 4's progressive reveal, which the 02 May measurement
retired rather than postponed.
