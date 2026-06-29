# HomeMediaHub

Self-hosted home photo and video library. Replaces manual iPhone → MacBook → USB drive workflow with automated ingestion, AI tagging, and semantic search. Runs on a single Dell Precision 3620 (Ubuntu Server 24.04) as a Docker Compose stack.

## Current Status

**Phase 8 in progress — architecture migration** (2026-06-29). Replacing n8n with a RabbitMQ + MassTransit message-driven worker pipeline. See **Implementation Plan** section at the bottom for the step-by-step guide. Server will be torn down and rebuilt fresh — no DB migration required. All n8n workflows, the `Dockerfile.n8n`, and `/api/tools/*` endpoints are being removed.

**Phase 6 complete** (2026-05-06). React/TypeScript frontend live at `src/Frontend/` (Vite + React Router + TanStack Query + react-leaflet). All 7 pages wired to the real API: Dashboard (POTD, OTD strip, library stats), Gallery (paginated grid, year/month nav), On This Day, Geo (Leaflet dark map with pins), Search (semantic), Albums, Videos. Lightbox with zoom, keyboard shortcuts, Info/Map tab. Three new API endpoints added: `/api/media/stats`, `/api/media/on-this-day`, `/api/media/geo`. Thumbnail, original, and stream endpoints made public (JWT cannot be sent from `<img>`/`<video>` tags; GUID URLs are unguessable on a LAN-only app).

**Post-Phase 7 improvements** (2026-05-31). Incremental improvements after Phase 7:
- **Lightbox file path visible**: Replaced hidden `<abbr title>` tooltip with a dedicated `.lb-filepath` row (monospace, full path, truncated with ellipsis, folder_open icon). Always visible above the date in the info bar.
- **Meme/Review feature**: New `/review` page and nav link. AI-flagged items (`screenshot`, `document`, `artwork`, `reference`) surface on the Review page. User clicks "Meme" → `origin_class = 'meme'` + `origin_reviewed = true` (hidden from gallery, search, On This Day). "Real Photo" → `origin_reviewed = true`, `origin_class` unchanged (item leaves review queue). New endpoint: `PATCH /api/media/{id}/review` with `{ action: "meme" | "real" }`. Gallery and semantic search both exclude `origin_class = 'meme'` items. `ReviewPage.tsx` uses optimistic card removal + server invalidation via TanStack Query. Review queue is **paginated** (`page`/`pageSize`, 50/page, URL-backed).
- **`origin_reviewed` column**: `BOOLEAN NOT NULL DEFAULT FALSE` added to `media_files`. Tracks whether a user has acted on the review for a given item. Review queue filters on `!origin_reviewed`.
- **OriginClass rework**: AI sidecar expanded from 3 to 6 origin classes: `photo`, `screenshot`, `document` (incl. passports/IDs/credit cards), `artwork` (drawings/paintings), `reference` (meter readings, signs, notices), `people` (portraits/group shots). `people` is not flagged for review. Gallery shows color-coded origin badge on each card (top-right, hidden for `photo`). Lightbox info bar shows origin class + confidence %. Gallery has origin filter chip row (All · Photo · People · Screenshot · Artwork · Document · Reference · Meme). Search has a type dropdown filter. Both `GET /api/media` and `GET /api/search/semantic` accept `?originClass=` param.
- **Migration squash**: `AddRefreshTokens` migration folded into `CreateIdentitySchema` — refresh token columns (`refresh_token_hash`, `refresh_token_expiry`) added directly to `AspNetUsers` CREATE TABLE. Only one EF migration remains. Fresh deployments run a single migration; no manual column patching needed.
- **ReviewMediaHandler NoTracking bug fixed**: `AppDbContext` sets `QueryTrackingBehavior.NoTracking` globally. `ReviewMediaHandler` was loading an entity and calling `SaveChangesAsync` with no tracking — the UPDATE was silently discarded. Fixed by switching to `ExecuteUpdateAsync` (direct SQL, no tracking required). Other mutation handlers (`UpdateMediaHandler`, `UpdateAlbumHandler`, `UpdateSeriesHandler`) correctly use `.AsTracking()` on their queries and are unaffected.

**Phase 7 complete** (2026-05-06). All Phase 6 validation bugs fixed: semantic search 500, lightbox file path tooltip, lightbox map z-index, gallery state reset on nav, videos pagination, AVI/Canon MIME type, `taken_at` fallback for images and videos, POTD widget.

**Operational note — file copy to server**: The `taken_at` mtime fallback only works if files are copied with mtime preservation. Use `rsync -av --times` or `scp -p` when copying photos to the server. Plain `scp` (without `-p`) sets mtime to the copy time, giving wrong dates for files without EXIF.

**Operational note — Docker bind mounts**: If a new directory is created on the host while a container is running (e.g. a new `NotIndexed/` subfolder in the photo library), the container may not see it due to a stale mount. Run `docker compose --env-file .env.stage up -d --force-recreate <service>` to reload the mount.

## Server Access

| | |
|--|--|
| SSH | `ssh mike@192.168.0.192` |
| IP | `192.168.0.192` (static DHCP — reserved by MAC address in home router) |
| App directory | `/opt/homemediahub/app` |

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700, 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | 2TB HGST Ultrastar 7K4000 SATA HDD — mounted at `/mnt/storage` |
| Network | LAN-only (no public internet exposure) |

## Stack

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (MIT) — iPhone backup only, no Immich UI used |
| Message broker | RabbitMQ 3 — all inter-service messaging |
| Processing orchestration | MassTransit (.NET) — consumers + saga state machine in `media-worker` |
| API | .NET 10 ASP.NET Core — frontend queries, JWT auth, scan trigger |
| Media worker | .NET 10 Worker Service (`src/MediaWorker/`) — ingest pipeline consumers + saga |
| Frontend | React + TypeScript |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n (object detection) + CLIP ViT-B/32 (semantic embeddings); RabbitMQ consumer via pika |
| Geocoding | .NET 10 — standalone geoservice microservice (Nominatim cache); RabbitMQ consumer via MassTransit |
| Media processing | ffmpeg, libheif + libde265 (HEIC/HEVC), ExifTool, SixLabors.ImageSharp — all run inside `media-worker` |
| Observability | Prometheus + Grafana — scrapes all services + RabbitMQ |
| Reverse proxy | NGINX |
| File sharing | Samba (native Ubuntu, not containerized) |
| Containerization | Docker Compose (single host) |

