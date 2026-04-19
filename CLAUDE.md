# HomeMediaHub

Self-hosted home photo and video library. Replaces manual iPhone → MacBook → USB drive workflow with automated ingestion, AI tagging, and semantic search. Runs on a single Dell Precision 3620 (Ubuntu Server 24.04) as a Docker Compose stack.

## Hardware

| Component | Spec |
|-----------|------|
| Host | Dell Precision 3620 — Intel i7-6700, 32 GB RAM, 500 GB SSD |
| OS | Ubuntu Server 24.04 |
| Storage | USB HDD 500 GB — mounted at `/mnt/usb-hdd` |
| Network | LAN-only (no public internet exposure) |

## Stack

| Layer | Technology |
|-------|-----------|
| Mobile ingestion | Immich (MIT) — iPhone backup only, no Immich UI used |
| Orchestration | n8n — all multi-step processing workflows |
| API | .NET 10 ASP.NET Core |
| Frontend | React + TypeScript |
| Database | PostgreSQL 16 + pgvector |
| AI | Python 3.11 — YOLOv8n (object detection) + CLIP ViT-B/32 (semantic embeddings) |
| Media processing | ffmpeg, libheif, SixLabors.ImageSharp |
| Reverse proxy | NGINX |
| Notifications | Telegram Bot API (called from n8n) |
| File sharing | Samba (native Ubuntu, not containerized) |
| Containerization | Docker Compose (single host) |

## Service Ports

| Service | Port | Notes |
|---------|------|-------|
| nginx | 80 (443 future) | Single public ingress |
| api | 8080 | Internal only |
| frontend | 3000 | Internal only |
| n8n | 5678 | Admin only — restricted by nginx IP filter |
| ai-sidecar | 8000 | Internal only |
| postgres | 5432 | Internal only — hosts both `homemediahub_db` and `n8n_db` |
| immich-server | 2283 | iPhone app connects here |
| immich-postgres | 5433 | Internal only |
| redis | 6379 | Internal only — Immich only |

## Databases

| Database | Host | Purpose |
|----------|------|---------|
| `homemediahub_db` | postgres:5432 | Application data — media, tags, embeddings, users |
| `n8n_db` | postgres:5432 | n8n internal — workflow definitions, execution history, credentials |
| Immich DB | immich-postgres:5433 | Immich internal — not accessed by custom services |

## Environments

Two isolated environments run from the same `docker-compose.yml` via separate env files. Only one runs at a time (they share ports 80, 8080, etc.).

| Environment | Command | Photo library | Data root |
|-------------|---------|---------------|-----------|
| **stage** | `docker compose --env-file .env.stage up -d` | `/opt/homemediahub/stage/TestPhoto` (SSD) | `/opt/homemediahub/stage/data/` |
| **prod** | `docker compose --env-file .env.prod up -d` | `/mnt/usb-hdd/Photo` (USB HDD) | `/opt/homemediahub/data/` |

Stage uses the `TestPhoto` folder (copied from Windows laptop to server SSD) so the USB HDD is not required during development. Switch to prod when the USB HDD is attached and the full library is ready.

## Key Paths on Disk

### Stage (SSD)
```
/opt/homemediahub/stage/
├── TestPhoto/            ← small curated test library (copied from Windows laptop)
│   └── iPhone/          ← Immich uploads in stage
├── thumbnails/           ← generated, recreatable
├── video-cache/          ← H.264 transcodes, recreatable
└── data/
    ├── postgres/         ← stage Postgres data volume
    ├── immich-postgres/  ← stage Immich Postgres data volume
    └── n8n/              ← stage n8n data volume

/tmp/homemediahub-stage/  ← shared temp (stage)
```

### Prod (USB HDD)
```
/mnt/usb-hdd/
├── Photo/               ← single source of truth for all originals
│   ├── 2005/ ... 2025/  ← existing archive (not reorganised)
│   └── iPhone/          ← new uploads from Immich (storage template: {y}/{y-MM-dd}/)
├── thumbnails/          ← 400 px WebP, recreatable from originals
└── video-cache/         ← H.264 MP4 transcodes of iPhone HEVC videos, recreatable

/opt/homemediahub/data/
├── postgres/            ← prod Postgres data volume
├── immich-postgres/     ← prod Immich Postgres data volume
└── n8n/                 ← prod n8n data volume

/tmp/homemediahub-prod/  ← shared temp (prod)
```

## Samba Share

