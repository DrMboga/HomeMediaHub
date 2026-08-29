# Backlog

Deferred work items. Each entry records what was investigated, what the cause actually is (read
off code or measured, not guessed), and what would need to change — so the work can be picked up
cold without re-deriving anything.

Items that graduate into planned work move out of here and into `docs/architecture.md` (design)
plus `docs/delivery-plan.md` (steps), leaving a pointer behind.

Ordered newest first.

---

## nginx caches upstream IPs, so a partial deploy 502s the whole site — 2026-08-29

**Caused a real prod outage the day it was written.** `api`, `media-worker` and `frontend` were
rebuilt and recreated; nginx was not. Every page returned 502 while all fourteen containers were up
and `api` reported healthy.

`infra/nginx/nginx.conf:13-14` declares static upstreams:

```nginx
upstream frontend  { server frontend:80; }
upstream api       { server api:8080; }
```

nginx resolves those **once, at config load**. The `resolver 127.0.0.11 valid=10s` on line 11 only
takes effect for a `proxy_pass` that goes through a variable — which is exactly what the `/immich/`
location already does, and the reason that one route kept working.

The failure is nastier than a plain stale address: Docker had reassigned the frontend's old
`172.18.0.5` to the **api** container, so nginx was proxying the homepage to a service that does not
listen on port 80. The error log said `connect() failed (111: Connection refused) ... upstream:
"http://172.18.0.5:80/"` — an IP that pointed at a perfectly healthy container.

**Workaround in place:** `docker compose --env-file .env.prod restart nginx` after any partial
deploy, recorded in CLAUDE.md's operational notes. A bare `up -d` (no service list) is also safe,
which is why the committed deploy docs have never hit this.

**The fix** is to give the two upstreams the same treatment `/immich/` already has, so DNS is
resolved per request:

```nginx
location /api/ { set $api_upstream http://api:8080; proxy_pass $api_upstream; }
location /     { set $fe_upstream  http://frontend:80; proxy_pass $fe_upstream; }
```

Note the missing trailing slash: with no URI component, nginx passes the original request URI
unchanged, matching today's behaviour. `/immich/` uses `$immich_upstream/` *with* a slash precisely
because it needs the prefix stripped — do not copy that form here. Losing the `upstream` blocks
costs keepalive and load balancing, neither of which is used with one replica each.

Wants doing with a click-through of every route afterwards (`/`, `/api/*`, `/thumbs/` internal
redirect, `/immich/`), so it is a deliberate change rather than an opportunistic one.

---

## Deleting a photo in Review resurrects its duplicate — FIXED 2026-08-29

**Measured, not theorised: 167 photos came back during the Phase 14 Step 4 scan.**

`BulkDeleteMediaHandler` (`src/Api/Features/Media/BulkDeleteMedia/BulkDeleteMediaHandler.cs`)
deletes the file from disk and then `ExecuteDeleteAsync`s the `media_files` row. That is correct
for the file it was given. But the library holds **992 known duplicate paths** — files whose hash
matches an indexed photo, which every scan re-discovers and re-marks `skipped_duplicate`
(see the entry below).

Dedup is hash-against-`media_files`. Delete the indexed copy and its twin no longer matches
anything, so the next scan ingests it as a brand-new photo. The photo is back in the gallery under
a different path, with fresh tags and a fresh embedding.

Verified on the 2026-08-29 scan: of 515 completed jobs, **167 were archive files that each had an
earlier `skipped_duplicate` job for the same path** — all 167, not a subset.

**The mismatch is in what "delete" means.** Review deletes *a file*; the person clicking it means
*this photo*. Nothing in the pipeline is malfunctioning.

**Mike chose option 2 the same day and it is built** — `deleted_hashes`, a table `DedupService`
consults before ingest, written by `BulkDeleteMediaHandler`. See `docs/delivery-plan.md` → Phase 14
→ "Step 4b". The 167 that already came back stop coming back once they are re-deleted through
Review with that code live. The four options that were weighed:

1. **Delete every copy.** On delete, find all files sharing the hash and remove them too. Needs the
   hash of the deleted row (available) and a path list, which we do not keep — `processing_jobs`
   rows for `skipped_duplicate` do record the path, so it is recoverable.
2. **Remember the deletion.** A `deleted_hashes` table consulted by `DedupService`, so a hash Mike
   deleted never ingests again from any path. Cheapest to implement, and it also covers copies
   added to the library later.
3. **Soft-delete instead.** `SoftDeleteMediaHandler` already exists and only sets `is_deleted`;
   the row would remain and keep shadowing the duplicate. Changes what Review means, and leaves
   the file on disk.
4. **Accept it**, and clean up in Review when it recurs.

Option 2 won because it matches the user's intent without touching disk twice, and because it is
the only one that also covers a copy added to the library *later*.

**Still outstanding:** the fix is on `main` but **not deployed** — it needs the `AddDeletedHashes`
migration plus a rebuild of api, media-worker and frontend. Until then, every scan keeps promoting
whatever has been deleted since.

Related: the 992-duplicate rediscovery below is what makes this reachable at all.

---

## Immich ingestion — graduated to Phase 14 on 2026-08-29

**Moved out of the backlog.** Steps live in `docs/delivery-plan.md` → "Phase 14 — Immich ingestion:
make the library scan safe", written against the real tree after Mike's first iPhone backup (350
photos) landed on prod.

This entry (written 2026-08-17 from the code, before Immich had any assets) was **right that the
scan would ingest Immich's derivatives** — measured at 721 files discovered, 371 of them junk: 350
`_preview.jpeg` at 1440×1920 plus 21 H.264 transcodes, none of which hash dedup can catch.

It was **wrong about the layout**. It assumed originals land under `library/`, so it proposed
excluding `upload/` along with the rest. In fact Immich's storage template ships `enabled: false`,
`library/` is empty, and **the originals are in `upload/`** under GUID names. Excluding by that
list as written would have skipped the entire camera roll.

Two problems it did not anticipate, both in the phase:

- Immich streams uploads **straight to their final path** with no `.tmp` staging, so a scan
  overlapping a phone backup hashes a half-written file — and never revisits it, because the path
  is then in `media_files`. The same hole has always existed for Samba copies.
- Immich's 30-day trash deletes files from disk, and the scan is add-only — no reconciliation pass
  exists anywhere in the system.

The nginx `/immich/` `client_max_body_size` note from this entry is **still open** — see below.

---

## nginx `/immich/` has no `client_max_body_size` — 2026-08-17

The `/immich/` location in `infra/nginx/nginx.conf` proxies to `immich-server` but sets no
`client_max_body_size`, so nginx's 1 MB default rejects every photo upload with a 413. The phone
should use `:2283` directly, which compose publishes on all interfaces —
`docs/immich-iphone-setup.md` §1 says so.