## Service Ports

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 (443 future) | Single public ingress |
| api | 8080 | Internal only |
| frontend | 3000 | Internal only |
| rabbitmq | 5672 (AMQP), 15672 (mgmt UI), 15692 (Prometheus metrics) | Internal; mgmt UI admin-only behind nginx |
| media-worker | 9090 (Prometheus /metrics only) | Internal only — no HTTP API |
| ai-sidecar | 8000 (health), 8001 (Prometheus /metrics) | Internal only — no HTTP API after Phase 8 |
| geoservice | 8001 | Internal only — HTTP reverse geocoding cache + MassTransit consumer |
| prometheus | 9090 | Internal only |
| grafana | 3001 | Admin only |
| postgres | 5432 | Internal only — hosts `homemediahub_db` |
| immich-server | 2283 | iPhone app connects here |
| immich-postgres | 5433 | Internal only |
| geoservice-postgres | 5434 | Internal only — hosts `geoservice_db` |
| redis | 6379 | Internal only — Immich only |

## Databases

| Database | Host | Purpose |
|----------|------|---------|
| `homemediahub_db` | postgres:5432 | Application data — media, tags, embeddings, users, saga state |
| Immich DB | immich-postgres:5433 | Immich internal — not accessed by custom services |
| `geoservice_db` | geoservice-postgres:5434 | Geoservice cache — reverse geocoding results keyed by rounded lat/lon |

Note: `n8n_db` is removed. The `media_processing_sagas` table is created in `homemediahub_db` by an EF migration in `src/MediaWorker/` and auto-runs on startup.

## Processing Pipeline

```
POST /api/admin/scan  OR  monthly IHostedService cron in API
              │ publishes ScanRequested
              ▼
        ┌─────────────┐
        │  RabbitMQ   │
        └──────┬──────┘
               │ FileDiscovered (one per new file)
               ▼
   ┌─────────────────────────┐
   │  MediaWorker            │
   │  IngestConsumer (.NET)  │  hash → dedup → convert → metadata → thumbnail
   └────────────┬────────────┘
                │ publishes MediaReadyForEnrichment
                ▼
   ┌────────────────────────────────┐
   │  Fan-out (2 queues, parallel)  │
   └────────┬───────────┬───────────┘
            │                   │
  AiAnalysisRequested   GeoLookupRequested
            │                   │
            ▼                   ▼
  ┌──────────────────┐  ┌──────────────────────┐
  │  AiSidecar       │  │  GeoService           │
  │  Python + pika   │  │  .NET MassTransit     │
  │  YOLOv8n + CLIP  │  │  + Nominatim cache    │
  └────────┬─────────┘  └──────────┬────────────┘
           │ AiAnalysisCompleted    │ GeoLookupCompleted
           └────────────┬──────────┘
                        ▼
   ┌─────────────────────────────────┐
   │  MediaWorker                    │
   │  ProcessingSaga                 │  waits for BOTH results
   │  MassTransitStateMachine        │  (or skips geo if no GPS)
   │  + PostgreSQL persistence       │  then publishes MediaSaveRequested
   └───────────────┬─────────────────┘
                   ▼
   ┌─────────────────────────┐
   │  MediaWorker            │
   │  MediaSaveConsumer      │  EF Core → homemediahub_db
   └─────────────────────────┘

Dead Letter Queue → processing_jobs.status = 'failed'
```

**Ingest strategy:** No Immich webhooks. Immich writes files to disk; `POST /api/admin/scan` is the ingest entry point. Monthly `ScanScheduler` IHostedService (inside the API) auto-publishes `ScanRequested` at `0 2 1 * *`.

## Environments

Two isolated environments run from the same `docker-compose.yml` via separate env files. Only one runs at a time (they share ports 80, 8080, etc.).

| Environment | Command | Photo library | Data root |
|-------------|---------|---------------|-----------|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/storage/Photo` (SATA HDD) | `/opt/homemediahub/data/` |

Stage uses the `TestPhoto` folder (copied from Windows laptop to server SSD) so the SATA HDD is not required during development. Switch to prod when the drive is mounted and the full library is ready.

## Key Paths on Disk

### Stage (SSD)
```
/opt/homemediahub/stage/
├── TestPhoto/            ← small curated test library (copied from Windows laptop)
│   └── iPhone/          ← Immich uploads in stage
├── thumbnails/           ← generated, recreatable
├── video-cache/          ← H.264 transcodes, recreatable
├── display-cache/        ← JPEG conversions of HEIC originals, recreatable
└── data/
    ├── postgres/              ← stage Postgres data volume
    ├── immich-postgres/       ← stage Immich Postgres data volume
    ├── geoservice-postgres/   ← stage GeoService Postgres data volume
    ├── rabbitmq/              ← stage RabbitMQ data volume
    └── grafana/               ← stage Grafana data volume

/tmp/homemediahub-stage/  ← shared temp for in-progress file conversions
```

### Prod (SATA HDD)
```
/mnt/storage/
├── Photo/               ← single source of truth for all originals
│   ├── 2005/ ... 2025/  ← existing archive (not reorganised)
│   └── iPhone/          ← new uploads from Immich (storage template: {y}/{y-MM-dd}/)
├── thumbnails/          ← 400 px WebP, recreatable from originals
├── video-cache/         ← H.264 MP4 transcodes of iPhone HEVC videos, recreatable
└── display-cache/       ← JPEG conversions of HEIC originals, recreatable

/opt/homemediahub/data/
├── postgres/              ← prod Postgres data volume
├── immich-postgres/       ← prod Immich Postgres data volume
├── geoservice-postgres/   ← prod GeoService Postgres data volume
├── rabbitmq/              ← prod RabbitMQ data volume
└── grafana/               ← prod Grafana data volume

/tmp/homemediahub-prod/  ← shared temp for in-progress file conversions
```

## Samba Share

`\\<server-ip>\Storage` maps to `/mnt/storage` (whole drive). Read-write for `mediahub` group.
- Windows: `\\<server-ip>\Storage`
- macOS: Finder → Go → Connect to Server → `smb://<server-ip>/Storage`

## Docker Compose Commands

Always pass `--env-file` to target the right environment. Omitting it defaults to `.env` which may not exist.