`\\<server-ip>\Photo` maps to `/mnt/usb-hdd/Photo`. Read-write for `mediahub` group.
- Windows: `\\<server-ip>\Photo`
- macOS: Finder → Go → Connect to Server → `smb://<server-ip>/Photo`

## n8n Workflows

| # | Name | Trigger | Purpose |
|---|------|---------|---------|
| 1 | Process Single File | Webhook / sub-workflow call | Core pipeline: hash → dedup → convert → metadata → thumbnail → geocode → AI → DB |
| 2 | Library Scanner | Manual button OR monthly cron (`0 2 1 * *`) | Scans `/mnt/usb-hdd/Photo`, finds unprocessed files, calls W1 for each |
| 3 | Photo of the Day | Daily cron `0 15 * * *` | Picks a notable photo from DB, sends to Telegram channel |
| 4 | Error Handler | Global n8n error trigger | Updates `processing_jobs` table, sends Telegram alert to admin |

**Ingest strategy:** No Immich webhooks. Immich writes files to disk; Library Scanner (W2) is the only ingest entry point. Run manually after large imports; monthly cron catches iPhone backups automatically.

## Docker Compose Commands

Always pass `--env-file` to target the right environment. Omitting it defaults to `.env` which may not exist.

```bash
# Start stage (TestPhoto on SSD)
docker compose --env-file .env.stage up -d

# Start prod (USB HDD — ensure drive is mounted first)
docker compose --env-file .env.prod up -d

# View logs (same flag required)
docker compose --env-file .env.stage logs -f api
docker compose --env-file .env.stage logs -f n8n

# Restart a single service
docker compose --env-file .env.stage restart api

# Stop an environment
docker compose --env-file .env.stage down

# Rebuild custom images after code changes
docker compose --env-file .env.stage build api
docker compose --env-file .env.stage build ai-sidecar
docker compose --env-file .env.stage build n8n

# Run a one-off Postgres command (stage example)
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db
```

## Authentication

ASP.NET Core Identity. Two users: Mike and his wife. No roles — both users share the full library with equal access. JWT (15 min expiry) + refresh token (30-day sliding window).

## Design Decisions

- **n8n is the orchestrator.** The .NET API contains no background workers, no Quartz jobs, no multi-step logic. n8n owns all workflow execution.
- **Tool endpoints pattern.** n8n calls small atomic .NET endpoints for operations it cannot perform natively: SHA-256 hashing, EXIF extraction, thumbnail generation, directory scanning.
- **HEIC/video conversion in n8n.** `heif-convert` and `ffmpeg` run as Execute Command nodes in n8n (installed in the custom n8n image). Converted temp files go to `/tmp/homemediahub-work/` (shared with the api container).
- **Thumbnails on filesystem.** nginx serves thumbnails directly via `X-Accel-Redirect` — no Postgres or .NET involved per thumbnail request.
- **Video on-demand transcoding.** iPhone HEVC videos are transcoded to H.264 MP4 on first play request by the .NET API. Cached in `/mnt/usb-hdd/video-cache/`. Original is never modified.
- **Single storage tree.** All originals (archive + new iPhone uploads) live under `/mnt/usb-hdd/Photo/`. No duplication. Immich's storage template writes new uploads to `Photo/iPhone/{y}/{y-MM-dd}/`.
- **Separate Postgres instances.** Immich gets its own Postgres (port 5433). The custom stack uses a separate container (port 5432) with two databases: `homemediahub_db` and `n8n_db`.
- **Samba native, not containerised.** Running Samba in a container requires `--privileged` and `--network host`, eliminating isolation benefits.
- **All AI runs locally.** No cloud ML APIs. YOLOv8n for object detection, CLIP ViT-B/32 for semantic embeddings and natural-language search. Face recognition is explicitly out of scope.

## Out of Scope

- Face / people recognition
- Remote access (LAN only)
- Web browser upload (copy to Samba share + run Library Scanner)
- RAW camera files (CR2, ARW, NEF) — iOS devices only
- Per-user library separation — shared pool, both users see everything

## Prior Art

Previous implementation in `C:/Users/Mike/Projects/PhotoLibrary` — ran on Raspberry Pi 4 with .NET 10, React, Postgres, ML.NET. Key lessons incorporated here: SignalR for real-time progress, EXIF via MetadataExtractor, geocoding via Nominatim, HEIC conversion via libheif/ffmpeg. Key problems it had: no iPhone auto-sync, poor ML.NET tagging results, no semantic search.
