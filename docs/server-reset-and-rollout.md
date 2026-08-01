# Server Reset, OS Update & Clean Rollout (Ubuntu Server)

End-to-end runbook for wiping the server back to a clean slate, patching Ubuntu, rolling out the
current (Phase 8) **stage** build, running the first media import, and watching it work through
RabbitMQ / Prometheus / Grafana.

Everything runs on the server: `ssh mike@192.168.0.192`, app dir `/opt/homemediahub/app`.

---

## ⚠️ Read this first — what must never be deleted

| Path | What it is | Safe to delete? |
|------|-----------|-----------------|
| `/mnt/storage/Photo` | **The real photo library — 20 years, ~190K files, 750 GB, irreplaceable** | ❌ **NEVER** |
| `/mnt/storage/Photo/iPhone` | Immich's iPhone uploads (part of the library) | ❌ **NEVER** |
| `/opt/homemediahub/stage/TestPhoto` | Stage test photos (throwaway sample set) | ⚠️ only if you have it backed up |
| `/opt/homemediahub/data/*` (prod data root) | Databases, RabbitMQ, metrics — **recreatable** | ✅ yes (this is the DB wipe) |
| `/opt/homemediahub/stage/data/*` (stage data root) | Same, for stage | ✅ yes |
| `/mnt/storage/thumbnails`, `video-cache`, `display-cache` | Regenerated on demand | ✅ yes |

**Golden rule:** wiping databases means deleting *data roots* (`.../data/…`) and *cache* folders —
**never** the `Photo` folders. A wrong `rm -rf` on `/mnt/storage/Photo` is unrecoverable.

---

## Mental model — why your old database survived `prune`

This is the thing that bit you last time. In this stack **every database is a bind mount to a host
folder**, not a Docker volume:

```yaml
# docker-compose.yml
postgres:
  volumes:
    - ${DATA_DIR}/postgres:/var/lib/postgresql/data   # ← a host folder, not a named volume
```

There are **no named volumes anywhere** in this compose file. Consequences:

- `docker compose down -v` → removes named volumes → **there are none → does nothing to your data**
- `docker volume prune` → removes dangling Docker volumes → **doesn't touch bind mounts**
- `docker system prune -a --volumes` → the biggest hammer → **still doesn't touch bind mounts**

Postgres only runs its first-time init when its data directory is **empty**. Because the host
folder `/opt/homemediahub/data/postgres` survived every `prune`, Postgres saw an existing data dir
and kept the old database (including the old `n8n_db`). **The only way to truly reset a database is
`sudo rm -rf` on its host folder.** That is what Section 1c does.

Two more facts about this setup:

- **Two environments, one repo, separate data roots.** `stage` (`COMPOSE_PROJECT_NAME=hmh-stage`,
  data in `/opt/homemediahub/stage/data`) and `prod` (`hmh-prod`, data in `/opt/homemediahub/data`)
  share the same code and images but nothing persistent. You must clean **both** separately.
- **Always pass `--env-file`.** It selects which environment (and therefore which project name and
  data root) a command targets. A `down` without it won't find your containers.

---

## 1. Full cleanup — remove all previous versions and databases

### 1a. Confirm your paths before deleting anything

```bash
cd /opt/homemediahub/app
grep -E 'DATA_DIR|PHOTO_DIR' .env.stage .env.prod
```

Expected (defaults):

```
.env.stage:DATA_DIR=/opt/homemediahub/stage/data
.env.stage:PHOTO_DIR=/opt/homemediahub/stage/TestPhoto
.env.prod:DATA_DIR=/opt/homemediahub/data
.env.prod:PHOTO_DIR=/mnt/storage/Photo
```

Note the two `DATA_DIR` values — those are the folders you will wipe. Note the two `PHOTO_DIR`
values — those you will **not** touch.

### 1b. Stop both stacks and remove their containers, networks, and orphans

`--remove-orphans` is important: it deletes leftover containers from the **old** compose file
(e.g. the removed `n8n` service) that a plain `down` would otherwise leave running.

```bash
docker compose --env-file .env.prod  down --remove-orphans
docker compose --env-file .env.stage down --remove-orphans
```