```bash
# Start stage
docker compose --env-file .env.stage up -d

# Start prod (ensure /mnt/storage is mounted first)
docker compose --env-file .env.prod up -d

# View logs
docker compose --env-file .env.stage logs -f api
docker compose --env-file .env.stage logs -f media-worker
docker compose --env-file .env.stage logs -f ai-sidecar

# Restart a single service
docker compose --env-file .env.stage restart media-worker

# Stop an environment
docker compose --env-file .env.stage down

# Rebuild custom images after code changes
docker compose --env-file .env.stage build api
docker compose --env-file .env.stage build media-worker
docker compose --env-file .env.stage build ai-sidecar
docker compose --env-file .env.stage build geoservice

# Trigger a library scan (requires JWT token)
curl -X POST http://192.168.0.192/api/admin/scan \
  -H "Authorization: Bearer $TOKEN"

# Watch RabbitMQ queues (admin UI)
# http://192.168.0.192:15672  — credentials from RABBITMQ_USER/RABBITMQ_PASS in env file

# View processing metrics
# http://192.168.0.192:3001  — Grafana (admin / GRAFANA_PASSWORD from env file)

# Run a one-off Postgres command (stage example)
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db
```

## Authentication

ASP.NET Core Identity. Two users: Mike and Badger. No roles — both users share the full library with equal access. JWT (15 min expiry) + refresh token (30-day sliding window).

## Design Decisions

- **EF Core over raw SQL.** Use EF Core entity navigation and `SaveChangesAsync` for all persistence. Raw SQL (`ExecuteSqlRawAsync` / `ExecuteSqlAsync` string concatenation) is forbidden — use proper type mappings instead (e.g. `Pgvector.Vector` with `HasColumnType("vector(512)")` for embeddings, standard EF navigation for relationships). Only reach for raw SQL when EF genuinely cannot express the operation (e.g. bulk operations with `ExecuteUpdateAsync`/`ExecuteDeleteAsync` are fine).
- **RabbitMQ + MassTransit is the orchestrator.** The API contains no processing logic — only frontend-facing queries, auth, and a scan trigger endpoint. All ingestion flows through RabbitMQ queues. MassTransit consumers in `media-worker` own the pipeline. MassTransit Sagas track per-file state through the AI + Geo fan-out.
- **MediaWorker hosts all processing logic.** File hashing (SHA-256), HEIC/video conversion (heif-convert + ffmpeg via `Process.Start`), EXIF/ExifTool metadata extraction, WebP thumbnail generation (ImageSharp), directory scanning, and dedup checking all live in `src/MediaWorker/Services/`. These were ported from the old `/api/tools/*` handlers.
- **HEIC/video conversion in MediaWorker.** HEIC conversion is two steps: `heif-convert -q 90` converts pixels, then `exiftool -TagsFromFile --Orientation` copies EXIF/GPS/XMP metadata from the original (`--Orientation` excluded because heif-convert already applied the rotation physically). `ffmpeg` extracts a frame from videos. Both use `Process.Start` in `FileConvertService.cs`. The `media-worker` Docker image installs `libheif-tools libde265 perl-image-exiftool ffmpeg` at build time.
- **Saga skips geo when no GPS.** When `IngestConsumer` detects no GPS coordinates, it sets `ProcessingState.GeoCompleted = true` immediately in the saga's `Initially` handler and does not publish `GeoLookupRequested`. This keeps the saga state machine symmetric — it always waits for both `HasAi` and `HasGeo` before finalizing, regardless of which path was taken.
- **AI Sidecar stays Python.** YOLOv8n (ultralytics) and CLIP (open_clip) have no viable .NET equivalents. The sidecar replaces FastAPI with a pika RabbitMQ consumer loop. The `/embed/text` HTTP endpoint (used by the API for semantic search) is kept as a minimal HTTP server on a side thread — it is the only HTTP endpoint remaining.
- **Thumbnails on filesystem.** nginx serves thumbnails directly via `X-Accel-Redirect` — no Postgres or .NET involved per thumbnail request.
- **Video on-demand transcoding.** iPhone HEVC videos are transcoded to H.264 MP4 on first play request by the .NET API. Cached in `/mnt/storage/video-cache/`. Original is never modified.
- **Single storage tree.** All originals (archive + new iPhone uploads) live under `/mnt/storage/Photo/`. No duplication. Immich's storage template writes new uploads to `Photo/iPhone/{y}/{y-MM-dd}/`.
- **Separate Postgres instances.** Immich gets its own Postgres (port 5433). The custom stack uses a separate container (port 5432) with one database: `homemediahub_db` (n8n_db removed). GeoService gets its own Postgres (port 5434).
- **GeoService is a standalone microservice.** Reverse geocoding is extracted into its own .NET 10 service (`geoservice:8001`) with its own database. It now consumes `GeoLookupRequested` messages from RabbitMQ via MassTransit in addition to keeping its existing HTTP `/reverse` endpoint. The cache logic (rounded lat/lon to 4 decimal places, Nominatim fallback) is unchanged.
- **Samba native, not containerised.** Running Samba in a container requires `--privileged` and `--network host`, eliminating isolation benefits.
- **All AI runs locally.** No cloud ML APIs. YOLOv8n for object detection, CLIP ViT-B/32 for semantic embeddings and natural-language search. Face recognition is explicitly out of scope.

## Out of Scope

- Face / people recognition
- Remote access (LAN only)
- Web browser upload (copy to Samba share + run Library Scanner)
- RAW camera files (CR2, ARW, NEF) — iOS devices only
- Per-user library separation — shared pool, both users see everything
- Telegram notifications (removed with n8n; can be added later as a dead-letter consumer)
- Photo of the Day Telegram post (removed with n8n; can be added as a scheduled worker later)

## Prior Art

Previous implementation in `C:/Users/Mike/Projects/PhotoLibrary` — ran on Raspberry Pi 4 with .NET 10, React, Postgres, ML.NET. Key lessons incorporated here: SignalR for real-time progress, EXIF via MetadataExtractor, geocoding via Nominatim, HEIC conversion via libheif/ffmpeg. Key problems it had: no iPhone auto-sync, poor ML.NET tagging results, no semantic search.

---

## Implementation Plan — Phase 8: RabbitMQ + MassTransit Migration

> Server will be torn down and rebuilt fresh. No EF migration squashing required. Start clean.

### Message contracts (`src/Contracts/`)

New `netstandard2.0`/`net10.0` class library referenced by Api, MediaWorker, and GeoService. No NuGet dependencies — pure C# records.