Today the location is a trap rather than a feature: it looks like a supported path and is not one.
Either give it a body-size limit large enough for a video (Immich's own default is unlimited) or
delete the location. Unrelated to scanning, so deliberately left out of Phase 14.

---

## `MediaListItemDto.ThumbnailPath` is dead weight on every list row — 2026-08-17

`MediaListItemDto` (`src/Api/Features/Media/MediaDtos.cs:8`) carries `string? ThumbnailPath`, and
`src/Frontend/src/types/api.ts:4` mirrors it — but **no component reads it.** Every thumbnail in
the UI is built from the id instead, via `thumbnailUrl(item.id)`, because the file is served
through nginx `X-Accel-Redirect` rather than by path.

So each list row ships a filesystem path nothing consumes. It also forces a `LEFT JOIN` onto
`media_thumbnails` in every list projection that includes it.

Cheap to remove — drop the field, the frontend type line, and the `f.Thumbnail!.ThumbnailPath`
term from each projection — but it touches every list handler (`GetMediaList`, `OnThisDay`,
`Tags`, `Search`, `GeoMedia`, `Review`), so it wants doing in one deliberate pass with the tests
green, not opportunistically. Noticed during Phase 13, confirmed during Phase 11.

Worth doing mainly because `/on-this-day` is now uncapped: a busy anniversary renders ~2,250 rows,
each carrying an unused ~60-character path.

---

## `Potd:SendAtLocal` and `Potd:MinConfidence` are not reachable from the env file — 2026-08-17

Phase 11 mapped four of the six `Potd:*` settings into compose (`POTD_ENABLED`, `POTD_BOT_TOKEN`,
`POTD_CHAT_ID`, `POTD_TIMEZONE`). The other two exist in `appsettings.json` and are read correctly,
but there is no `Potd__SendAtLocal` / `Potd__MinConfidence` line in the `api` service, so changing
either means editing `docker-compose.yml` or rebuilding the image.

**The practical cost is testing the scheduler.** Once 15:00 has passed, the loop sleeps until
tomorrow — deleting the `potd_sends` row does *not* re-arm it, and neither does restarting the
Api, because `NextRunTime` from any instant after the slot lands on the next day. Moving the send
time a few minutes ahead and recreating `api` is the only way to watch a scheduled run on demand,
and today that takes a compose edit.

Two lines in the `api` `environment:` block (`Potd__SendAtLocal: ${POTD_SEND_AT:-}` and
`Potd__MinConfidence: ${POTD_MIN_CONFIDENCE:-}`) plus the matching entries in the three env
examples. Use `:-` and leave the default blank, so an unset variable falls through to
`appsettings.json` — `PotdConfig` already treats blank as absent for exactly this reason.

---

## Every scan rediscovers all 992 duplicates — 2026-08-16

Found by the Phase 12 confirmation scan on prod, which logged `discovered 992 new files` on a
library that was already fully imported. 992 is exactly the `skipped_duplicate` count.

`ScanService.DiscoverAsync` excludes a file when it is present in `media_files` **or** holds a job
in an active status (`queued`, `processing`, `enriching`). A duplicate satisfies neither: it never
earned a `media_files` row of its own — that belongs to the file it duplicates, under a different
path — and `skipped_duplicate` is a terminal status, not an active one.

So every scan re-queues all 992, hashes each one off the HDD, finds the match in
`FindByHashAsync`, and writes a second `skipped_duplicate` row. Prod went 992 → 1,984 job rows on
the first re-scan, and would gain another 992 on each subsequent one.

**Not urgent.** Dedup runs *before* conversion in `IngestConsumer`, so the wasted work is one hash
read per file — no AI, no thumbnails, no media rows, no risk to the library. It stayed invisible
until now only because `ScanScheduler` is off by default (`Scan:Enabled=false`) and prod had never
been re-scanned after an import.

**The fix** is one more exclusion in `DiscoverAsync`: treat a `skipped_duplicate` job on a path as
a reason to skip it, the same way `media_files` presence is. Worth a moment's thought first about
the case it forecloses — if the *original* is later deleted from the library, the duplicate would
then never be picked up as its replacement. That may well be the right trade anyway, since nothing
today promotes a duplicate when its original disappears.

Weigh it against just leaving it: the cost is ~992 wasted hashes and 992 junk rows per manual
scan, and scans are rare.

---

## Prod import failure triage — 171 failed jobs — 2026-08-16

**The import is drained.** Final prod counts: **188,291 completed · 992 skipped_duplicate ·
171 failed · 0 queued/processing/enriching**. `media_files` holds 188,300 rows and
`media_embeddings` holds 188,291 — i.e. **every completed job produced both a media row and an
embedding**; the 9-row gap is exactly the AI-step failures in bucket 3 below, which have media
rows (save-early) but no embedding. Enrichment coverage on completed work is 100%.

Supersedes the 2026-08-14 mid-import note (11 files); those 11 are a subset of what follows.

Every one of the 171 was grouped by `error_message`, then each group was probed on prod
(`ffprobe`, `stat`, `od` on the file header, `exiftool`, top-level box parse). Six buckets, four
distinct causes:

| # | Bucket | n | `failed_step` | Cause | Recoverable? |
|---|--------|---|---------------|-------|--------------|
| 1 | `Could not find file '/shared-temp/<guid>.jpg'` | **116** | ingest | our bug — sub-second video seek | **yes, code fix + retry** |
| 2 | `Image cannot be loaded. Available decoders:` | 19 | ingest | 6 AppleDouble + 12 zero-byte + 1 XML | no — but 6 should never be queued |
| 3 | `Cannot open image: image file is truncated` | 9 | **ai** | our bug — Pillow rejects truncated JPEG | **yes, one-line fix + retry** |
| 4 | `ffmpeg exited 183` | 17 | ingest | truncated/zero-byte MOV, no `moov` | no |
| 5 | `heif-convert exited 1: Input file does not…` | 8 | ingest | AppleDouble `._*.HEIC` | no — should never be queued |
| 6 | `heif-convert exited 1: …No 'hvcC' box` | 2 | ingest | unresolved (see below) | unknown |

**125 of the 171 are our bugs and are recoverable** (buckets 1 and 3) — the files are good.
14 more (the `._*` AppleDouble sidecars in buckets 2 and 5) are not media at all and should be
filtered at scan so they stop appearing as failures forever. That leaves **30 genuinely bad or
unexplained source files** out of ~190K.

---

### 1. `Could not find file '/shared-temp/<guid>.jpg'` — 116 files, **our bug**

The sub-second-video bug from the 2026-08-14 note, at its true scale: it was 4 files at the time
because only ~14% of the library had been ingested. **116 files, 114 `.MOV` + 2 `.MP4`.**

All 116 were probed with `ffprobe` inside the worker container. Every one has a valid video stream
(`hevc` or `h264`) and a duration between **0.067 s and 1.113 s**. There is no other pattern —
sizes range 25 KB to 6.3 MB, both codecs, dating from 2013 to 2026. These are perfectly good
videos that we are wrongly rejecting.

`FileConvertService.ExtractVideoFrameAsync` (`src/MediaWorker/Services/FileConvertService.cs:22`)
hardcodes the seek:

```csharp
await RunAsync("ffmpeg",
    ["-i", sourcePath, "-ss", "00:00:01", "-vframes", "1", "-q:v", "2", destPath, "-y"], ct);
```

`-ss 00:00:01` seeks past the end (or past the last frame) of a sub-second file. ffmpeg finds no
frame, writes no JPEG, and **still exits 0** — so `RunAsync` does not throw. The failure surfaces
one step later, in the thumbnail stage, as a missing temp file. That is why the error message
names `/shared-temp/…` rather than ffmpeg.

Note the four files at **1.000–1.113 s** that also failed: the cutoff is not exactly one second.
Output-side seeking needs a frame at or after the 1.0 s mark, and on a variable-frame-rate iPhone
clip whose `duration` includes the last frame's display time, there may not be one. Any fix must
be duration-relative, not a "skip the seek if under 1 s" special case.

### 2. `Image cannot be loaded. Available decoders:` — 19 files, three different things

ImageSharp at the ingest step. Header bytes (`od -An -tx1`) split them cleanly:

- **6 × AppleDouble sidecars** — `._IMG_*.JPG`, all exactly 4096 bytes, header
  `00 05 16 07 00 02 00 00 "Mac OS X"`. That is the AppleDouble magic number: macOS resource-fork
  metadata written next to the real file on a non-HFS volume. They are not images. See bucket 5 —
  the same thing with a `.HEIC` name. **Total across both buckets: 14.**
- **12 × zero-byte `.JPG`** — `stat` says 0 bytes. Nothing to decode.
- **1 × XML with a `.JPG` name** — `/photo/iPhoneBadger23-11-25/202311__/IMG_2686.JPG`, 1006
  bytes, starts `<?xml versio`. Almost certainly a failed-download error document saved under the
  photo's name.

The 13 zero-byte/XML files are dead source data. Nothing in code makes them loadable.

### 3. `Cannot open image: image file is truncated (0 bytes not processed)` — 9 files, **our bug**

**These are the only failures that are not at the ingest step — `failed_step = 'ai'`.** That
matters: the media rows exist and the photos are in the gallery (save-early), they just have no
tags and no embedding. All 9 are in `/photo/2020/`, all 780×1040, 39–85 KB, all with intact
`ff d8 ff e0 … JFIF` headers — and all **missing the `ff d9` end-of-image marker**. They are
truncated JPEGs, most likely partial messenger downloads.

Ingest passed them because `MetadataService` uses ImageSharp's header-only `Image.IdentifyAsync`,
which only needs to reach the SOF marker. The AI sidecar then does a **full decode**, and
`Image.open(...)` → `exif_transpose(...).convert("RGB")` in `inference.py:85-91` raises Pillow's
`image file is truncated (N bytes not processed)`.

Pillow can decode these — it refuses by policy, not inability. The fix is one line in
`src/AiSidecar/inference.py`:

```python
from PIL import ImageFile
ImageFile.LOAD_TRUNCATED_IMAGES = True
```

The bottom of a truncated JPEG decodes as grey, which is harmless for CLIP/YOLO — a tagged photo
with a grey strip beats an untagged one. `ApplyEnrichmentConsumer` keeps the work file when AI
fails (`ApplyEnrichmentConsumer.cs:59-60`), so these retry cleanly from the Admin page.

This bucket also proves the "AI failures never dead-letter" invariant held: the saga completed,
the row was saved, and `UpdateJobStatusAsync` (`ApplyEnrichmentConsumer.cs:114-130`) marked the
job `failed` at step `ai` **by design**. Working as intended, not a defect.

### 4. `ffmpeg exited 183` — 17 files, bad source data, nothing to fix in code

Exit code 183 is `AVERROR_INVALIDDATA` — "Invalid data found when processing input".
(`FFERRTAG('I','N','D','A')` is `-0x41444E49`; as an 8-bit exit status that is `0xB7` = 183.)

`stat` on all 17 inside the worker container — **9 are zero-length**, 8 are truncated:

| Size | File |
|------|------|
| **0** | `2021/video/IMG_1355.MOV`, `IMG_1365.MOV`, `IMG_1470.MOV`, `IMG_1873.MOV` |
| **0** | `iPhoneBadger22-10-22/165APPLE/IMG_5208.MOV`, `IMG_5211.MOV`, `IMG_5215.MOV`, `IMG_5220.MOV` |
| **0** | `iPhoneBadger24-03-31/202402__/IMG_6699.MOV` |
| 2,621,440 | `2021/video/IMG_6366.MOV` — exactly 2.5 MiB |
| 3,460,661 | `iPhoneBadger22-10-22/162APPLE/IMG_2717.MOV` |
| 4,194,304 | `iPhoneBadger22-10-22/163APPLE/IMG_3541.MOV` — exactly 4 MiB |
| 6,549,690 | `iPhoneBadger2025-01-02/202412_a/IMG_6159.MOV` |
| 8,178,550 | `iPhoneBadger2025-01-02/202412_a/IMG_6207.MOV` |
| 10,707,815 | `2021/video/IMG_2364.MOV` |
| 200,278,016 | `iPhoneBadger23-01-11/169APPLE/IMG_9143.MOV` — exactly 191 MiB |
| 498,540,629 | `PhotoLibrary/Masters/2017/06/09/20170609-174904/IMG_0759 (1).MOV` |

ffprobe reports **moov atom not found** on every one. iPhone writes the `moov` index atom **last**,
so an interrupted recording or an interrupted copy leaves an `mdat` payload with no index.
Verified byte-by-byte on `IMG_6159.MOV`: `ftyp` → `wide` → `mdat` declaring 6,523,582 bytes, then
26,080 further bytes of raw payload and **no atom header at all** where `moov` should begin.
Three of the sizes are exact power-of-two-MiB boundaries — the signature of a copy that died
mid-buffer, not of a bad recording.

These files are unplayable in any player. Retrying them just re-fails. **Nothing to fix in code —
the correct end state is that they stay `failed`, or get culled from the library.** Mike's call:
leave them as a permanent 17-row entry in Failed Jobs, or delete the source files.

### 5. `heif-convert exited 1: Input file does not appear to start with a valid box length` — 8 files

All 8 are AppleDouble sidecars — `._IMG_*.HEIC`, 4096 bytes, `00 05 16 07` magic. Same non-media
files as the 6 in bucket 2, just landing in the HEIC branch because of the extension.
**14 AppleDouble files total.**

They exist because the library was copied from a Mac to a non-HFS volume at some point. macOS
writes the resource fork and extended attributes into a `._<name>` companion, and our scan picks
them up purely on extension. `ScanService` should exclude them — see fix 5 below.

### 6. `heif-convert exited 1: Could not read HEIF/AVIF file: Invalid input: No 'hvcC' box` — 2 files, **unresolved**

- `iPhoneBadger2025-06-09/IMG_8010.HEIC` — 1,897,815 bytes, 4032×3024
- `iPhoneMike2025-06-09/IMG_1097.HEIC` — 4,126,779 bytes, 5712×4284

These are **real photos**, and everything checkable says they are well-formed:

- Top-level box parse: `ftyp` → `meta` → `mdat`, and `mdat`'s declared 64-bit length ends
  **exactly** at EOF on both. Not truncated.
- `exiftool` reads `HEVCConfigurationVersion: 1`, full dimensions, EXIF, Apple MakerNote and an
  ICC profile out of both — so an `hvcC` box *is* present in the file.
- libheif is **1.19.8** (strukturag PPA), not an old distro build.
- The tempting explanation — Apple's HDR gain-map / `tmap` derived-image HEICs that older libheif
  can't resolve — **is wrong**. A sibling in the same folder (`iPhoneMike2025-06-09/IMG_0003.HEIC`)
  has an identical brand set (`mif1, MiHB, MiHE, MiPr, miaf, heic, tmap`), the same
  `urn:com:apple:photo:2020:aux:hdrgainmap` auxiliary image, and converts fine.
- ffmpeg can't open them either (`moov atom not found` — ffmpeg's HEIF demuxer is weak, so this
  is not independent evidence).