### 1c. Delete the persistent data — the part `prune` cannot reach

This is the actual database wipe. Double-check each path echoes the data root, not the photo
library, before running:

```bash
# Prod data root (databases, rabbitmq, prometheus, grafana) — NOT the photos
sudo rm -rf /opt/homemediahub/data/postgres
sudo rm -rf /opt/homemediahub/data/immich-postgres
sudo rm -rf /opt/homemediahub/data/geoservice-postgres
sudo rm -rf /opt/homemediahub/data/rabbitmq
sudo rm -rf /opt/homemediahub/data/prometheus
sudo rm -rf /opt/homemediahub/data/grafana

# Stage data root
sudo rm -rf /opt/homemediahub/stage/data/postgres
sudo rm -rf /opt/homemediahub/stage/data/immich-postgres
sudo rm -rf /opt/homemediahub/stage/data/geoservice-postgres
sudo rm -rf /opt/homemediahub/stage/data/rabbitmq
sudo rm -rf /opt/homemediahub/stage/data/prometheus
sudo rm -rf /opt/homemediahub/stage/data/grafana

# Recreatable caches (safe — regenerated during ingest / on demand)
sudo rm -rf /mnt/storage/thumbnails/*            /mnt/storage/video-cache/*            /mnt/storage/display-cache/*
sudo rm -rf /opt/homemediahub/stage/thumbnails/* /opt/homemediahub/stage/video-cache/* /opt/homemediahub/stage/display-cache/*
sudo rm -rf /tmp/homemediahub-prod/* /tmp/homemediahub-stage/*
```

> Shortcut (only if you are 100% sure the `DATA_DIR` values are correct and are *not* a photo
> library): `sudo rm -rf /opt/homemediahub/data /opt/homemediahub/stage/data`. The per-folder form
> above is safer because it can never accidentally name a `Photo` dir.

Wiping `immich-postgres` only resets Immich's index — the uploaded photo **files** in
`PHOTO_DIR/iPhone` are untouched; Immich re-indexes them on next start.

### 1d. Remove old images and build cache (force a clean rebuild)

Everything is stopped, so this clears all old app images (old `api`, `frontend`, the deleted
`n8n` image, etc.), dangling layers, and the build cache. It re-pulls/rebuilds from scratch next
time.

```bash
docker system prune -af
```

> Remember: this frees Docker disk but, as explained above, **does not** delete your bind-mounted
> databases — Section 1c already did that. There are no named volumes here, so `--volumes` is
> unnecessary.

### 1e. Verify the slate is clean

```bash
docker ps -a                      # no homemediahub containers
docker images                     # no hmh-* / homemediahub images
ls -la /opt/homemediahub/data           # postgres/ etc. gone (dir may be empty or absent)
ls -la /opt/homemediahub/stage/data
ls /mnt/storage/Photo | head           # ✅ photos still here — sanity check you kept the library
```

---

## 2. Ubuntu Server updates

Ubuntu's equivalent of "Windows Update". Do this while the stack is down (Section 1). Safe to run
routinely; on a 5-month-old box expect a kernel update and therefore a reboot.

### 2a. Refresh the package index and upgrade everything

```bash
sudo apt update                 # refresh the catalog of available versions
sudo apt full-upgrade -y        # install all updates (incl. ones needing new deps); also covers Docker if installed from apt/Docker repo
sudo apt autoremove --purge -y  # remove old kernels / orphaned packages
sudo apt clean                  # drop cached .deb files to reclaim space
```

`full-upgrade` (a.k.a. `dist-upgrade`) is preferred over plain `upgrade` because it will pull in
new dependencies when a package needs them — normal for kernel and security updates. It stays on
**24.04 LTS**; it does **not** jump you to a new Ubuntu release.

### 2b. Update snaps (Ubuntu Server ships some, e.g. the `docker` snap on some installs)

```bash
sudo snap refresh
```

### 2c. Reboot if the kernel or core libraries were updated

```bash
# If this file exists, a reboot is required:
ls -l /var/run/reboot-required 2>/dev/null && cat /var/run/reboot-required.pkgs

sudo reboot
```