**File: `src/Contracts/Contracts.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net10.0</TargetFrameworks>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

**Files in `src/Contracts/Messages/`:**

```csharp
// ScanRequested.cs — triggers a directory scan
public record ScanRequested(string Path, string Source, string TriggeredBy, DateTime RequestedAt);

// FileDiscovered.cs — one per file found during scan
public record FileDiscovered(string FilePath, string Source, DateTime DiscoveredAt);

// MediaReadyForEnrichment.cs — published by IngestConsumer after hash/convert/metadata/thumbnail
public record MediaReadyForEnrichment(
    Guid     MediaFileId,
    string   FilePath,
    string?  WorkFilePath,    // non-null for HEIC/video (the converted JPEG temp path)
    string   FileHash,
    string   Source,
    string   MimeType,
    long     FileSize,
    int      Width,
    int      Height,
    float?   DurationSec,
    DateTime? TakenAt,
    double?  Lat,
    double?  Lon,
    string?  CameraMake,
    string?  CameraModel,
    string   ThumbnailPath);

// AiAnalysisRequested.cs — sent to AI sidecar queue
public record AiAnalysisRequested(Guid CorrelationId, string FilePath);

// AiAnalysisCompleted.cs — published by AI sidecar after inference
public record AiAnalysisCompleted(
    Guid    CorrelationId,
    AiTag[] Tags,
    float[] Embedding,
    string  OriginClass,
    float   OriginConfidence);

public record AiTag(string Tag, float Confidence, string Model);

// GeoLookupRequested.cs — sent to GeoService queue
public record GeoLookupRequested(Guid CorrelationId, double Lat, double Lon);

// GeoLookupCompleted.cs — published by GeoService after reverse geocoding
public record GeoLookupCompleted(Guid CorrelationId, string? Address);

// MediaSaveRequested.cs — published by Saga when both AI + Geo results are in
public record MediaSaveRequested(
    Guid      MediaFileId,
    string    FilePath,
    string    FileHash,
    string    Source,
    string    MimeType,
    long      FileSize,
    int       Width,
    int       Height,
    float?    DurationSec,
    DateTime? TakenAt,
    double?   Lat,
    double?   Lon,
    string?   Address,
    string?   CameraMake,
    string?   CameraModel,
    string    ThumbnailPath,
    AiTag[]   Tags,
    float[]   Embedding,
    string    OriginClass,
    float     OriginConfidence);

// MediaProcessingFailed.cs — published on unrecoverable error
public record MediaProcessingFailed(string FilePath, string Stage, string ErrorMessage, DateTime OccurredAt);
```

Add `<ProjectReference>` to `Contracts.csproj` from `Api.csproj`, `MediaWorker.csproj`, and `GeoService.csproj`.

---

### New service: `src/MediaWorker/`

`dotnet new worker -n MediaWorker -o src/MediaWorker --framework net10.0`

**NuGet packages for `MediaWorker.csproj`:**
```
MassTransit                           (latest 8.x)
MassTransit.RabbitMQ
MassTransit.EntityFrameworkCore
Npgsql.EntityFrameworkCore.PostgreSQL (10.0.x — match Api version)
pgvector.EntityFrameworkCore          (0.3.x)
EFCore.NamingConventions              (10.0.x)
SixLabors.ImageSharp                  (3.1.x)
prometheus-net                        (8.x)
```

#### `src/MediaWorker/Data/WorkerDbContext.cs`

Same entities as `AppDbContext` but without Identity tables. Registers the saga entity. Both `AppDbContext` and `WorkerDbContext` point at the same `homemediahub_db` connection string — they share the database but are separate EF contexts.

```csharp
public class WorkerDbContext : DbContext
{
    public WorkerDbContext(DbContextOptions<WorkerDbContext> options) : base(options)
    {
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    public DbSet<MediaFile>      MediaFiles     => Set<MediaFile>();
    public DbSet<MediaMetadata>  MediaMetadata  => Set<MediaMetadata>();
    public DbSet<MediaThumbnail> MediaThumbnails => Set<MediaThumbnail>();
    public DbSet<MediaTag>       MediaTags      => Set<MediaTag>();
    public DbSet<MediaEmbedding> MediaEmbeddings => Set<MediaEmbedding>();
    public DbSet<ProcessingJob>  ProcessingJobs => Set<ProcessingJob>();
    public DbSet<ProcessingState> ProcessingSagas => Set<ProcessingState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Mirror the ExcludeFromMigrations tables from AppDbContext
        builder.Entity<MediaFile>(e => { e.ToTable("media_files", "public", t => t.ExcludeFromMigrations()); /* ... */ });
        // ... same config as AppDbContext for all shared tables ...

        // Saga table — managed by EF migration in this project
        builder.Entity<ProcessingState>(e =>
        {
            e.ToTable("media_processing_sagas");
            e.HasKey(x => x.CorrelationId);
            e.Property(x => x.RowVersion).IsRowVersion();
        });
    }
}
```

Run `dotnet ef migrations add InitSaga --context WorkerDbContext` to generate the migration that creates `media_processing_sagas`. `Program.cs` calls `db.Database.MigrateAsync()` on startup so the table is created automatically on first run.

The entity classes (`MediaFile`, `MediaMetadata`, etc.) and `ProcessingJob` are copied from `src/Api/Data/` into `src/MediaWorker/Data/`. They are identical — duplication is intentional since the two services have different DbContext base classes and the Contracts library must remain dependency-free.

#### `src/MediaWorker/StateMachines/ProcessingState.cs`

```csharp
public class ProcessingState : SagaStateMachineInstance
{
    public Guid   CorrelationId  { get; set; }
    public string CurrentState   { get; set; } = null!;
    public byte[] RowVersion     { get; set; } = null!; // optimistic concurrency

    // From MediaReadyForEnrichment — stored for assembling the save request
    public string   FilePath      { get; set; } = null!;
    public string?  WorkFilePath  { get; set; }
    public string   FileHash      { get; set; } = null!;
    public string   Source        { get; set; } = null!;
    public string   MimeType      { get; set; } = null!;
    public long     FileSize      { get; set; }
    public int      Width         { get; set; }
    public int      Height        { get; set; }
    public float?   DurationSec   { get; set; }
    public DateTime? TakenAt      { get; set; }
    public double?  Lat           { get; set; }
    public double?  Lon           { get; set; }
    public string?  CameraMake    { get; set; }
    public string?  CameraModel   { get; set; }
    public string   ThumbnailPath { get; set; } = null!;