The one asymmetry found: `IMG_8010`'s `meta` box is **4,224 bytes** where healthy siblings run
~31–32 KB. That points at a damaged or rewritten item-property table — the `hvcC` bytes are on
disk but the primary item's property association no longer reaches them. `IMG_1097`'s `meta` is a
normal 31,091 bytes, so that doesn't explain both.

**Two files out of 190K. Not worth more forensics.** If they matter, the cheap next moves are
(a) re-copy them from the phone or a Time Machine backup, or (b) try `pillow-heif` /
ImageMagick as a one-off — a different HEIF parser may be more forgiving than libheif's.

---

### Fixes to make

> **Done and deployed 2026-08-16 as Phase 12 Steps 1–3 and 5 — A1–A4, B5 and C6 are all live on
> prod.** The retry recovered **all 125** files: buckets 1 and 3 are empty, failed went 171 → 32,
> and `media_files` = `media_embeddings` = 188,416. The 14 AppleDouble rows (buckets 2 and 5) were
> deleted and are filtered at scan now. **Step 4 was then settled the same day: buckets 4 and 6
> plus the 13 zero-byte/XML files were deleted from disk with their rows** (manifest at
> `/opt/homemediahub/phase12-deleted-2026-08-16.txt`), leaving **0 failed jobs**. Numbers in
> `docs/delivery-plan.md` → Phase 12 → "Deployed and verified on prod".
> The evidence below stands unchanged and is still the reference for what each bucket was.

