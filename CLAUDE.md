# HomeMediaHub

Self-hosted home photo and video library. Replaces the manual iPhone → MacBook → USB drive
workflow with automated ingestion, AI tagging, and semantic search. Runs on a single Dell
Precision 3620 (Ubuntu Server 24.04) as a Docker Compose stack. LAN-only.

## Current Status

**Phase 8 in progress — Steps 1–10 complete, Step 11 deferred** (2026-08-01).
**First stage rollout ran green on 2026-08-02**: the pipeline ingested the whole TestPhoto
library and enrichment applied both geo addresses and AI labels. The stack is no longer
sandbox-only — see "First stage rollout" in `docs/delivery-plan.md` for what was and wasn't
exercised, and the three bugs it flushed out.

Phase 8 replaces n8n with a RabbitMQ 4 + MassTransit 8.x message-driven pipeline
(**save-early**: media rows written at ingest so the gallery is browsable immediately; AI + geo
applied later as enrichment updates via a saga). The server will be torn down and rebuilt fresh
— no DB migration needed.

**Read these before touching pipeline code:**
- `docs/architecture.md` — the **target** architecture: message contracts, queue topology, saga
  design, Python↔MassTransit interop, `processing_jobs` lifecycle, schema ownership.
- `docs/delivery-plan.md` — the **step-by-step implementation plan** (Steps 1–10) with per-step
  verification, plus history of Phases 1–7.5.

