# Monitoring the pipeline

How to watch an import — a stage validation scan or the multi-day prod archive run — and how to
tell a working pipeline from a wedged one.

Everything here works for both environments; only the `--env-file` changes. Only one environment
runs at a time (shared ports), so the URLs are identical either way.

---

## The five tools, in the order you should reach for them

| Tool | Where | Answers |
|------|-------|---------|
| **Admin page** | `http://192.168.0.192/admin` | *Is it progressing?* |
| **RabbitMQ UI** | `http://192.168.0.192:15672` | *Which stage is the bottleneck?* |
| **Grafana** | `http://192.168.0.192:3001` | *What are the rates, and since when?* |
| **Logs** | `docker compose --env-file .env.stage logs -f media-worker` | *Why did this particular file fail?* |
| **psql** | `… exec -T postgres psql -U postgres -d homemediahub_db` | Ground truth when a UI disagrees with itself. |

The order matters. The Admin page tells you *whether* something is wrong, RabbitMQ *which stage*,
Grafana *since when*, the logs *why*. Jumping straight to logs during a 190K-file run means
reading a waterfall with no idea what you are looking for.

---

## 1. Admin page — the default

`http://192.168.0.192/admin`

- **Scan card** — triggers a scan (optional path), shows the returned `scanId`.
- **Pipeline Progress** — polls `/api/admin/pipeline/status` every 5 s. Per-status counts scoped
  to the *latest scan*, plus `completedLastHour` and a stale-job warning computed globally.
- **Failed Jobs** — paged table, per-row Retry, and Retry All Failed.

This is the only surface with authentication already handled. `/api/admin/*` requires a JWT, so
the curl equivalents mean lifting a bearer token out of browser devtools:

```bash
TOKEN=<paste from devtools>
curl -s http://192.168.0.192/api/admin/pipeline/status -H "Authorization: Bearer $TOKEN" | jq
```

**Job lifecycle:** `queued → processing → enriching → completed | failed | skipped_duplicate`

`enriching` means the media row is already written and the photo is browsable — AI and geo are
still outstanding. A large, slow-draining `enriching` count is normal and is the save-early design
working, not a stall.

---

## 2. RabbitMQ management UI — where the work is piling up

`http://192.168.0.192:15672` · credentials from `RABBITMQ_USER` / `RABBITMQ_PASS`

This is the tool that distinguishes "designed backpressure" from "broken". Each queue has an
explicit prefetch, so depth is expected — the question is *which* queue is deep.

| Queue | Prefetch / concurrency | Deep queue means |
|-------|------------------------|------------------|
| `media-scan` | 1 | Scan still enumerating the library. Brief. |
| `media-ingest` | 2 | **Disk-bound.** Hash/convert/thumbnail. One SATA spindle — more concurrency would just cause seek thrash. |
| `ai-analysis` | 1 (pika) | **Expected, and the normal steady state.** CPU-bound YOLO + CLIP. This is the progress bar. |
| `geo-lookup` | 1 | Nominatim's hard 1 req/s limit. Slow by contract. |
| `media-enrichment-saga` | 8 | Should stay near zero. Sustained depth means saga trouble. |
| `media-apply-enrichment` | 4 | Should stay near zero. |
| `media-job-status` | 4 | Should stay near zero. |
| `ai-analysis-completed` | 4 | Should stay near zero — this is the raw-JSON bridge. |

**Healthy during an import:** `ai-analysis` deep and draining steadily, everything else near zero.

**Investigate:** any `*_error` queue with messages; `media-enrichment-saga` growing; a queue with
**0 consumers** (that service is down or failed to connect).

---

## 3. Grafana — rates and trends

`http://192.168.0.192:3001` · dashboard **HomeMediaHub Pipeline**

Eight panels. The ones worth watching during an import:

- **Queue depth (messages ready)** — the same story as the RabbitMQ UI, but over time, so you can
  see whether it is draining or growing.
- **Jobs completed / hour** — extrapolate your finish time from this.
- **AI inference duration (P95)** — your real cost per photo. The prod projection of days rests on
  the 2–5 s/image estimate; this is where you find out whether that holds.
- **MassTransit consume faults** — flat is good. Any movement means files are failing.

Grafana matters over hours, not minutes. For a "what is happening right now" question, use the
Admin page or the RabbitMQ UI.

---

## 4. Logs

```bash
docker compose --env-file .env.stage logs -f media-worker    # the pipeline
docker compose --env-file .env.stage logs -f ai-sidecar      # YOLO/CLIP
docker compose --env-file .env.stage logs -f api             # queries, scan trigger, transcodes
docker compose --env-file .env.stage logs -f geoservice      # Nominatim
```

One grep worth keeping open — every video transcode reports its strategy and elapsed time:

```bash
docker compose --env-file .env.stage logs -f api | grep ffmpeg
```

`remux` means the file was already H.264/AAC and was stream-copied in seconds. `re-encode` means a
real libx264 pass costing minutes. If `re-encode` appears often, that is the signal that
background transcoding needs building — a full re-encode cannot complete inside one HTTP request,
so the first play always fails from the browser's side even though the result caches.

---

## 5. psql — ground truth

