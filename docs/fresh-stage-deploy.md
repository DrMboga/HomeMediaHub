# Fresh Stage Deploy

Run this when you need a clean slate on stage — after schema changes, migration squashes, or AI model updates. Destroys all stage data and re-imports from TestPhoto.

## Prerequisites

- Code changes committed and pushed to the server (see step 1)
- SSH access: `mike@192.168.0.192`

---

## Steps

### 1. Push changes from your laptop

Commit and push any local changes so the server gets the latest code before rebuilding images.

### 2. SSH into the server

```bash
ssh mike@192.168.0.192
cd /opt/homemediahub/app
```

### 3. Stop the stack

```bash
docker compose --env-file .env.stage down
```

### 4. Wipe the postgres data volume

```bash
sudo rm -rf /opt/homemediahub/stage/data/postgres
```

Optionally wipe the other databases too (only needed if their schema changed):

```bash
sudo rm -rf /opt/homemediahub/stage/data/immich-postgres
sudo rm -rf /opt/homemediahub/stage/data/geoservice-postgres
```

Optionally wipe recreatable caches:

```bash
sudo rm -rf /opt/homemediahub/stage/thumbnails/*
sudo rm -rf /opt/homemediahub/stage/display-cache/*
```

### 5. Pull the latest code

```bash
git pull
```

### 6. Rebuild images

```bash
docker compose --env-file .env.stage build api ai-sidecar frontend geoservice
```

### 7. Start the stack

```bash
docker compose --env-file .env.stage up -d
```

What happens automatically on first start:

| Step | Who | What |
|------|-----|------|
| DB init | postgres container | `01-init.sh` creates `homemediahub_db` and `n8n_db` |
| Schema | postgres container | `02-schema.sql` creates all media tables |
| Migrations | API on startup | `MigrateAsync()` runs `CreateIdentitySchema` — creates Identity tables including refresh token columns |
| Users | API on startup | `DbSeeder` creates Mike and Badger accounts |

### 8. Run Library Scanner in n8n

Open [http://192.168.0.192:5678](http://192.168.0.192:5678), find **W2 Library Scanner**, click **Execute Workflow**.

All TestPhoto items will be ingested and classified with the current AI model.

---

## Verify

```bash
curl http://localhost:8080/health
curl http://localhost:8001/health
curl http://localhost:8000/health

docker compose --env-file .env.stage logs -f --tail=100 api
docker compose --env-file .env.stage logs -f --tail=100 geoservice
docker compose --env-file .env.stage logs -f --tail=100 ai-sidecar

docker compose --env-file .env.stage restart nginx
```

- Log in at [http://192.168.0.192](http://192.168.0.192) → confirms Identity schema and users are correct
- Gallery shows photos → confirms media import and thumbnails
- Review page shows artwork/screenshot/document/reference items → confirms 6-class AI model is active
- Gallery origin filter chips work → confirms `origin_class` column exists

---

## Notes

- **n8n workflows** are stored in the `n8n_db` volume. Wiping it loses workflow definitions — export them from the n8n UI first if needed, or just wipe `data/postgres` (which shares the host) and leave `data/n8n` alone. The `data/n8n` volume holds execution history; `data/postgres` holds the `n8n_db` database.
- **`.env` files** do not need to change — leave them as-is.
- **USB HDD / prod** — this procedure applies to stage only. Never run `rm -rf` on `/opt/homemediahub/data/` (prod) unless you are certain the full photo library is intact on the USB HDD.