After reboot, containers with `restart: unless-stopped` would normally auto-start — but you have
them fully down/removed from Section 1, so nothing comes back up until you roll out in Section 3.

Reconnect and confirm versions:

```bash
ssh mike@192.168.0.192
uname -r            # new kernel
docker --version    # Docker Engine
docker compose version
```

> **Distro release upgrades** (e.g. 24.04 → 24.10 via `do-release-upgrade`) are a different,
> riskier operation and are **not** needed here — 24.04 is the current LTS. Stick to package
> updates above.

---

## 3. Roll out the stage version

### 3a. Pull the latest code

```bash
cd /opt/homemediahub/app
git pull
```

### 3b. Confirm the stage env file has the Phase 8 variables

The new stack needs `RABBITMQ_USER`, `RABBITMQ_PASS`, and `GRAFANA_PASSWORD`. `SERVER_IP` is no
longer used.

```bash
grep -E 'RABBITMQ_USER|RABBITMQ_PASS|GRAFANA_PASSWORD' .env.stage
```

If any are missing, add them (compare against `.env.stage.example`):

```bash
# Example — set real values:
#   RABBITMQ_USER=hmh
#   RABBITMQ_PASS=<pick a password>
#   GRAFANA_PASSWORD=<pick a password>
nano .env.stage
```

### 3c. Build the images (build context is `./src` for the .NET services)

```bash
docker compose --env-file .env.stage build api media-worker ai-sidecar geoservice frontend
```

First build is slow — the AI sidecar downloads model weights and the .NET images install libheif.
Subsequent builds are cached.

### 3d. Start the stack

```bash
docker compose --env-file .env.stage up -d
docker compose --env-file .env.stage ps
```

What happens automatically on first start (no manual SQL, no init scripts anymore):

| Order | Who | What |
|------|-----|------|
| 1 | `postgres`, `rabbitmq` | come up, pass healthchecks |
| 2 | `api` | `MigrateAsync()` creates **Identity + all media tables** (EF owns the schema); `DbSeeder` creates the Mike & Badger users |
| 3 | `media-worker` | waits for `api` healthy, then `MigrateAsync()` creates `enrichment_sagas` in its own history table |
| 4 | `media-worker` / `geoservice` / `ai-sidecar` | declare all RabbitMQ exchanges + queues automatically |

### 3e. Smoke-test

```bash
curl -f http://localhost:8080/health && echo OK
docker compose --env-file .env.stage logs --tail=50 api
docker compose --env-file .env.stage logs --tail=50 media-worker
```

Then open `http://192.168.0.192` in a browser and log in as Mike. The gallery will be empty until
the first import.

---

## 4. First media import

Ingest (hash → convert → metadata → thumbnail → **save**) is fast and I/O-bound; AI + geo
enrichment happens afterward as a background wave. **Photos appear in the gallery as soon as they're
saved — before AI finishes.** That is the intended "save-early" behavior.

### Option A — the Admin page (easiest)

1. Log in at `http://192.168.0.192` → **Admin** in the nav.
2. **Library Scan** card → leave the path blank (defaults to `/photo`, which is the stage
   `TestPhoto` mounted read-only) → **Trigger Scan**.
3. Watch the **Pipeline Progress** card: it polls every 5 s — counts move
   `queued → processing → enriching → completed`, with a progress bar and throughput/hour.

### Option B — curl

```bash
TOKEN=$(curl -s -X POST http://192.168.0.192/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"mike","password":"<MIKE_PASSWORD>"}' | jq -r .accessToken)

curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"
# → 202 Accepted { "scanId": "..." }

# Progress as JSON:
curl -s http://192.168.0.192/api/admin/pipeline/status -H "Authorization: Bearer $TOKEN" | jq
```

### Sanity checks during/after import

```bash
# Rows saved so far
docker compose --env-file .env.stage exec postgres \
  psql -U postgres -d homemediahub_db -c "SELECT COUNT(*) FROM media_files;"

# Job status breakdown
docker compose --env-file .env.stage exec postgres \
  psql -U postgres -d homemediahub_db -c "SELECT status, COUNT(*) FROM processing_jobs GROUP BY status;"
```