    // AI results
    public bool   AiCompleted      { get; set; }
    public string? OriginClass     { get; set; }
    public float   OriginConfidence { get; set; }
    public string? TagsJson        { get; set; }        // JSON-serialized AiTag[]
    public string? EmbeddingJson   { get; set; }        // JSON-serialized float[]

    // Geo results
    public bool   GeoCompleted { get; set; }
    public string? Address     { get; set; }
}
```

#### `src/MediaWorker/StateMachines/ProcessingStateMachine.cs`

```csharp
public class ProcessingStateMachine : MassTransitStateMachine<ProcessingState>
{
    public State Enriching { get; private set; } = null!;

    public Event<MediaReadyForEnrichment> ReadyForEnrichment { get; private set; } = null!;
    public Event<AiAnalysisCompleted>     AiDone            { get; private set; } = null!;
    public Event<GeoLookupCompleted>      GeoDone           { get; private set; } = null!;

    public ProcessingStateMachine()
    {
        InstanceState(x => x.CurrentState);

        Event(() => ReadyForEnrichment, x => x.CorrelateById(m => m.Message.MediaFileId));
        Event(() => AiDone,  x => x.CorrelateById(m => m.Message.CorrelationId));
        Event(() => GeoDone, x => x.CorrelateById(m => m.Message.CorrelationId));

        Initially(
            When(ReadyForEnrichment)
                .Then(ctx =>
                {
                    var msg = ctx.Message;
                    ctx.Saga.FilePath      = msg.FilePath;
                    ctx.Saga.WorkFilePath  = msg.WorkFilePath;
                    ctx.Saga.FileHash      = msg.FileHash;
                    ctx.Saga.Source        = msg.Source;
                    ctx.Saga.MimeType      = msg.MimeType;
                    ctx.Saga.FileSize      = msg.FileSize;
                    ctx.Saga.Width         = msg.Width;
                    ctx.Saga.Height        = msg.Height;
                    ctx.Saga.DurationSec   = msg.DurationSec;
                    ctx.Saga.TakenAt       = msg.TakenAt;
                    ctx.Saga.Lat           = msg.Lat;
                    ctx.Saga.Lon           = msg.Lon;
                    ctx.Saga.CameraMake    = msg.CameraMake;
                    ctx.Saga.CameraModel   = msg.CameraModel;
                    ctx.Saga.ThumbnailPath = msg.ThumbnailPath;
                    // Skip geo immediately if no GPS
                    ctx.Saga.GeoCompleted  = !msg.Lat.HasValue;
                })
                .PublishAsync(ctx => ctx.Init<AiAnalysisRequested>(
                    new(ctx.Message.MediaFileId, ctx.Message.WorkFilePath ?? ctx.Message.FilePath)))
                .If(ctx => ctx.Message.Lat.HasValue,
                    x => x.PublishAsync(ctx => ctx.Init<GeoLookupRequested>(
                        new(ctx.Message.MediaFileId, ctx.Message.Lat!.Value, ctx.Message.Lon!.Value))))
                .TransitionTo(Enriching));

        During(Enriching,
            When(AiDone)
                .Then(ctx =>
                {
                    ctx.Saga.AiCompleted       = true;
                    ctx.Saga.OriginClass        = ctx.Message.OriginClass;
                    ctx.Saga.OriginConfidence   = ctx.Message.OriginConfidence;
                    ctx.Saga.TagsJson           = JsonSerializer.Serialize(ctx.Message.Tags);
                    ctx.Saga.EmbeddingJson      = JsonSerializer.Serialize(ctx.Message.Embedding);
                })
                .If(ctx => ctx.Saga.GeoCompleted,
                    x => x.PublishAsync(ctx => ctx.Init<MediaSaveRequested>(BuildSave(ctx.Saga)))
                          .Finalize()),

            When(GeoDone)
                .Then(ctx =>
                {
                    ctx.Saga.GeoCompleted = true;
                    ctx.Saga.Address      = ctx.Message.Address;
                })
                .If(ctx => ctx.Saga.AiCompleted,
                    x => x.PublishAsync(ctx => ctx.Init<MediaSaveRequested>(BuildSave(ctx.Saga)))
                          .Finalize()));

        SetCompletedWhenFinalized(); // removes saga row from DB on finalize
    }

    private static MediaSaveRequested BuildSave(ProcessingState s)
    {
        var tags      = string.IsNullOrEmpty(s.TagsJson)
            ? Array.Empty<AiTag>()
            : JsonSerializer.Deserialize<AiTag[]>(s.TagsJson)!;
        var embedding = string.IsNullOrEmpty(s.EmbeddingJson)
            ? Array.Empty<float>()
            : JsonSerializer.Deserialize<float[]>(s.EmbeddingJson)!;

        return new MediaSaveRequested(
            s.CorrelationId, s.FilePath, s.FileHash, s.Source,
            s.MimeType, s.FileSize, s.Width, s.Height, s.DurationSec,
            s.TakenAt, s.Lat, s.Lon, s.Address,
            s.CameraMake, s.CameraModel, s.ThumbnailPath,
            tags, embedding, s.OriginClass ?? "photo", s.OriginConfidence);
    }
}
```

#### `src/MediaWorker/Services/` — ported from API handlers

Port the following handlers (remove MediatR, change namespace, inject ILogger):

| Source (src/Api/Features/Tools/) | Destination (src/MediaWorker/Services/) |
|----------------------------------|------------------------------------------|
| `Hash/HashFileHandler.cs` | `FileHashService.cs` |
| `Convert/ConvertFileHandler.cs` | `FileConvertService.cs` |
| `Metadata/ExtractMetadataHandler.cs` | `MetadataService.cs` (+ `MetadataResult` record) |
| `Thumbnail/GenerateThumbnailHandler.cs` | `ThumbnailService.cs` |
| `Scan/ScanDirectoryHandler.cs` | `ScanService.cs` (inject `WorkerDbContext`) |
| `Dedup/DedupHandler.cs` | `DedupService.cs` (inject `WorkerDbContext`) |

Each becomes a plain `public sealed class XxxService` with public async methods. No `IRequest`/`IRequestHandler`.

#### `src/MediaWorker/Consumers/ScanConsumer.cs`

```csharp
public class ScanConsumer : IConsumer<ScanRequested>
{
    private readonly ScanService _scan;
    private readonly ILogger<ScanConsumer> _logger;

