# Server Setup — Dell Precision 3620 (Ubuntu Server 24.04)

Run these steps once on a fresh OS over SSH.

---

```bash
sudo mkdir -p /opt/homemediahub/app
sudo chown -R mike:mike /opt/homemediahub
```

## 1. Copy project files to the server

Run this on your **Windows machine**:

```powershell
# Replace 192.168.0.192 with your server's LAN IP
scp -r C:\Users\Mike\Projects\HomeMediaHub mike@192.168.0.192:/opt/homemediahub/app
```

Or use `rsync` if available:
```bash
rsync -av /mnt/c/Users/Mike/Projects/HomeMediaHub/ mike@192.168.0.192:/opt/homemediahub/app/
```

```bash
  mv /opt/homemediahub/app/HomeMediaHub/* /opt/homemediahub/app/
  mv /opt/homemediahub/app/HomeMediaHub/.* /opt/homemediahub/app/ 2>/dev/null
  rmdir /opt/homemediahub/app/HomeMediaHub
```

---

## 2. Mount the USB HDD (if not already mounted)

SSH into the server, then:

```bash
lsblk -f                    # find device — look for the 500 GB drive, e.g. /dev/sdb1
sudo blkid /dev/sdb1        # note the UUID

# Create mount point
sudo mkdir -p /mnt/usb-hdd

# Add to /etc/fstab for auto-mount on boot
echo 'UUID=<paste-uuid-here>  /mnt/usb-hdd  ext4  defaults,nofail  0  2' | sudo tee -a /etc/fstab

sudo mount -a               # mount now without rebooting
df -h /mnt/usb-hdd          # confirm it shows ~500 GB
```

---

## 3. Run the setup script

```bash
cd /opt/homemediahub/app
bash infra/setup/server-setup.sh
```

This installs Docker, Samba, creates all required directories, and configures the Samba share.
You will be prompted to set a Samba password for your user.

**After the script finishes — log out and back in:**
```bash
exit
ssh mike@192.168.0.192
```
This activates the `docker` group so you can run Docker without `sudo`.

---

## 4. Configure environment variables

There are two independent environments. Start with **stage** — it uses the SSD only, no USB HDD required.

```bash
cd /opt/homemediahub/app

# Stage (start here)
cp .env.stage.example .env.stage
nano .env.stage   # fill in SERVER_IP, POSTGRES_PASSWORD, IMMICH_DB_PASSWORD

# Prod (set up later, when USB HDD is attached)
cp .env.prod.example .env.prod
nano .env.prod    # same fields
```

Fill in for each file:
- `SERVER_IP` — your server's LAN IP (e.g. `192.168.0.192`)
- `POSTGRES_PASSWORD` — any strong password
- `IMMICH_DB_PASSWORD` — any strong password

---

## 5. Copy TestPhoto to the server (stage only)

On your **Windows machine**, create a `TestPhoto` folder with a small sample:
- A few JPEGs from different years
- One or two HEIC files (from iPhone)
- One short video (.mov or .mp4)

Then copy it to the server:
```powershell
scp -r "C:\Users\Mike\Projects\TestPhoto" mike@192.168.0.192:/opt/homemediahub/stage
```

Also create the `iPhone` subfolder that Immich expects:
```bash
mkdir -p /opt/homemediahub/stage/TestPhoto/iPhone
```

---

## 6. Build images and start the stack (stage)

```bash
chmod -R 755 /opt/homemediahub/app/infra/postgres/init 

cd /opt/homemediahub/app

# Build custom images (n8n with ffmpeg, .NET API stub, Python AI sidecar stub)
docker compose --env-file .env.stage build

# Pull pre-built images and start everything
docker compose --env-file .env.stage up -d

# Watch startup logs
docker compose --env-file .env.stage logs -f
```

First run pulls ~1.5 GB of images (Immich is the largest). Subsequent starts are fast.

---

## 7. Verify Phase 1 is done (stage)

```bash
# All containers running?
docker compose --env-file .env.stage ps

# Schema applied correctly?
docker compose --env-file .env.stage exec postgres \
    psql -U postgres -d homemediahub_db -c "\dt"
# Expected: 10 tables listed (media_files, media_tags, albums, etc.)

# Health checks passing?
curl http://localhost:8080/health   # .NET API stub → {"status":"ok","phase":1}
curl http://localhost:8000/health   # AI sidecar stub → {"status":"ok","phase":1}
```

---

## 8. Switch to prod (when USB HDD is ready)

```bash
# Stop stage
docker compose --env-file .env.stage down

# Mount USB HDD (if not already in fstab — see section 2)
sudo mount -a
df -h /mnt/usb-hdd

# Start prod
docker compose --env-file .env.prod up -d
```

---

## Samba access (Windows / macOS)

| OS | Path |
|----|------|
| Windows | `\\<server-ip>\Photo` |
| macOS | Finder → Go → Connect to Server → `smb://<server-ip>/Photo` |

Use the Samba password you set in step 3.

---

## Useful Docker commands

```bash
# View logs for a specific service
docker compose logs -f api
docker compose logs -f n8n

# Restart a single service
docker compose restart api

# Stop everything
docker compose down

# Rebuild after code changes
docker compose build api
docker compose up -d api
```