**A. `src/MediaWorker/Services/FileConvertService.cs`** — recovers the 116 videos.

1. **Clamp the seek.** Probe duration and seek to `min(1s, duration/2)`, or drop the seek and use
   `-ss 0 -vf thumbnail`. Must be duration-relative — four of the 116 are *over* 1.0 s
   (up to 1.113 s) and still fail. Retry from the Admin page afterwards; no re-scan needed.
2. **Assert the output file exists** after `RunAsync` returns 0. "ffmpeg said OK but wrote nothing"
   should fail at the ffmpeg step with a clear message instead of confusing the next stage. This
   is exactly what disguised a one-line bug as a shared-temp problem, on 116 files.
3. **Add `-hide_banner -loglevel error`** to the ffmpeg invocation. Today `RunAsync` throws with
   the *entire* stderr (line 41), and ffmpeg prints its version banner plus the full `configure`
   line before anything else — so `processing_jobs.error_message` is ~2 KB of `--enable-libfoo`
   and the actual reason is off the end of the Admin table. Confirmed on all 17 bucket-4 rows:
   the useful part of the message is invisible in the UI.
4. **Latent deadlock, worth fixing while in there.** `RunAsync` calls `WaitForExitAsync` (line 36)
   and only *then* drains stderr (line 40). ffmpeg's banner fits the ~64 KB pipe buffer so it
   survives today, but any child process emitting more than that would block on write forever and
   hang the consumer. Read stderr concurrently with the wait.