    public async Task Consume(ConsumeContext<ScanRequested> ctx)
    {
        var files = await _scan.FindUnprocessedAsync(ctx.Message.Path, ctx.CancellationToken);
        _logger.LogInformation("Scan found {Count} new files under {Path}", files.Count, ctx.Message.Path);
        foreach (var file in files)
            await ctx.Publish(new FileDiscovered(file, ctx.Message.Source, DateTime.UtcNow));
    }
}
```

#### `src/MediaWorker/Consumers/IngestConsumer.cs`

```csharp
public class IngestConsumer : IConsumer<FileDiscovered>
{
    // Inject: FileHashService, DedupService, FileConvertService,
    //         MetadataService, ThumbnailService, ILogger

    public async Task Consume(ConsumeContext<FileDiscovered> ctx)
    {
        var filePath = ctx.Message.FilePath;
        var hash = await _hash.ComputeAsync(filePath, ctx.CancellationToken);

        if (await _dedup.ExistsAsync(hash, ctx.CancellationToken))
        {
            _logger.LogDebug("Skipping duplicate {FilePath}", filePath);
            return;
        }

        var mediaFileId = Guid.NewGuid();
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var needsConvert = ext is ".heic" or ".heif" or ".mp4" or ".mov" or ".m4v" or ".avi";

        string workFilePath = filePath;
        if (needsConvert)
        {
            workFilePath = $"/shared-temp/{mediaFileId}.jpg";
            await _convert.ConvertAsync(filePath, workFilePath, ext, ctx.CancellationToken);
        }

        var metadata = await _metadata.ExtractAsync(workFilePath, filePath, ctx.CancellationToken);
        var thumbnailPath = await _thumbnail.GenerateAsync(workFilePath, mediaFileId, ctx.CancellationToken);

        await ctx.Publish(new MediaReadyForEnrichment(
            mediaFileId,
            filePath,
            needsConvert ? workFilePath : null,
            hash,
            ctx.Message.Source,
            metadata.MimeType, metadata.FileSize,
            metadata.Width, metadata.Height,
            metadata.DurationSec, metadata.TakenAt,
            metadata.Lat, metadata.Lon,
            metadata.CameraMake, metadata.CameraModel,
            thumbnailPath));
    }
}
```

**Concurrency:** Set `EndpointConvention` or configure queue `PrefetchCount` in `Program.cs`. Start with `prefetch_count = 4` (mirrors the old n8n batch size of 4).

#### `src/MediaWorker/Consumers/MediaSaveConsumer.cs`

Port the logic from `SaveMediaHandler.cs` and `SaveMediaExtensions.cs`. Inject `WorkerDbContext` (using `.AsTracking()` for the add operation since `NoTracking` is the default). Write a `ProcessingJob` row on success with `status = "completed"`.

On consume failure (unhandled exception), MassTransit moves the message to the dead-letter queue. Add a dead-letter consumer `DeadLetterConsumer.cs` that writes `ProcessingJob` rows with `status = "failed"`.

#### `src/MediaWorker/Program.cs`

```csharp
var builder = Host.CreateApplicationBuilder(args);

// EF Core
builder.Services.AddDbContext<WorkerDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
        .UseSnakeCaseNamingConvention());

// Worker services
builder.Services.AddScoped<FileHashService>();
builder.Services.AddScoped<FileConvertService>();
builder.Services.AddScoped<MetadataService>();
builder.Services.AddScoped<ThumbnailService>();
builder.Services.AddScoped<ScanService>();
builder.Services.AddScoped<DedupService>();

// MassTransit
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<ScanConsumer>();
    x.AddConsumer<IngestConsumer>(cfg => cfg.PrefetchCount = 4);
    x.AddConsumer<MediaSaveConsumer>();

    x.AddSagaStateMachine<ProcessingStateMachine, ProcessingState>()
        .EntityFrameworkRepository(r =>
        {
            r.ConcurrencyMode = ConcurrencyMode.Optimistic;
            r.AddDbContext<DbContext, WorkerDbContext>((_, opts) =>
                opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection"))
                    .UseSnakeCaseNamingConvention());
        });

    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"], "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:User"]!);
            h.Password(builder.Configuration["RabbitMq:Pass"]!);
        });
        cfg.UsePrometheusMetrics(serviceName: "media-worker");
        cfg.ConfigureEndpoints(ctx);
    });
});

// Expose Prometheus /metrics on :9090
builder.Services.AddSingleton<KestrelMetricServer>(_ =>
    new KestrelMetricServer(port: 9090));

var host = builder.Build();

// Auto-run EF migration for saga table
using (var scope = host.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
    await db.Database.MigrateAsync();
}

host.Services.GetRequiredService<KestrelMetricServer>().Start();
await host.RunAsync();
```

#### `src/MediaWorker/Dockerfile`

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Contracts/ src/Contracts/
COPY src/MediaWorker/ src/MediaWorker/
RUN dotnet publish src/MediaWorker/MediaWorker.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    libheif-tools libde265-0 perl-image-exiftool ffmpeg \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "MediaWorker.dll"]
```

---

### Modify `src/Api/`

**Add NuGet packages:**
```
MassTransit
MassTransit.RabbitMQ
prometheus-net.AspNetCore
```
Add `<ProjectReference>` to `Contracts.csproj`.

**`Program.cs` additions:**
```csharp
// MassTransit (publish-only — no consumers in API)
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"], "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:User"]!);
            h.Password(builder.Configuration["RabbitMq:Pass"]!);
        });
        cfg.ConfigureEndpoints(ctx);
    });
});

// Prometheus
app.UseHttpMetrics();
app.MapMetrics("/metrics");

// Monthly scan scheduler
builder.Services.AddHostedService<ScanScheduler>();
```

**Add `src/Api/Features/Admin/ScanEndpoint.cs`** (or add to existing `AdminHandlers.cs`):
```csharp
adminGroup.MapPost("/scan", async (IPublishEndpoint bus, ClaimsPrincipal user, CancellationToken ct) =>
{
    await bus.Publish(new ScanRequested("/photo", "library", user.Identity!.Name!, DateTime.UtcNow), ct);
    return Results.Accepted();
}).RequireAuthorization();
```

**Add `src/Api/Services/ScanScheduler.cs`:**
```csharp
public class ScanScheduler : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = new DateTime(now.Year, now.Month, 1, 2, 0, 0, DateTimeKind.Utc).AddMonths(1);
            await Task.Delay(next - now, stoppingToken);
            await using var scope = _scopes.CreateAsyncScope();
            var bus = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
            await bus.Publish(new ScanRequested("/photo", "scheduled", "cron", DateTime.UtcNow), stoppingToken);
        }
    }
}
```

