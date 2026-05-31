# Production First Run

Switch from stage (TestPhoto on SSD) to prod (full archive on USB HDD).

**Prerequisites:** stage environment validated, all code committed and pushed.

---

## 1. Attach and verify the USB HDD

```bash
ssh mike@192.168.0.192

# Check the drive is visible
lsblk
# Expect something like:
# sdb      8:16   0  500G  0 disk
# └─sdb1   8:17   0  500G  0 part

# Verify it is mounted
findmnt /mnt/usb-hdd
# If not mounted:
sudo mount /dev/sdb1 /mnt/usb-hdd

# Verify the photo library is accessible
ls /mnt/usb-hdd/Photo | head -20
```

### Make the mount permanent (if not already in fstab)

```bash
# Get the UUID of the partition
sudo blkid /dev/sdb1
# e.g. UUID="a1b2c3d4-..."

# Add to fstab
echo 'UUID=a1b2c3d4-... /mnt/usb-hdd ext4 defaults,nofail 0 2' | sudo tee -a /etc/fstab

# Test without rebooting
sudo mount -a
```

The `nofail` option prevents boot failures if the drive is accidentally disconnected.

---

## 2. Create required directories

The prod directories may not exist yet if only stage has been used:

```bash
sudo mkdir -p /mnt/usb-hdd/thumbnails
sudo mkdir -p /mnt/usb-hdd/video-cache
sudo mkdir -p /mnt/usb-hdd/display-cache
sudo mkdir -p /opt/homemediahub/data/postgres
sudo mkdir -p /opt/homemediahub/data/immich-postgres
sudo mkdir -p /opt/homemediahub/data/geoservice-postgres
sudo mkdir -p /opt/homemediahub/data/n8n
sudo mkdir -p /tmp/homemediahub-prod
```

---

## 3. Stop stage

```bash
cd /opt/homemediahub/app
docker compose --env-file .env.stage down
```

---

## 4. Pull latest code and build prod images

```bash
git pull
docker compose --env-file .env.prod build api ai-sidecar frontend geoservice
```

---

## 5. Start prod

```bash
docker compose --env-file .env.prod up -d
```

On first start:
- Postgres initialises `homemediahub_db` and `n8n_db` from `infra/postgres/init/`
- API runs `MigrateAsync` → creates Identity tables (single migration)
- `DbSeeder` creates Mike and Badger accounts

Verify:

```bash
curl http://localhost:8080/health
curl http://localhost:8001/health
curl http://localhost:8000/health

docker compose --env-file .env.prod logs -f --tail=50 api
```

---

## 6. Re-configure n8n workflows

n8n's workflow definitions and credentials are stored in `n8n_db`. Since prod uses a fresh database, you need to re-import workflows and re-enter credentials.

### Import workflows

```bash
# Export from stage first (if still accessible):
docker compose --env-file .env.stage exec n8n n8n export:workflow --all --output=/home/node/.n8n/workflows.json

# Import into prod:
docker compose --env-file .env.prod exec n8n n8n import:workflow --input=/home/node/.n8n/workflows.json
```

Or re-create them manually via the n8n UI at `http://192.168.0.192:5678`.

### Re-enter credentials in n8n UI

- Telegram bot token (for W3 and W4)
- Postgres connection (if not using the default internal hostname)

---

## 7. Run Library Scanner

This is the main event — importing the full photo archive.

Open n8n → **W2 Library Scanner** → **Execute Workflow**.

**Expected duration:** roughly 50–150 ms per file on the i7-6700 (AI sidecar is the bottleneck). For a 20-year archive of ~20 000 files, expect 30–60 minutes.

Monitor progress:

```bash
# Live log of files being processed
docker compose --env-file .env.prod logs -f --tail=20 api

# Count rows ingested so far
docker compose --env-file .env.prod exec postgres \
    psql -U postgres -d homemediahub_db \
    -c "SELECT COUNT(*) FROM media_files;"

# Any failures?
docker compose --env-file .env.prod exec postgres \
    psql -U postgres -d homemediahub_db \
    -c "SELECT COUNT(*), status FROM processing_jobs GROUP BY status;"
```

The scanner can be interrupted and resumed safely — it skips files already in `media_files` via the hash dedup check.

---

## 8. Verify

- Open `http://192.168.0.192` → log in → Gallery shows real photos
- Geo page shows pins on the map for geotagged photos
- Review page shows any AI-flagged items for review
- Search "birthday" or "beach" returns semantically relevant results

---

## Notes

- **File copy with mtime preservation**: If copying new files to the server, use `rsync -av --times` or `scp -p` to preserve modification times. Without `-p`/`--times`, mtime is set to the copy time, which becomes the `taken_at` date for files without EXIF.
- **Samba share**: After switching to prod, the Samba share (`\\192.168.0.192\Photo`) maps to the live archive. New files copied there will be picked up by the next Library Scanner run.
- **Switching back to stage**: `docker compose --env-file .env.prod down` then `docker compose --env-file .env.stage up -d`. The two environments share ports 80/8080/etc. — only one can run at a time.