**B. `src/AiSidecar/inference.py`** — recovers the 9 truncated JPEGs.

5. Set `ImageFile.LOAD_TRUNCATED_IMAGES = True` at import time (see bucket 3). One line. Add a
   sidecar test with a deliberately truncated JPEG fixture.

**C. `src/MediaWorker/Services/ScanService.cs:33-35`** — stops 14 permanent non-failures.

6. Skip files whose name starts with `._` (AppleDouble), and skip zero-length files, before the
   extension filter. Both are provably not media, and today each one costs a queue round-trip, a
   `processing_jobs` row and a permanent line in the Admin table on **every** scan.
   Have `ScanConsumer` log the skipped count so the filtering is observable rather than silent.

Fixes 1, 2 and 5 are correctness — 125 good files are currently missing or unenriched. Fix 6 is
hygiene. Fixes 3 and 4 are the reason this cost time to diagnose, twice.

---

## Photo of the Day — Telegram bot ✅ DONE (2026-08-17)

**Built, deployed and verified on prod as Phase 11.** The scheduled 15:00 run fires and posts to
the family channel. Nothing here is outstanding — kept only as the pointer trail:

- **Design & rationale:** `docs/architecture.md` §14 — why a `BackgroundService` in the Api rather
  than a queue and consumer, the pick query, the `potd_sends` table, config, and the five ways it
  differs from `ScanScheduler`.
- **Implementation steps & verification:** `docs/delivery-plan.md` → Phase 11.

Prior art is the old Raspberry Pi solution at `C:\Users\Mike\Projects\PhotoLibrary\backend`;
file-by-file pointers are in the delivery-plan section.

Measured on prod during the build: all **366** calendar days clear tier 1, so the confidence floor
of 0.5 is settled and tiers 2–3 never fire on this library. The one bug worth remembering is in
"Fixed on the first real send" — a bot token's colon made the request URI parse as absolute, and
**every dry run passed anyway**, because the URI is only built on a real send.