Expected end state on the small stage set: most jobs `completed`, a few maybe `skipped_duplicate`,
ideally zero `failed`. Any failures show on the Admin page's **Failed Jobs** table with a Retry
button. Search ("beach", "cake") and the Geo map should work once enrichment catches up.

---

## 5. Monitoring — RabbitMQ, Prometheus, Grafana

Three tools, three jobs:

- **RabbitMQ Management UI** — the live message broker: which queues exist, how many messages are
  waiting, how fast they drain. Your real-time "is the pipeline moving?" view.
- **Prometheus** — a time-series database that scrapes numeric metrics from every service every
  15 s. You rarely look at it directly; it's the data source Grafana reads.
- **Grafana** — dashboards built on Prometheus data: queue depth over time, throughput, AI
  latency, error rates.

### Access & credentials (important networking note)

Some ports are LAN-open, some are loopback-only on the server:

| Tool | URL | Reachable from your laptop? | Login |
|------|-----|------------------------------|-------|
| RabbitMQ UI | `http://192.168.0.192:15672` | ✅ directly (LAN) | `RABBITMQ_USER` / `RABBITMQ_PASS` |
| Grafana | `http://192.168.0.192:3001` | ✅ directly (LAN) | `admin` / `GRAFANA_PASSWORD` |
| Prometheus | `http://localhost:9090` | ❌ loopback-only — needs an SSH tunnel | none |

To reach Prometheus from your laptop, open an SSH tunnel and then browse to `localhost:9090`:

```bash
ssh -L 9090:localhost:9090 mike@192.168.0.192
# leave that session open, then open http://localhost:9090 in your browser
```

### 5a. RabbitMQ Management UI — `:15672`

Log in, then:

- **Overview** tab — top graphs show total message rates (publish / deliver / ack) and total
  ready/unacked counts across the broker. During an import you'll see a spike as `ScanConsumer`
  fans out one message per file, then a steady drain.
