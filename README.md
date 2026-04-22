# HomeMediaHub

Self-hosted home photo and video library running on a single Dell Precision 3620 (Ubuntu Server 24.04). Replaces a manual iPhone → MacBook → USB drive workflow with automated ingestion, AI tagging, and semantic search.

## What it does

- **iPhone backup** via Immich — originals land directly on the USB HDD
- **Automated processing pipeline** via n8n: dedup → HEIC/video conversion → EXIF extraction → thumbnail generation → geocoding → AI tagging → semantic embeddings
- **Natural-language search** ("birthday cake", "beach sunset") powered by CLIP ViT-B/32 + pgvector
- **Object detection tags** via YOLOv8n (runs fully local — no cloud ML)
- **Browser UI** (React + TypeScript) for browsing, searching, albums, and favorites
- **Photo of the Day** — daily Telegram notification with a notable photo
- **Samba share** for direct file access from Windows/macOS on the LAN

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700, 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | USB HDD 500 GB — mounted at `/mnt/usb-hdd` |
| Network | LAN-only |

## Stack

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (iPhone backup only) |
| Orchestration | n8n |
| API | .NET 10 ASP.NET Core |
| Frontend | React + TypeScript |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n + CLIP ViT-B/32 |
| Media processing | ffmpeg, libheif + libde265 (HEIC/HEVC), ExifTool, SixLabors.ImageSharp |
| Reverse proxy | NGINX |
| Notifications | Telegram Bot API |
| File sharing | Samba (native Ubuntu) |
| Containerization | Docker Compose |

## Services

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 | Single public ingress |
| api (.NET) | 8080 | Internal only |
| frontend | 3000 | Internal only |
| n8n | 5678 | Admin only — nginx IP filter |
| ai-sidecar | 8000 | Internal only |
| postgres | 5432 | `homemediahub_db` + `n8n_db` |
| immich-server | 2283 | iPhone app endpoint |
| immich-postgres | 5433 | Internal only |
| redis | 6379 | Immich only |

## Storage Layout

```
/mnt/usb-hdd/
├── Photo/               ← single source of truth (Samba share root)
│   ├── 2005/ ... 2025/  ← existing archive
│   └── iPhone/          ← new uploads from Immich
├── thumbnails/          ← 400 px WebP (recreatable)
└── video-cache/         ← H.264 MP4 transcodes of HEVC videos (recreatable)
```

## n8n Workflows

| # | Name | Trigger | Purpose |
|---|------|---------|---------|
| 1 | Process Single File | Webhook / sub-workflow | Core pipeline: hash → dedup → convert → metadata → thumbnail → geocode → AI → DB |
| 2 | Library Scanner | Manual or monthly cron | Scans `/mnt/usb-hdd/Photo`, calls W1 for each unprocessed file |
| 3 | Photo of the Day | Daily cron 15:00 | Picks a notable photo and posts to Telegram |
| 4 | Error Handler | Global n8n error trigger | Updates `processing_jobs`, sends Telegram alert |

## Quick Start

```bash
# Start all services
docker compose up -d

# Rebuild after code changes
docker compose build api
docker compose build ai-sidecar

# View logs
docker compose logs -f api
docker compose logs -f n8n
docker compose logs -f ai-sidecar

# Connect to the database
docker compose exec postgres psql -U postgres -d homemediahub_db
```

## Ingest Strategy

Immich writes iPhone backups directly to `/mnt/usb-hdd/Photo/iPhone/`. The Library Scanner (Workflow 2) is the only ingest entry point — run it manually after a large import, or let the monthly cron catch it automatically. No Immich webhooks are used (fire-and-forget webhooks can silently drop events if n8n is unavailable).

## AI Pipeline

Both models run fully locally — no cloud APIs.

- **YOLOv8n** — object detection (80 COCO classes, ~50 ms/image on CPU, 6 MB model)
- **CLIP ViT-B/32** — semantic embeddings for natural-language search (151 MB model)

Models are baked into the `ai-sidecar` Docker image at build time.

## Authentication

ASP.NET Core Identity with two users (Mike + wife). No roles — both share the full library with equal access. JWT (15-minute expiry) + refresh token (30-day sliding window).

## Samba Share

`\\<server-ip>\Photo` → `/mnt/usb-hdd/Photo`. Read-write for the `mediahub` group.

- **Windows:** `\\<server-ip>\Photo`
- **macOS:** Finder → Go → Connect to Server → `smb://<server-ip>/Photo`

## Documentation

- [`docs/architecture.md`](docs/architecture.md) — detailed system diagram, service breakdown, DB schema, API surface, workflow pseudocode, volume map, and observability guide

## Design Decisions

- **n8n is the orchestrator.** The .NET API has no background workers or multi-step logic — n8n owns all workflow execution.
- **Tool endpoints pattern.** n8n calls small atomic .NET endpoints for operations it can't do natively: SHA-256 hashing, EXIF extraction, thumbnail generation, directory scanning.
- **All AI runs locally.** No cloud ML APIs. Face recognition is explicitly out of scope.
- **Thumbnails served by nginx.** The API returns `X-Accel-Redirect`; nginx serves the WebP directly from disk — no .NET process involved per request.
- **Separate Postgres instances.** Immich gets its own Postgres (5433). The custom stack runs a separate container (5432) with `homemediahub_db` and `n8n_db`.
- **Samba not containerized.** Running Samba in a container requires `--privileged` + `--network host`, eliminating isolation benefits.

## Out of Scope

- Face / people recognition
- Remote access (LAN only)
- Web upload (use the Samba share + run Library Scanner)
- RAW camera files (iOS devices only)
- Per-user library separation (shared pool, both users see everything)