n8n is fully **removed** as of Step 7 (`infra/n8n/`, `Dockerfile.n8n` deleted, compose service and
nginx `/n8n/` location gone) — do not resurrect it.

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
- **Step 5** — GeoService gained a `GeoLookupConsumer : IConsumer<GeoLookupRequested>` that calls
  the existing `ReverseGeocodeHandler` (cache + Nominatim, unchanged) via `IMediator` and publishes
  `GeoLookupCompleted`; any exception is caught and published as `Success:false` (mirrors the AI
  sidecar's always-publish contract). `geo-lookup` receive endpoint at prefetch/concurrency 1
  (Nominatim's hard 1 req/s limit). `GET /reverse`, `/health`, and the Nominatim throttle are
  untouched. OTel Prometheus metrics on `/metrics`. Dockerfile reworked for the `./src` build
  context (Contracts + GeoService, no libheif — GeoService does no media processing). Full
  solution: Api 31 · GeoService 12 · MediaWorker 60, all green.
- **Step 6** — `src/AiSidecar` reworked from a pure FastAPI HTTP service to a pika worker.
  `main.py` split into `inference.py` (model globals, idempotent `load_models()`, `embed_text()`,
  `analyze()` — logic unchanged from the old `/analyze` handler), `app.py` (FastAPI `lifespan` +
  `GET /health` + `POST /embed/text` only — `/analyze` is gone), `worker.py` (entrypoint:
  `load_models()` → Prometheus `start_http_server(8001)` → uvicorn on a daemon thread → pika
  `BlockingConnection` consume loop on the main thread, 5 s reconnect-on-error). The pika loop
  declares both fanout exchanges (`ai-analysis-requested`/`ai-analysis-completed`) + queue
  `ai-analysis` at `prefetch_count=1`; any `analyze()` exception becomes
  `{success:false, error:...}` and is **always** ACKed (never nacked — poison-image safety, per
  architecture.md §3/§5). `torch.set_num_threads(4)` + `OMP_NUM_THREADS=4`. `requirements.txt`
  gained `pika`/`prometheus-client`; new dev-only `requirements-dev.txt` adds `pytest`.
  `tests/test_worker.py` (5 tests, pure-helper coverage: envelope parsing, result-shape building,
  success/error `handle_message` paths — no model loading). Container build not verified in this
  sandbox (no Docker); import/test correctness verified via a venv with stub `torch`/`PIL`/
  `ultralytics`/`open_clip` modules.
- **Step 7** — `docker-compose.yml`: `n8n` removed; `api`/`geoservice` build contexts switched to
  `./src`; new `media-worker` service (`./src` + `MediaWorker/Dockerfile`, `/photo:ro` +
  `/thumbnails` + `/shared-temp`, `depends_on` postgres/rabbitmq/**api** healthy); new `rabbitmq`
  (`rabbitmq:4-management-alpine`, mgmt UI on LAN `:15672`, AMQP + metrics loopback-only,
  `enabled_plugins` = management + prometheus); `api`/`geoservice`/`ai-sidecar` gained
  `RABBITMQ_*` env + `depends_on rabbitmq`; `api` gained a `/health` curl healthcheck; new
  `prometheus` (loopback `:9090`, scrapes all 5 services) and `grafana` (LAN `:3001`, provisioned
  Prometheus datasource + a first-cut `pipeline.json` dashboard). `nginx.conf`'s `/n8n/` location
  removed. Env examples gained `RABBITMQ_USER`/`RABBITMQ_PASS`/`GRAFANA_PASSWORD`, dropped
  `SERVER_IP`. `Dockerfile.n8n` + `infra/n8n/` deleted.
  `docker compose config` validates cleanly; `geoservice` built successfully end-to-end proving
  the `./src` context rework. `api`/`media-worker` could not be built in this sandbox — their
  `aspnet:10.0-noble` base image (unchanged, set in Steps 3/4) isn't pullable from this network;
  real verification is the stage rollout checklist in `docs/delivery-plan.md`. Dashboard PromQL
  for MassTransit metrics is first-cut, unverified against a live scrape.
- **Step 8** — Frontend `/admin` page (sub-phase 8a). `src/Frontend/src/api/admin.ts` (scan
  trigger / pipeline-status / jobs / retry / retry-failed clients); `pages/AdminPage.tsx` — Scan
  card (optional path, shows returned `scanId`), Pipeline Progress card (polls `pipeline/status`
  every 5 s, progress bar, per-status counts, `completedLastHour`, stale-job warning), Failed Jobs
  table (paged, per-row Retry + "Retry All Failed"); route `/admin` + nav link. Reuses existing
  styling (`.card`, `.btn-primary`, `.pager-btn`, …) per the Step 10 no-redesign-yet rule; a few
  new admin-specific classes added to `global.css` built from the same design tokens. `npm run
  build` (tsc + vite) green; manual click-through against live stage **not done** — stage
  (`192.168.0.192`) unreachable from this sandbox, same limitation as Steps 3/6/7. Exercised
  locally instead (`vite dev` + Playwright, `admin.ts` temporarily mocked, reverted after) — caught
  and fixed a real bug: "Retry All Failed" reused `.btn-primary` (`width: 100%`, styled for the
  login form) and was stretching across the section header; fixed with an inline
  `width: 'auto'` override.
- **Step 9** — Docs pass. `README.md` fully rewritten for the Phase 8 stack (no n8n);
  `docs/fresh-stage-deploy.md`/`docs/prod-first-run.md` reworked (no init SQL, `data/rabbitmq`
  wipe/create steps, Admin-page/`POST /api/admin/scan` scan trigger replacing the n8n UI step,
  RabbitMQ mgmt UI + Grafana dashboard verify steps); `docs/telegram-potd-setup.md` marked
  superseded (kept for reference); `docs/architecture.md`'s stale status banner (said "Steps 1–6,
  not wired into compose yet") corrected to Steps 1–8 / fully wired / n8n removed.
- **Step 10** — Marked done **per Mike's direction**, not by building out the originally-scoped
  "full visual redesign of all pages." The Darkroom Archive foundation + a component pilot
  (Nav/Gallery/PhotoCard/Lightbox) already landed on `main` in commit `2ba29a3` (2026-07-04,
  before Phase 8 began). Remaining pages (including the new Admin page) inherit the same palette
  via `tokens.css`'s legacy-alias remap but haven't had their markup migrated to the pilot's
  deeper patterns (lucide icons, mono contact-sheet framing). Mike considers the foundation+pilot
  sufficient; further per-page migration is ad hoc future work, not a blocking Phase 8 item.

**The stage validation checklist is complete as of 2026-08-08** — all ten items exercised on real
hardware, including save-early ordering observed live, corrupt-file → `failed` + retry,
duplicate → `skipped_duplicate`, semantic search, HEIC display + video streaming, and Prometheus
targets + Grafana. See "Stage validation complete" and the dated sections in
`docs/delivery-plan.md` for what each one flushed out. **Watch an import with
`docs/monitoring.md`.**

**Phase 10 — Tags page — complete, validated on stage 2026-08-09.** `/tags` makes the YOLO tags
reachable: a chip row over the whole vocabulary with live photo counts, multi-select AND
filtering, and facet narrowing that greys out chips which would return nothing. Backed by
`GET /api/tags` (vocabulary + narrowed counts, one endpoint for both) and `GET /api/tags/media`
(paged grid) in `src/Api/Features/Tags/`. The dead `GET /api/search/tags` path is deleted.
Two query shapes there are load-bearing for EF Core translation reasons that are not obvious from
reading them — **read the "Built — 2026-08-09" notes in `docs/delivery-plan.md` before touching
`TagHandlers.cs`**. The chip row shows the **whole vocabulary** — `CHIP_FLOOR` and the "show all"
toggle were removed 2026-08-16 once prod could be measured: all 80 COCO classes present, counts
decaying smoothly with no cliff to cut at, and count order isn't usefulness order (`fire hydrant`
595 outranks `elephant` 393). Safe because YOLOv8n's vocabulary is permanently capped at 80.

**Phase 9, the prod archive import, is COMPLETE — the queue drained 2026-08-16.**
Final: **188,291 completed · 992 skipped_duplicate · 171 failed (0.09%) · 0 in flight.**
`media_files` 188,300 · `media_embeddings` 188,291 · 146,767 files (78%) carry ≥1 tag · 7,204
videos. Enrichment coverage on completed work is 100%. Worker containers are free to rebuild again.

The 171 failures were triaged file-by-file on 2026-08-16 — **125 of them are our bugs and the
files are good** (116 sub-second videos rejected by a hardcoded `-ss 00:00:01` frame seek; 9
truncated JPEGs that Pillow refuses to decode), 14 are macOS AppleDouble `._*` sidecars that
should never have been queued, and ~30 are genuinely dead source files (no `moov` atom, zero-byte)
or unexplained. Evidence per bucket in `docs/backlog.md` → "Prod import failure triage"; the fixes
are **Phase 12** in `docs/delivery-plan.md`.

**Phase 12 — import failure cleanup — is DEPLOYED AND VERIFIED ON PROD (2026-08-16).** Steps 1, 2,
3 and 5 are live: the video frame seek is duration-relative with a frame-0 fallback, the sidecar
sets `ImageFile.LOAD_TRUNCATED_IMAGES`, `ScanService` filters AppleDouble `._*` and zero-length
files and logs the counts, and `FailedJobsCollector` (Api) exports
`homemediahub_processing_jobs_failed` to two Grafana panels.
**All 125 recoverable files came back** — completed 188,291 → **188,416**, and
`media_files` = `media_embeddings` = 188,416, so enrichment coverage is 100% with no AI-step gap.
Step 4 was settled the same day: the 14 AppleDouble rows were deleted, and the 32 dead source
files (21 zero-byte, 8 truncated MOVs, 1 XML named `.JPG`, the 2 unexplained HEICs) were deleted
from disk with their rows — manifest at `/opt/homemediahub/phase12-deleted-2026-08-16.txt`.
**Final: 188,416 completed · 992 skipped_duplicate · 0 failed.** A confirming scan logged
`skipped 14 AppleDouble sidecar(s) and 0 zero-length file(s)`. Numbers and the full record in
`docs/delivery-plan.md` → Phase 12 → "Deployed and verified".

**Those totals are a 2026-08-16 snapshot, not the current library size.** Photos have been deleted
through the Review page since: as of **2026-08-22** `media_files` is **183,820**, with
`media_embeddings` tracking it 1:1. Expect it to keep drifting down — query the DB rather than
trusting this line whenever the exact count matters.

**Every scan rediscovers all 992 duplicates** (found by that confirmation scan). `DiscoverAsync`
excludes files in `media_files` or holding an *active* job; a duplicate has neither, since
`skipped_duplicate` is terminal. Each scan therefore re-hashes all 992 and writes 992 more job
rows. Harmless — dedup precedes conversion, so no AI/thumbnail/media-row work — and **not a Phase
12 regression**. Details and the one-line fix in `docs/backlog.md`.

**The ivfflat index is gone (2026-08-16) — semantic search runs an exact scan.** Measured on prod:
exact 354 ms with perfect recall, versus 556 ms and **7/15** (image vector) or **0/15** (real text
query vector) for the index at its live `probes = 1`. It was built on an *empty* table, so its
k-means centroids modelled nothing, and `probes` was never set anywhere. Slower *and* wrong, so
`DropIvfflatEmbeddingIndex` removes it rather than rebuilding at `lists = 188`. Do not re-add
ivfflat; if ~350 ms ever hurts, use HNSW (pgvector 0.8.6 is installed). The 2026-08-07 stage note
that "ivfflat was exonerated" was measured on 276 rows and is **not** evidence about prod.

Semantic search and geo were both re-checked on prod after the drop (2026-08-16) and both pass.
Search: top hits at 0.67–0.70, "dog on the beach" resolves to real beach trips on two coasts, and
"bridge" returns a photo whose own address names a footbridge. Geo: **119,316 GPS rows, 119,298
addressed — 100.0%**, all recognisable places (**63% of the library is geotagged**, which is Photo
of the Day's tier-2 population). **Any judgement of search quality formed before that date was
formed against the wrong result set** — re-argue the deferred background-vocabulary scoring
against this baseline, not the old one.

**Phase 9 is fully closed as of 2026-08-16** — the ivfflat rebuild, the geo and semantic
spot-checks, and the `CHIP_FLOOR` re-judgement are all done. Nothing is outstanding from it.

**What does NOT exist yet (do not assume it does):** full per-page visual redesign beyond the
Step 10 pilot pages; the CLIP + tag hybrid *ranking* (tags are browsable now, but semantic search
still ignores them).

**Phase 13 — On This Day uncap + date picker — is COMPLETE, validated on prod 2026-08-17.**
Api tests 51 → **65**. The `.Take(200)` is gone —
`OnThisDayRequest` has a `Limit` that defaults to **unlimited**, `GET /api/media/on-this-day` takes
`?limit=`, and the dashboard strip passes `200` (with `200` in its TanStack query key, or the two
pages would share one cache entry). The handler now projects into `MediaListItemDto` instead of
`Include`-ing two navigations, and pins the month/day boundary to UTC via
`TimeZoneInfo.ConvertTimeBySystemTimeZoneId(…, "UTC")` → `AT TIME ZONE 'UTC'`. `/on-this-day` has a
date picker + prev/next arrows, holds the date in the URL as `?d=YYYY-MM-DD`, shows the
total in the header, and sorts photos ascending **within** each year block while year blocks stay
newest-first — that sort lives on the page, not in the handler, because `DashboardPage` slices the
API's first four per year and must keep getting the most recent ones. Per-calendar-day counts run
median 454 / p95 1,014 / **max 2,252** (02 May); the new query measured **108 ms** there on prod, so
no index was added. **Step 4's progressive reveal was deliberately not built and is not needed** —
02 May renders fine on real hardware, which is what the plan said to measure before building it.
**The date field is a styled button, not a bare `<input type="date">`**: the native input renders
in the browser's locale and cannot be made to read `dd.mm.yyyy`, so it is hidden behind the button
(`opacity: 0`, *not* `display: none`) and supplies only the calendar popup via `showPicker()`.
Details in `docs/delivery-plan.md` → Phase 13 → "Built — 2026-08-17" and the note after it.

**Phase 14 — Immich ingestion — is PLANNED, NOT BUILT (specified 2026-08-29).** Mike's first real
iPhone backup put **350 photos** on prod. **Do not trigger a library scan until Steps 1–3 of
`docs/delivery-plan.md` → Phase 14 are done** — measured on the real tree, a scan today discovers
**721 files, 371 of them junk**.

`${PHOTO_DIR}/iPhone` is **not a photo folder — it is Immich's entire media root**
(`IMMICH_MEDIA_LOCATION`, `docker-compose.yml:63`) sitting inside our scan root, holding
`upload/` · `thumbs/` · `encoded-video/` · `backups/` · `library/` · `profile/`. `ScanService` has
**no directory exclusion at all**, and `.jpeg`/`.mp4` are supported extensions, so Immich's 350
1440×1920 `_preview.jpeg` files and 21 H.264 transcodes would each earn a real `media_files` row —
hash dedup cannot catch them, they are different bytes. The `_thumbnail.webp` files are spared only
because `.webp` is not in `SupportedExtensions`; that is luck, not design.

**Immich's storage template ships `enabled: false`**, so `library/` is empty and the originals sit
in the staging folder under GUID names (`a9e1715e-….HEIC`). Enabling it (Step 1, no code) moves
them to `library/mike/2026/2026-04-11/IMG_5181.HEIC` **and** makes `upload/` pure staging — which
also closes the worst bug: Immich streams uploads **straight to their final path** with no `.tmp`,
so a scan overlapping a backup hashes a half-written file, writes a row with a bogus hash, and
never revisits it because the path is then indexed. Silent, permanent, and invisible in the failed
jobs panels. Same hole has always existed for Samba copies.

Also open: Immich's 30-day trash deletes files from disk and **the scan is add-only** — no
reconciliation pass exists anywhere in the system. Phase 14 Step 5 makes it a reported number only.

The originals themselves are fine — verified byte-identical, EXIF (`DateTimeOriginal`, GPS) intact,
mtime backdated to the phone's `fileModifiedAt`, no timezone regression, and 0 of 20 sampled hashes
collide with the existing library.

**Former blind spot — now covered.** File-level processing failures still do not fault a consumer:
`IngestConsumer` catches them and publishes `MediaProcessingFailed` rather than rethrowing, so
MassTransit records a successful consume and the "consume faults" / "error queue depth" panels
stay flat even while files fail — that is by design, and it is how all 171 archive-import failures
passed unseen. **Watch the "Failed jobs" panels instead** (`homemediahub_processing_jobs_failed`,
sampled from `processing_jobs` by `FailedJobsCollector` every 30 s, live since 2026-08-16).

**Deferred work lives in `docs/backlog.md`** — currently the full prod-import failure triage
(all six error buckets, measured on prod, with what was ruled out as well as what was found).
Read it before touching `FileConvertService.cs`, `AiSidecar/inference.py`, or `ScanService.cs`.

**Phase 11 — Photo of the Day — is COMPLETE, verified on prod 2026-08-17.** The scheduled run
fired on its own at **13:00:00.176 UTC = 15:00:00 CEST** and posted to the family channel; one
`potd_sends` row, claim filled in, tier `person` from 477 candidates.
Api tests 65 → **128**. A daily 15:00 Telegram post, as `PhotoOfTheDayScheduler : BackgroundService`
in the Api (**not** an n8n job, not a queue/consumer) — `src/Api/Features/PhotoOfTheDay/` +
`src/Api/Services/PhotoOfTheDayScheduler.cs`, new `potd_sends` table via `AddPotdSends`.
The pick falls through three tiers (`person` ≥ 0.5 conf → geotagged → any non-video), random
**inside** whichever tier matched. `POST /api/admin/potd/send` (optional month/day / mediaFileId /
dryRun) fires it on demand and is wired to a Send Now card on `/admin`; that path deliberately
ignores `Potd:Enabled` and the already-sent guard and **never writes `potd_sends`**.

**Measured on prod 2026-08-17: all 366 calendar days clear tier 1** (min 31 candidates, mean 281),
so tiers 2 and 3 never fire on this library and `Potd:MinConfidence = 0.5` is settled — do not
re-litigate it without new data. `Potd:TimeZone` defaults to `Europe/Berlin`.

`POTD_ENABLED` **must stay false in stage** — stage and prod share one compose file and stage must
never post to the family channel.

**Three implementation notes that are not obvious from the code:**
- The `potd_sends` claim row is **released if the send fails**, so a Telegram outage neither burns
  the day nor records a phantom send. A `media_file_id` of all zeros would mean that path broke.
- The pick's month/day boundary is pinned to **UTC** like `OnThisDayHandler`'s. That is a different
  thing from `Potd:TimeZone`, which only decides which calendar day it is *now*.
- The caption timestamp (`dd.MM.yyyy HH:mm`) is formatted **as stored, never converted** —
  `taken_at` is the camera's naive wall-clock time, so converting it to `Potd:TimeZone` would
  reintroduce the +2h stage error. Same reason `Frontend/src/utils/mediaDate.ts` uses UTC getters.

**`dryRun` proves the pick, the HEIC conversion and the caption — and nothing at all about
reaching Telegram.** The request URI is only built on a real send; that is how a bot token's colon
made every preview pass while the first real send 500'd (`docs/delivery-plan.md` → Phase 11 →
"Fixed on the first real send").

Phases 1–7.5 are complete: full React frontend (8 pages + Review), JWT auth, semantic search,
albums, videos, geo map, tags, review/meme workflow, on-demand HEIC display + video transcoding.

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
| API | .NET 10 ASP.NET Core minimal APIs + MediatR — frontend queries, JWT auth, admin/scan endpoints, Photo-of-the-Day scheduler, owns media schema via EF migrations |
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
| `homemediahub_db` | postgres:5432 | media, tags, embeddings, jobs, identity, `enrichment_sagas`, `potd_sends` |
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
`rabbitmq/`, `prometheus/`, `grafana/`.

**The recreatable caches are NOT under the data root** — they sit beside the photo library, on
whichever disk holds it (prod's are on the HDD because 190K thumbnails are large):

| Cache dirs | stage | prod |
|------------|-------|------|
| `thumbnails/` · `video-cache/` · `display-cache/` | `/opt/homemediahub/stage/` | `/mnt/storage/` |

Exact paths come from `THUMBNAILS_DIR` / `VIDEO_CACHE_DIR` / `DISPLAY_CACHE_DIR` in the env file —
read them from there rather than assuming, and note stage's are a *sibling* of `data/`, not a child.

Shared temp for conversions: `/tmp/homemediahub-{stage|prod}/` → `/shared-temp`.
Env vars incl. `RABBITMQ_USER`, `RABBITMQ_PASS`, `GRAFANA_PASSWORD` (Phase 8); `SERVER_IP` removed.
`POTD_ENABLED` / `POTD_BOT_TOKEN` / `POTD_CHAT_ID` / `POTD_TIMEZONE` (Phase 11) — **`POTD_ENABLED`
must be `false` in stage.**

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

# Photo of the Day — preview the pick without posting (also a card on /admin)
curl -X POST http://192.168.0.192/api/admin/potd/send -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"dryRun":true}'
# RabbitMQ mgmt UI: http://192.168.0.192:15672   Grafana: http://192.168.0.192:3001
# Watching an import (which tool for what, metric-name cookbook): docs/monitoring.md

# one-off psql
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db

# clear a recreatable cache (paths come from the env file — never guess them)
set -a; . ./.env.stage; set +a          # or .env.prod
rm -f "$VIDEO_CACHE_DIR"/*.mp4          # forces re-transcode on next play
rm -f "$DISPLAY_CACHE_DIR"/*.jpg        # forces HEIC re-conversion
# video-cache is also indexed in the DB; clear both or the stale row wins
docker compose --env-file .env.stage exec -T postgres psql -U postgres -d homemediahub_db \
  -c "TRUNCATE public.video_transcodes;"

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
- **Photo of the Day is a `BackgroundService`, not a message.** One timer, one row, one HTTPS POST
  a day — no pipeline work and nothing to back-pressure. `ScanScheduler` publishes a message only
  because the *scan* belongs to MediaWorker; here the work belongs where the data (and the
  on-demand HEIC conversion) already is. Note this is the Api's **first outbound internet call** —
  "LAN-only" in these docs has always meant inbound.
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
  invisible — `docker compose --env-file .env.stage up -d --force-recreate <service>`. A plain
  `restart` is **not** enough — a container's mount namespace is fixed at creation.
- **Docker must not start before `/mnt/storage`.** After an unclean reboot, ext4 journal recovery
  can delay the HDD mount past Docker's start; Docker then auto-creates the missing bind-mount
  sources as empty dirs on the SSD, and every container sees an empty library — thumbnails,
  originals, display and video all 404 site-wide while metadata and counts render fine (the DB
  lives on the SSD) and Samba still shows every file, because it reads the host. Telltale journal
  line: `Directory /mnt/storage to mount over is not empty, mounting anyway`. Prevented by
  `/etc/systemd/system/docker.service.d/wait-for-storage.conf` (`RequiresMountsFor=/mnt/storage`);
  recover with `up -d --force-recreate`. Hit for real on 2026-08-22.
- **The host self-recovers from a freeze** (since 2026-08-22, after an 8 h 40 m silent lockup):
  the Intel PCH TCO hardware watchdog is armed by `itco-watchdog.service`, which runs a bare
  `modprobe iTCO_wdt` — Ubuntu deny-lists that module per kernel, so `modules-load.d` and
  `/etc/initramfs-tools/modules` both silently skip it and only an explicit modprobe works. With
  `kernel.panic=10`, `panic_on_oops=1`, `hardlockup_panic=1` and kdump, a hung kernel now resets in
  ~90 s and a panic leaves a vmcore in `/var/crash`.
- **`docker compose down -v` does not wipe bind mounts** — delete the data dirs manually for a
  clean slate.
- **Npgsql on Docker bridge**: keep `Keepalive=30;Connection Idle Lifetime=300` +
  `EnableRetryOnFailure` — conntrack drops idle TCP after ~4 min.
- **ImageSharp**: `Image.IdentifyAsync` (header-only) for metadata, never `LoadAsync`.
  ExifTool subprocesses capped at 30 s with `Kill(entireProcessTree: true)`.

## Out of Scope

Face/people recognition · remote access (LAN only) · browser upload (Samba + scan) ·
RAW files (CR2/ARW/NEF) · per-user library separation · Telegram dead-letter/error alerts.

**No longer out of scope:** Photo-of-the-Day. Dropped with n8n, asked for again on 2026-08-14, and
**built as Phase 11 on 2026-08-17** — a scheduled `BackgroundService` in the Api, **not** a
resurrected workflow. See `docs/architecture.md` §14, `docs/telegram-potd-setup.md`, and
`docs/delivery-plan.md`.

## Prior Art

Previous attempt in `C:/Users/Mike/Projects/PhotoLibrary` (Raspberry Pi 4, .NET, ML.NET):
lessons kept — EXIF extraction, Nominatim caching, HEIC via libheif/ffmpeg. Problems fixed
here — no iPhone auto-sync, poor ML.NET tagging, no semantic search.
