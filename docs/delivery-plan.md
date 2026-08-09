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
- **ivfflat recall** — index created on an empty table; recreate once after the Phase 9 archive
  load. Verified *not* to be distorting results at stage scale (2026-08-07), but 276 rows is no
  evidence about 190K.
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

**Phase 10 is done.** The one caveat that stage cannot settle stands: `CHIP_FLOOR = 24` is a
top-N floor calibrated against a 276-file sample. Stage has 46 distinct tags; prod is ~690×
larger and `person` becomes ~68K photos. Re-judge the floor after the archive import — along with
the tag distribution itself, per the note under Phase 9.

---

## Phase 9 — Archive Import

> **Runs after Phase 10.** See the note there.

**Goal:** all ~20 years of photos (~190K files, 750 GB) catalogued.

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
