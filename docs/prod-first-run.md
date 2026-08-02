# Production First Run

Switch from stage (TestPhoto on SSD) to prod (full archive on SATA HDD).

**Prerequisites:** stage environment validated against the
[stage rollout checklist](delivery-plan.md#stage-rollout--verification-checklist), all code
committed and pushed.

---

## 1. Verify the SATA HDD is mounted

```bash
ssh mike@192.168.0.192

# Check the drive is visible
lsblk
# Expect something like:
# sdb      8:16   0  1.8T  0 disk
# └─sdb1   8:17   0  1.8T  0 part  /mnt/storage

# Verify it is mounted
findmnt /mnt/storage
# If not mounted:
sudo mount -a

# Verify the photo library is accessible
ls /mnt/storage/Photo | head -20
```

### fstab entry (already configured)

```
UUID=247b5598-b391-45cb-9634-68a08571a352  /mnt/storage  ext4  defaults,noatime,nofail  0  2
```

The `nofail` option prevents boot failures if the drive is unexpectedly unavailable.

---

## 2. Create required directories

The prod directories may not exist yet if only stage has been used:

```bash
sudo mkdir -p /mnt/storage/thumbnails
sudo mkdir -p /mnt/storage/video-cache
sudo mkdir -p /mnt/storage/display-cache
sudo mkdir -p /opt/homemediahub/data/postgres
sudo mkdir -p /opt/homemediahub/data/immich-postgres
sudo mkdir -p /opt/homemediahub/data/geoservice-postgres
sudo mkdir -p /opt/homemediahub/data/rabbitmq
sudo mkdir -p /opt/homemediahub/data/prometheus
sudo mkdir -p /opt/homemediahub/data/grafana
sudo mkdir -p /tmp/homemediahub-prod
```

Prometheus and Grafana run as non-root inside their images and cannot write a `root:root`
bind mount (`open /prometheus/queries.active: permission denied`, and Grafana fails the same
way on `/var/lib/grafana`). Hand those two directories over:

```bash
sudo chown -R 65534:65534 /opt/homemediahub/data/prometheus   # nobody
sudo chown -R 472:472     /opt/homemediahub/data/grafana      # grafana
```

The Postgres and RabbitMQ images chown their own data dirs at startup — only these two need it.

---

## 3. Stop stage

```bash
cd /opt/homemediahub/app
docker compose --env-file .env.stage down
```

---

## 4. Pull latest code and build prod images

Build context for the .NET services is `./src` (Phase 8).

```bash
git pull
docker compose --env-file .env.prod build api media-worker ai-sidecar geoservice frontend
```

---

## 5. Start prod

```bash
docker compose --env-file .env.prod up -d
```

On first start:
- Api's `MigrateAsync()` creates Identity **and** all media tables (EF owns the schema — no init
  SQL); `DbSeeder` creates Mike and Badger accounts
- MediaWorker's `MigrateAsync()` creates `enrichment_sagas` in its own history table, after
  waiting for Api to be healthy (`depends_on`)
- MassTransit/pika declare all RabbitMQ exchanges and queues on startup

Verify:

```bash
docker compose --env-file .env.prod ps
curl http://localhost:8080/health

docker compose --env-file .env.prod logs -f --tail=50 api
docker compose --env-file .env.prod logs -f --tail=50 media-worker
```

---

## 6. Trigger the full archive scan

This is the main event — importing the full photo archive. Log in at
`http://192.168.0.192`, open the **Admin** page, and click **Trigger Scan** — or from the CLI:

```bash
TOKEN=$(curl -s -X POST http://192.168.0.192/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"mike","password":"<password>"}' | jq -r .accessToken)

curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"
```

**Expected duration:** ingest (hash/convert/thumbnail/save, I/O-bound) finishes in hours. AI
enrichment is CPU-bound and takes **multi-day** for the full ~190K-file / 750 GB archive
(~2–5 s/image on the i7-6700) — this is accepted; the gallery is fully browsable throughout
(save-early), and queue depth is the progress bar.

Monitor progress:

```bash
# Admin page — live counts, progress bar, throughput (polls every 5s)
# RabbitMQ mgmt UI — queue depths: http://192.168.0.192:15672
# Grafana pipeline dashboard — rates, AI P95, error-queue depth: http://192.168.0.192:3001

# Count rows ingested so far
docker compose --env-file .env.prod exec postgres \
    psql -U postgres -d homemediahub_db \
    -c "SELECT COUNT(*) FROM media_files;"

# Job status breakdown
docker compose --env-file .env.prod exec postgres \
    psql -U postgres -d homemediahub_db \
    -c "SELECT status, COUNT(*) FROM processing_jobs GROUP BY status;"
```

The scan can be interrupted and resumed safely — `ScanService` excludes files already in
`media_files` or with an active job, so re-triggering a scan mid-flight does not create
duplicates.

---

## 7. Verify

- Open `http://192.168.0.192` → log in → Gallery shows real photos (appearing progressively as
  ingest completes, well before AI enrichment finishes)
- Geo page shows pins on the map for geotagged photos
- Review page shows any AI-flagged items for review
- Search "birthday" or "beach" returns semantically relevant results
- Admin page's Failed Jobs table is empty or explainable (corrupt files, moved-mid-scan, etc.);
  Retry works after fixing the underlying issue

---

## Post-scan cleanup

Once the archive finishes ingesting (before enrichment fully completes is fine — ivfflat recall
degrades gracefully):

```bash
# Recreate the ivfflat index now that media_embeddings has real data (it was created empty)
docker compose --env-file .env.prod exec postgres \
    psql -U postgres -d homemediahub_db \
    -c "REINDEX INDEX media_embeddings_embedding_idx;"
```

Then spot-check geo addresses and semantic search quality, and tune
`Search:MaxCosineDistance` against the full library if results feel too strict/loose.

---

## Notes

- **File copy with mtime preservation**: If copying new files to the server, use
  `rsync -av --times` or `scp -p` to preserve modification times. Without `-p`/`--times`, mtime is
  set to the copy time, which becomes the `taken_at` date for files without EXIF.
- **Samba share**: After switching to prod, the Samba share (`\\192.168.0.192\Storage`) maps to
  the whole drive. Navigate to the `Photo/` subfolder to access the live archive. New files copied
  there will be picked up by the next scan (manual trigger, or the monthly `ScanScheduler` once
  `Scan:Enabled` is turned on for prod).
- **Switching back to stage**: `docker compose --env-file .env.prod down` then
  `docker compose --env-file .env.stage up -d`. The two environments share ports 80/8080/etc. —
  only one can run at a time.
