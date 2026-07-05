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

# Phase 8 — RabbitMQ + MassTransit Migration 🚧 IN PROGRESS (Steps 1–3 complete 2026-07-05)

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

### Step 8 — Frontend Admin page (sub-phase 8a)

1. `src/Frontend/src/api/admin.ts` — clients for scan / pipeline-status / jobs / retry / retry-failed.
2. `src/Frontend/src/pages/AdminPage.tsx` + `/admin` route + nav link:
   - Scan card: trigger button (+ optional path input), shows returned `scanId`
   - Progress card: polls `pipeline/status` every 5 s (TanStack Query `refetchInterval`) —
     status counts, progress bar, completed/hour, stale warning
   - Failed jobs table: paged, per-row Retry, "Retry all failed"
3. Existing styling only — no redesign here.

**Verify:** `npm run build`; manual click-through against the stage stack.

### Step 9 — Docs

- `CLAUDE.md`, `README.md`: status, stack/service/port tables, commands, no n8n.
- `docs/fresh-stage-deploy.md`, `docs/prod-first-run.md`: no init SQL, new env vars,
  RabbitMQ/Grafana first login, new verification checklist.
- `docs/telegram-potd-setup.md`: mark superseded.

### Step 10 — Sub-phase 8b: frontend visual redesign

Own branch, only after the stage checklist below passes. Full visual redesign of all pages +
Admin using the **frontend-design skill** (distinctive, non-templated direction). Zero feature
or API changes — backend rollback must never entangle with UI churn.

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