- **Queues and Streams** tab — the important one. You'll see the pipeline's queues:

  | Queue | Stage of the pipeline |
  |-------|----------------------|
  | `media-scan` | scan requests being expanded into per-file messages |
  | `media-ingest` | files being hashed/converted/thumbnailed/saved (prefetch 2) |
  | `media-enrichment-saga` | saga state coordinating AI + geo |
  | `ai-analysis` | images waiting for the AI sidecar (prefetch 1 — expect a **long** backlog here; that's normal) |
  | `ai-analysis-completed` | raw AI results waiting to be bridged back |
  | `geo-lookup` | reverse-geocoding (1 msg/s — Nominatim limit) |
  | `media-apply-enrichment` | writing tags/embedding/address back to the DB |
  | `media-job-status` | job lifecycle bookkeeping |
  | `*_error` | **dead-letter queues — should stay empty.** Anything here is a real failure to investigate |

  Columns to read: **Ready** (waiting to be processed — this is your backlog/"progress bar";
  it should trend down), **Unacked** (currently being worked), and the **incoming/deliver rates**.
  Click a queue name to see its detail, rates graph, and even peek at a message.

- The healthy picture during a big import: `ai-analysis` **Ready** stays high for a long time and
  drains slowly (CPU-bound AI — days on the full prod library), while `media-ingest` drains fast.
  All `*_error` queues stay at 0.

### 5b. Prometheus — `:9090` (via the tunnel)

Two things worth doing here:

- **Status → Targets** — lists the five scrape targets (`api`, `media-worker`, `geoservice`,
  `ai-sidecar`, `rabbitmq`). All should be **UP**. If one is **DOWN**, that service isn't exposing
  metrics or isn't reachable — check its logs.
- **Graph** tab — type a metric name and hit Execute to chart it (this query language is PromQL).
  A few to try:

  ```promql
  # Messages waiting in each pipeline queue right now
  rabbitmq_queue_messages_ready

  # AI inference time, 95th percentile, over the last 5 min (seconds/image)
  histogram_quantile(0.95, sum(rate(ai_inference_seconds_bucket[5m])) by (le))

  # Rate of AI results by outcome (ok vs error) per second
  sum(rate(ai_messages_total[5m])) by (status)

  # Apply-enrichment consume rate = effectively "photos fully finished per second"
  sum(rate(messaging_masstransit_consume_total{messaging_masstransit_destination="media-apply-enrichment"}[5m]))
  ```

  You don't need to master PromQL — Grafana already wraps these into panels. This tab is just for
  ad-hoc poking and confirming a metric exists.

### 5c. Grafana — `:3001`

1. Log in (`admin` / `GRAFANA_PASSWORD`). The Prometheus data source and the pipeline dashboard are
   **auto-provisioned** — no setup needed.
2. Left menu → **Dashboards** → **HomeMediaHub Pipeline**.
3. Top-right controls: the **time range** (default last 6 h — widen to "last 24 h" for a long
   import) and **auto-refresh** (set to 30 s or 1 m to watch it live).

The eight panels and how to read them:

| Panel | Reading it |
|-------|-----------|
| **Queue depth (messages ready)** | One line per pipeline queue. The `ai-analysis` line dominates during import and should slope **down** over time. A line that only ever rises = a stuck/too-slow consumer. |
| **Error queue depth** | Should be **flat at 0**. Any non-zero line = messages dead-lettered; go look at that `*_error` queue in RabbitMQ and the owning service's logs. |
| **MassTransit consume rate by endpoint** | Throughput per consumer (msgs/s). Shows which stage is the bottleneck (it'll be `ai-analysis`). |
| **MassTransit consume faults** | Consumer exceptions/s. Should be ~0. Spikes point at a buggy/unhappy consumer. |
| **MassTransit consume duration (P95)** | How long each stage takes. `media-ingest` = disk/convert time; `media-apply-enrichment` = DB write time. |
| **AI inference duration (P95)** | Seconds per image for YOLO+CLIP. Expect ~2–5 s on this CPU. This number × queue depth ≈ time remaining. |
| **Jobs completed / hour** | Rolling count of fully-enriched photos per hour — your real throughput. |
| **AI messages by status** | `ok` vs `error` rate. A rising `error` line means images are failing analysis (corrupt files, etc.) — cross-check the Admin Failed Jobs table. |

> **First-run caveat:** the MassTransit panels use metric names
> (`messaging_masstransit_consume_*`) whose exact histogram suffix hasn't yet been confirmed
> against a live scrape. If a MassTransit panel reads "No data" while the queue is clearly moving,
> the metric name needs a small tweak — check the real name in Prometheus **Status → Targets →**
> (media-worker endpoint) or by typing `messaging_` in the Graph tab's autocomplete, then fix the
> panel's query. The RabbitMQ and AI panels don't depend on this and should populate immediately.

### What "healthy" looks like end-to-end

- RabbitMQ: `media-ingest` drains quickly; `ai-analysis` has a big backlog that shrinks steadily;
  every `*_error` queue is 0.
- Grafana: error/fault panels flat at 0; "Jobs completed/hour" is a positive, roughly steady line;
  AI P95 in the 2–5 s range.
- Admin page: `completed` climbing, `failed` at or near 0, gallery filling in.

---

## Appendix — quick reference

```bash
# Bring stage up / down
docker compose --env-file .env.stage up -d
docker compose --env-file .env.stage down --remove-orphans

# Follow logs
docker compose --env-file .env.stage logs -f media-worker      # or api, ai-sidecar, geoservice

# Rebuild one service after a code change
docker compose --env-file .env.stage build media-worker && \
  docker compose --env-file .env.stage up -d media-worker

# psql into the app DB
docker compose --env-file .env.stage exec postgres psql -U postgres -d homemediahub_db

# Re-trigger a scan (Admin page is easier)
curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"
```

**Switching stage ↔ prod:** they share ports, so only one runs at a time. Down one before upping
the other, and always pass the matching `--env-file`. For the prod first run specifically, see
[`prod-first-run.md`](prod-first-run.md) (includes the post-import ivfflat `REINDEX`).