**Delete entirely:**
- `src/Api/Features/Tools/` (whole folder: Hash, Convert, Metadata, Thumbnail, Scan, Dedup, RecordFailure)
- `src/Api/Endpoints/ToolEndpoints.cs`

Remove the `ToolEndpoints` registration call from `Program.cs`.

The `POST /api/media` save endpoint in `SaveMediaHandler.cs` is also deleted — saving is now done entirely by `MediaSaveConsumer` in the worker. The `SaveMedia` feature folder can be removed too.

---

### Modify `src/GeoService/`

**Add NuGet packages:**
```
MassTransit
MassTransit.RabbitMQ
prometheus-net.AspNetCore
```
Add `<ProjectReference>` to `Contracts.csproj`.

**Add `src/GeoService/Consumers/GeoLookupConsumer.cs`:**
```csharp
public class GeoLookupConsumer : IConsumer<GeoLookupRequested>
{
    private readonly IMediator _mediator; // reuse existing MediatR handler

    public async Task Consume(ConsumeContext<GeoLookupRequested> ctx)
    {
        var result = await _mediator.Send(
            new ReverseGeocodeRequest(ctx.Message.Lat, ctx.Message.Lon), ctx.CancellationToken);
        await ctx.Publish(new GeoLookupCompleted(ctx.Message.CorrelationId, result.Address));
    }
}
```

**`Program.cs` additions:**
```csharp
builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<GeoLookupConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMq:Host"], "/", h =>
        {
            h.Username(builder.Configuration["RabbitMq:User"]!);
            h.Password(builder.Configuration["RabbitMq:Pass"]!);
        });
        cfg.UsePrometheusMetrics(serviceName: "geoservice");
        cfg.ConfigureEndpoints(ctx);
    });
});

app.UseHttpMetrics();
app.MapMetrics("/metrics");
```

Existing HTTP endpoint `GET /reverse` is kept unchanged (the API still calls it for semantic-search-time display).

---

### Modify `src/AiSidecar/`

**Replace `main.py` with `worker.py`** — keep all model loading code but replace FastAPI with pika consumer loop. Extract the inference logic into a module-level function to keep it testable.

Key structure:
```python
# worker.py
import pika, json, os, threading
from http.server import HTTPServer, BaseHTTPRequestHandler
from prometheus_client import Counter, Histogram, start_http_server
# ... import model loading from existing main.py ...

RABBITMQ_URL = os.environ.get("RABBITMQ_URL", "amqp://guest:guest@rabbitmq/")
messages_processed = Counter("ai_messages_processed_total", "AI messages processed")
processing_seconds = Histogram("ai_processing_seconds", "AI inference duration in seconds")

def on_message(ch, method, props, body):
    req = json.loads(body)
    with processing_seconds.time():
        result = _run_inference_from_path(req["filePath"])  # extracted from existing _run_inference()
    ch.basic_publish(
        exchange="",
        routing_key="ai-analysis-completed",
        body=json.dumps({
            "correlationId": req["correlationId"],
            "tags": [{"tag": t.tag, "confidence": t.confidence, "model": t.model} for t in result.tags],
            "embedding": result.embedding,
            "originClass": result.originClass,
            "originConfidence": result.originConfidence,
        }))
    ch.basic_ack(delivery_tag=method.delivery_tag)
    messages_processed.inc()

class HealthHandler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200); self.end_headers()
        self.wfile.write(b'{"status":"ok"}')
    def log_message(self, *_): pass

if __name__ == "__main__":
    # Load models (same code as existing lifespan startup)
    _load_models()

    start_http_server(8001)  # Prometheus /metrics
    threading.Thread(target=lambda: HTTPServer(("", 8000), HealthHandler).serve_forever(), daemon=True).start()

    conn = pika.BlockingConnection(pika.URLParameters(RABBITMQ_URL))
    ch = conn.channel()
    ch.queue_declare(queue="ai-analysis-requested", durable=True)
    ch.queue_declare(queue="ai-analysis-completed", durable=True)
    ch.basic_qos(prefetch_count=1)  # one at a time — GPU/CPU bound
    ch.basic_consume(queue="ai-analysis-requested", on_message_callback=on_message)
    ch.start_consuming()
```

**Keep `POST /embed/text` HTTP endpoint** — the API still calls it for semantic search. Add it as a second `BaseHTTPRequestHandler` route on port 8000 alongside `/health`, or run it as a minimal Flask/FastAPI app on a second thread. The simplest approach: keep a tiny FastAPI app with only `/embed/text` and `/health`, run it in a thread; the main thread runs the pika consumer loop.

**Update `requirements.txt`:**
- Add: `pika==1.3.x`, `prometheus-client==0.x`
- Remove: `fastapi`, `uvicorn`
- Keep: `torch`, `torchvision`, `ultralytics`, `open-clip-torch`, `Pillow`, `numpy`

**Update `Dockerfile`:**
- Change final CMD from `uvicorn main:app ...` to `python worker.py`
- Keep model pre-download layers unchanged

---

### Update `docker-compose.yml`

**Remove** the `n8n` service block entirely.

**Update `nginx` depends_on** — remove `n8n` from the list.

