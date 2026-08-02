# Fresh Stage Deploy

Run this when you need a clean slate on stage — after schema changes, migration squashes, or AI
model updates. Destroys all stage data and re-imports from TestPhoto.

These steps were executed end to end on **2026-08-02** for the first Phase 8 stage rollout and
the scan completed with geo + AI enrichment applied. The three problems that run hit are fixed
in code or captured as steps below; see "First stage rollout" in
[`delivery-plan.md`](delivery-plan.md).

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

### 4. Wipe the postgres and rabbitmq data volumes

```bash
sudo rm -rf /opt/homemediahub/stage/data/postgres
sudo rm -rf /opt/homemediahub/stage/data/rabbitmq
```

Optionally wipe the other databases too (only needed if their schema changed):

```bash
sudo rm -rf /opt/homemediahub/stage/data/immich-postgres
sudo rm -rf /opt/homemediahub/stage/data/geoservice-postgres
```

Optionally wipe recreatable caches:

```bash
sudo rm -rf /opt/homemediahub/stage/thumbnails/*
sudo rm -rf /opt/homemediahub/stage/data/display-cache/*
```

Prometheus/Grafana data (`/opt/homemediahub/stage/data/prometheus`,
`/opt/homemediahub/stage/data/grafana`) can be left alone — dashboards and datasources are
re-provisioned from `infra/grafana/provisioning/` on every start.

Both containers run as non-root, though, so if those directories were created with `sudo mkdir`
they are `root:root` and both services will crash-loop on permission denied
(`open /prometheus/queries.active: permission denied`). Fix once:

```bash
sudo chown -R 65534:65534 /opt/homemediahub/stage/data/prometheus   # nobody
sudo chown -R 472:472     /opt/homemediahub/stage/data/grafana      # grafana
```

### 5. Pull the latest code

```bash
git pull
```

### 6. Rebuild images

Build context for the .NET services is `./src` (Phase 8) — no other invocation works.

```bash
docker compose --env-file .env.stage build api media-worker ai-sidecar geoservice frontend
```

### 7. Start the stack

```bash
docker compose --env-file .env.stage up -d
```

What happens automatically on first start:

| Step | Who | What |
|------|-----|------|
| Schema | API on startup | `MigrateAsync()` runs Api's EF migrations — creates Identity **and** all media tables (`AddMediaSchema`), including unique/perf indexes and the raw-SQL partial + ivfflat indexes. There is no init SQL anymore — EF owns the schema. |
| Saga schema | media-worker on startup | `MigrateAsync()` runs MediaWorker's `AddEnrichmentSaga` migration into its own history table (`__ef_migrations_history_worker`) — must run *after* the Api migration creates the media tables `media-worker` depends on, hence `depends_on: api: service_healthy` in compose. |
| Users | API on startup | `DbSeeder` creates Mike and Badger accounts |
| Queues | media-worker/geoservice/ai-sidecar on startup | MassTransit/pika declare all exchanges/queues on `rabbitmq` — nothing to configure manually |

### 8. Trigger a library scan

Log in at `http://192.168.0.192`, open the **Admin** page, and click **Trigger Scan** — or from
the CLI:

```bash
TOKEN=$(curl -s -X POST http://192.168.0.192/api/auth/login \
  -H "Content-Type: application/json" \
  -d '{"username":"mike","password":"<password>"}' | jq -r .accessToken)

curl -X POST http://192.168.0.192/api/admin/scan -H "Authorization: Bearer $TOKEN"
```

All TestPhoto items will be ingested and classified with the current AI model. Watch progress on
the Admin page (polls every 5 s) or in the RabbitMQ mgmt UI queue depths.

---

## Verify

```bash
docker compose --env-file .env.stage ps
# all services healthy, including rabbitmq, media-worker, prometheus, grafana

curl http://localhost:8080/health

docker compose --env-file .env.stage logs -f --tail=100 media-worker
docker compose --env-file .env.stage logs -f --tail=100 api
docker compose --env-file .env.stage logs -f --tail=100 geoservice
docker compose --env-file .env.stage logs -f --tail=100 ai-sidecar

docker compose --env-file .env.stage restart nginx
```

- Log in at [http://192.168.0.192](http://192.168.0.192) → confirms Identity schema and users are correct
- Gallery shows photos **while enrichment is still running** → confirms save-early is working (the
  save-early acceptance test from the delivery plan)
- Review page shows artwork/screenshot/document/reference items → confirms the AI model is active
- Gallery origin filter chips work → confirms `origin_class` column exists
- Admin page shows live pipeline counts and throughput
- RabbitMQ mgmt UI ([http://192.168.0.192:15672](http://192.168.0.192:15672), login from
  `RABBITMQ_USER`/`RABBITMQ_PASS`) shows the expected queues with draining depth
- Grafana ([http://192.168.0.192:3001](http://192.168.0.192:3001), `admin` /
  `GRAFANA_PASSWORD`) → **Pipeline** dashboard shows live queue depth, consume rate, and AI P95

---

## Notes

- **`.env.stage`** needs `RABBITMQ_USER`, `RABBITMQ_PASS`, `GRAFANA_PASSWORD` (Phase 8 additions);
  `SERVER_IP` is no longer used and can be removed if still present from an older copy.
- **USB HDD / prod** — this procedure applies to stage only. Never run `rm -rf` on
  `/opt/homemediahub/data/` (prod) unless you are certain the full photo library is intact on the
  SATA HDD.
- **Two migrators, one DB** — the Api and MediaWorker each own a separate EF migrations history
  table. Media tables change **only** via Api migrations; never add a media-table migration to
  MediaWorker.