```bash
docker compose --env-file .env.stage exec -T postgres psql -U postgres -d homemediahub_db
```

```sql
-- overall progress
SELECT status, count(*) FROM processing_jobs GROUP BY status ORDER BY count DESC;

-- what actually failed, and why
SELECT file_path, error_message, updated_at
FROM processing_jobs WHERE status = 'failed'
ORDER BY updated_at DESC LIMIT 20;

-- enrichment coverage: rows saved vs rows enriched
SELECT (SELECT count(*) FROM media_files WHERE NOT is_deleted) AS files,
       (SELECT count(*) FROM media_embeddings)                 AS embedded,
       (SELECT count(*) FROM media_tags)                       AS tag_rows,
       (SELECT count(*) FROM enrichment_sagas)                 AS sagas_in_flight;

-- sagas that never finished (rows are deleted on completion, so any long-lived row is stuck)
SELECT correlation_id, current_state FROM enrichment_sagas;
```

`enrichment_sagas` is the one to check if the queues are empty but jobs are still `enriching`.
There is no scheduler timeout (see Open risks in `delivery-plan.md`), so a stuck saga stays stuck.

---

## Prometheus query cookbook

`http://localhost:9090` — **loopback only**, so query it over SSH, not from a laptop browser.

The metric names are not guessable, and getting them wrong is how six of eight dashboard panels
came to be silently empty (2026-08-08). Two rules explain most of it:

**The OTel Prometheus exporter appends each instrument's unit to the metric name.** MassTransit
counts in `ea` and times in **milliseconds**:

```
messaging_masstransit_consume_ea_total                      # NOT consume_total
messaging_masstransit_consume_duration_milliseconds_bucket  # NOT _seconds_bucket
messaging_masstransit_consume_fault_ea_total                # NOT consume_errors_total
messaging_masstransit_receive_ea_total
messaging_masstransit_saga_duration_milliseconds_bucket
```

Labels: `messaging_masstransit_destination` (the queue), `messaging_masstransit_consumer_type`,
`messaging_masstransit_message_type`, `messaging_masstransit_service`.

**Fault counters are registered lazily** — `consume_fault_ea_total` does not exist until something
actually faults. An empty faults panel on a healthy stack is correct, not a broken query.

**Per-queue RabbitMQ metrics come from a separate scrape job, under a different name.**
`rabbitmq_prometheus` defaults to `return_per_object_metrics = false`, so the plain `/metrics`
endpoint aggregates and carries no `queue` label at all. `prometheus.yml` therefore has a second
job, `rabbitmq-queues`, hitting `/metrics/detailed?family=queue_coarse_metrics` — and **that
endpoint prefixes its metric names with `rabbitmq_detailed_`**:

```
rabbitmq_detailed_queue_messages_ready   # per-queue, has the `queue` label — use this
rabbitmq_queue_messages_ready            # aggregated, NO queue label — looks right, matches nothing
```

Both names exist simultaneously, which is what makes this trap so easy to fall into twice.

```bash
# are all targets up?
curl -s localhost:9090/api/v1/targets \
  | jq '.data.activeTargets[] | {job: .labels.job, health, lastError}'

# does a metric exist at all? (the first question when a panel is empty)
curl -s localhost:9090/api/v1/label/__name__/values | jq -r '.data[] | select(test("masstransit"))'

# what labels does it carry?
curl -s 'localhost:9090/api/v1/series?match[]=messaging_masstransit_consume_ea_total' | jq '.data[0]'

# per-queue depth right now
curl -s 'localhost:9090/api/v1/query?query=rabbitmq_detailed_queue_messages_ready' \
  | jq -r '.data.result[] | "\(.metric.queue) \(.value[1])"'
```

**When a Grafana panel is empty, always check whether the metric name exists before assuming the
pipeline is idle.** That distinction cost a full debugging round on 2026-08-08.

---

## Common diagnoses

| Symptom | Likely cause | Check |
|---------|--------------|-------|
| Jobs stuck in `queued`, all queues empty | Consumer not connected | RabbitMQ UI → consumer count on that queue; `logs media-worker` |
| Jobs stuck in `enriching`, queues empty | Stuck saga (no timeout by design) | `SELECT * FROM enrichment_sagas` |
| `ai-analysis` deep and growing | Normal on a large import | AI P95 panel — days for a full library is expected |
| `media-ingest` deep | Disk-bound, prefetch 2 on one spindle | Expected; raising concurrency causes seek thrash |
| Everything idle mid-scan | Broker auth or connection failure | `logs api`/`media-worker` for `ACCESS_REFUSED` |
| Grafana panel empty | Metric name wrong, or lazily-registered counter | Cookbook above |
| Gallery has photos but no tags | Save-early working as designed | Not a fault — enrichment trails ingest |

---

## Related

- `docs/delivery-plan.md` — stage validation checklist, per-run findings, open risks
- `docs/architecture.md` — queue topology, saga design, `processing_jobs` lifecycle
- `docs/fresh-stage-deploy.md` · `docs/prod-first-run.md` — full deploy runbooks
- `CLAUDE.md` → Common Commands — cache clearing, one-off psql, env paths