**Add:**
```yaml
rabbitmq:
  image: rabbitmq:3-management-alpine
  hostname: rabbitmq
  restart: unless-stopped
  environment:
    RABBITMQ_DEFAULT_USER: ${RABBITMQ_USER:-guest}
    RABBITMQ_DEFAULT_PASS: ${RABBITMQ_PASS:-guest}
  ports:
    - "127.0.0.1:5672:5672"
    - "127.0.0.1:15672:15672"
    - "127.0.0.1:15692:15692"
  volumes:
    - ${DATA_DIR}/rabbitmq:/var/lib/rabbitmq
    - ./infra/rabbitmq/enabled_plugins:/etc/rabbitmq/enabled_plugins:ro
  networks: [homemediahub]
  healthcheck:
    test: ["CMD", "rabbitmq-diagnostics", "ping"]
    interval: 10s
    timeout: 5s
    retries: 5

media-worker:
  build:
    context: .
    dockerfile: src/MediaWorker/Dockerfile
  restart: unless-stopped
  environment:
    ConnectionStrings__DefaultConnection: "Host=postgres;Port=5432;Database=homemediahub_db;Username=${POSTGRES_USER};Password=${POSTGRES_PASSWORD};Keepalive=30"
    RabbitMq__Host: rabbitmq
    RabbitMq__User: ${RABBITMQ_USER:-guest}
    RabbitMq__Pass: ${RABBITMQ_PASS:-guest}
  volumes:
    - ${PHOTO_DIR}:/photo:ro
    - ${THUMBNAILS_DIR}:/thumbnails
    - ${WORK_DIR}:/shared-temp
  networks: [homemediahub]
  depends_on:
    postgres:   { condition: service_healthy }
    rabbitmq:   { condition: service_healthy }

prometheus:
  image: prom/prometheus:latest
  restart: unless-stopped
  volumes:
    - ./infra/prometheus/prometheus.yml:/etc/prometheus/prometheus.yml:ro
    - prometheus_data:/prometheus
  ports:
    - "127.0.0.1:9090:9090"
  networks: [homemediahub]

grafana:
  image: grafana/grafana:latest
  restart: unless-stopped
  environment:
    GF_SECURITY_ADMIN_PASSWORD: ${GRAFANA_PASSWORD:-admin}
    GF_USERS_ALLOW_SIGN_UP: "false"
  volumes:
    - ${DATA_DIR}/grafana:/var/lib/grafana
    - ./infra/grafana/provisioning:/etc/grafana/provisioning:ro
  ports:
    - "127.0.0.1:3001:3000"
  networks: [homemediahub]
```

**Update `ai-sidecar` service:**
- Add env var: `RABBITMQ_URL: amqp://${RABBITMQ_USER:-guest}:${RABBITMQ_PASS:-guest}@rabbitmq/`
- Add `depends_on: rabbitmq: { condition: service_healthy }`
- Health check stays on port 8000

**Update `geoservice` service:**
- Add env vars: `RabbitMq__Host: rabbitmq`, `RabbitMq__User: ${RABBITMQ_USER:-guest}`, `RabbitMq__Pass: ${RABBITMQ_PASS:-guest}`
- Add `depends_on: rabbitmq: { condition: service_healthy }`

**Update `api` service:**
- Add env vars: `RabbitMq__Host: rabbitmq`, `RabbitMq__User: ${RABBITMQ_USER:-guest}`, `RabbitMq__Pass: ${RABBITMQ_PASS:-guest}`
- Add `depends_on: rabbitmq: { condition: service_healthy }`

**Remove `volumes` entry for `n8n` data** from both the service and top-level volumes section.

---

### New `infra/` files

**`infra/rabbitmq/enabled_plugins`:**
```
[rabbitmq_management,rabbitmq_prometheus].
```

**`infra/prometheus/prometheus.yml`:**
```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: api
    static_configs: [{targets: ['api:8080']}]
    metrics_path: /metrics

  - job_name: media-worker
    static_configs: [{targets: ['media-worker:9090']}]
    metrics_path: /metrics

  - job_name: geoservice
    static_configs: [{targets: ['geoservice:8001']}]
    metrics_path: /metrics

  - job_name: ai-sidecar
    static_configs: [{targets: ['ai-sidecar:8001']}]
    metrics_path: /metrics

  - job_name: rabbitmq
    static_configs: [{targets: ['rabbitmq:15692']}]
    metrics_path: /metrics
```

**`infra/grafana/provisioning/datasources/prometheus.yml`:**
```yaml
apiVersion: 1
datasources:
  - name: Prometheus
    type: prometheus
    access: proxy
    url: http://prometheus:9090
    isDefault: true
```

**`infra/grafana/provisioning/dashboards/dashboards.yml`:**
```yaml
apiVersion: 1
providers:
  - name: default
    type: file
    options:
      path: /etc/grafana/provisioning/dashboards
```

Download these Grafana dashboard JSONs and commit to `infra/grafana/provisioning/dashboards/`:
- `masstransit.json` — Grafana dashboard ID **15467** (MassTransit .NET)
- `rabbitmq.json` — Grafana dashboard ID **10991** (RabbitMQ-Overview)
- `pipeline.json` — custom dashboard (create manually in Grafana, then export and commit):
  - Panel: Files indexed / hour (`rate(masstransit_consume_total{message_type=~".*MediaSaveRequested.*"}[1h]) * 3600`)
  - Panel: Queue depths (`rabbitmq_queue_messages_ready`)
  - Panel: AI inference time (`histogram_quantile(0.95, ai_processing_seconds_bucket)`)
  - Panel: Dead-letter message count (`rabbitmq_queue_messages_ready{queue=~".*skipped.*"}`)
  - Panel: Saga state counts (query `media_processing_sagas` table directly via Postgres data source, or approximate from consume totals)

---

### Update `.env.stage.example` and `.env.prod.example`

Add these variables:
```bash
RABBITMQ_USER=hmh
RABBITMQ_PASS=
GRAFANA_PASSWORD=
```

Remove `SERVER_IP` (was only used by n8n `WEBHOOK_URL`).

---

### Files to delete

- `Dockerfile.n8n`
- `infra/n8n/w1-process-single-file.json`
- `infra/n8n/w2-library-scanner.json`
- `infra/n8n/w4-error-handler.json`
- `infra/n8n/` directory

---

### Verification checklist

1. `docker compose --env-file .env.stage up -d` — all services start without errors
2. `http://192.168.0.192:15672` — RabbitMQ management UI; verify exchanges and queues are declared by MassTransit on startup
3. `curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"` → HTTP 202; verify `ScanRequested` message appears in RabbitMQ management UI
4. Watch `docker compose logs -f media-worker` — `FileDiscovered` messages consumed, `MediaReadyForEnrichment` published
5. Watch AI sidecar logs — `ai-analysis-requested` queue consumed
6. Watch GeoService logs — `geo-lookup-requested` queue consumed
7. `GET /api/media` — new records appear with tags, embeddings, address populated
8. `http://192.168.0.192:9090` Prometheus → Status → Targets → all 5 targets green (UP)
9. `http://192.168.0.192:3001` Grafana → MassTransit dashboard shows message rates, RabbitMQ dashboard shows queue depths
10. Corrupt a test file path → verify it lands in dead-letter queue in RabbitMQ UI and a `processing_jobs` row is written with `status = 'failed'`
11. Run all existing unit tests: `dotnet test` — should still pass (tests cover API query handlers, not the removed tools handlers)
