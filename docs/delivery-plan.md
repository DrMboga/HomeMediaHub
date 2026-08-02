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

**Not yet exercised:** 3 (save-early ordering observed live), 5 (corrupt file → `failed/ai` +
retry), 6 (duplicate → `skipped_duplicate`), 7 (semantic search), 8 (HEIC display + video
stream), 9 (Prometheus targets UP + Grafana dashboard — the dashboard PromQL is still
first-cut and unverified against a live scrape). Do these before the prod run.

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

## Open risks (accepted)

- **Stuck sagas** — no scheduler timeout (needs delayed-message-exchange plugin). Mitigated by
  the always-publish AI contract + Admin stale surfacing; add saga `Schedule` later if it bites.
- **MassTransit 8.x on net10** — targets net8; forward-compat expected, verify at first restore.
  OTel Prometheus exporters are pre-release — pin exact versions.
- **ivfflat recall** — index created on an empty table; recreate once after the Phase 9 archive load.
- **190K-message scan burst** — fine for RabbitMQ; ScanConsumer batches job inserts (~1000/SaveChanges).
- **pika heartbeat** on a pathological multi-minute image — reconnect loop + redelivery covers
  it; redelivery count isn't tracked (visible in Grafana if it loops).
- **Immich writing mid-scan** — partial file may hash oddly; unchanged from today, retry now exists.
- **Two migrators, one DB** — separate history tables; media tables change **only** via Api migrations.

---

## Phase 9 — Archive Import

**Goal:** all ~20 years of photos (~190K files, 750 GB) catalogued.

Trigger a prod scan after Phase 8 verification. Ingest (hash/convert/thumbnail/save) is
I/O-bound and finishes in hours; AI enrichment is CPU-bound and takes **days** — the library is
browsable throughout. Afterwards: recreate the ivfflat index, review the `failed` jobs list,
spot-check geo addresses and semantic search quality; tune `Search:MaxCosineDistance` against
the full library.
