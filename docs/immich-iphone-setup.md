# Immich iPhone Setup

Immich runs on the server purely as the **iPhone backup endpoint**. The iOS app uploads to it, it
writes the originals under `/mnt/storage/Photo/iPhone/`, and HomeMediaHub's scan picks them up from
there. You do not use the Immich web UI day-to-day.

**State as of 2026-08-29:** `v3.1.0`, one user (`mboga`, storage label `mike`), **350 assets backed
up and ingested into HomeMediaHub**. Storage template on, video transcoding and machine learning
off. Everything below is measured on that setup, not predicted.

---

## Why Immich's folders are dangerous to scan

`${PHOTO_DIR}/iPhone` is **not a folder of photos — it is Immich's entire media root**
(`IMMICH_MEDIA_LOCATION`, `docker-compose.yml`), and it sits inside the tree HomeMediaHub scans:

```
/mnt/storage/Photo/iPhone/       ← mounted as Immich's /usr/src/app/upload
├── library/mike/YYYY/YYYY-MM-DD/IMG_1234.heic   ← the originals. The ONLY folder we scan.
├── upload/          staging for in-flight uploads — emptied on completion
├── thumbs/          Immich's previews: one .jpeg + one .webp per photo
├── encoded-video/   Immich's H.264 transcodes (empty while transcoding is off)
├── backups/         weekly Immich DB dumps, .sql.gz
└── profile/         avatars
```

`.jpeg` and `.mp4` are both in `SupportedExtensions`, so **without an exclusion every preview and
every transcode is ingested as a photo**, and dedup cannot help — a preview is a different image at
a different resolution, so it hashes differently and earns a real `media_files` row. Measured
before the fix: a scan discovered **721 files, 371 of them junk**. The `_thumbnail.webp` files
escaped only because `.webp` is not a supported extension — luck, not design.

**This is handled, and must stay handled.** `SCAN_EXCLUDE_DIRS` in `.env.prod` prunes the five
non-`library` folders, and `SCAN_MIN_FILE_AGE_SECONDS` (120) stops a scan hashing a file that is
still being written — Immich streams uploads straight to their final path with no temp name, and so
does a Samba copy. Both are Phase 14; see `docs/delivery-plan.md` and `docs/architecture.md` §2.

> **If you ever add a service that writes derivatives into the library, add its folders to
> `SCAN_EXCLUDE_DIRS` before the next scan.** And if `.webp` is ever added to
> `SupportedExtensions`, the exclusion is the only thing standing between you and 350 more junk
> rows per backup.

`Scan:Enabled=false` on prod, so nothing scans on a timer — the only way a scan starts is
**Trigger Scan** on `/admin`.

---

## 1. Create the Immich admin account

From any browser on the LAN:

```
http://192.168.0.192:2283
```

First visit prompts you to create an admin account. Any name / email / password — this is Immich's
own account, unrelated to your HomeMediaHub login.

**Use `:2283` directly, not `http://192.168.0.192/immich/`.** The nginx proxy for `/immich/` exists
but sets no `client_max_body_size`, so nginx's 1 MB default would reject every photo upload with a
413. The port is published by compose on all interfaces, so the phone can reach it directly.

## 2. Turn off what we don't use

Immich ships its own machine-learning stack; we deliberately don't run that container, because
HomeMediaHub does its own YOLO + CLIP work. The server logs
`Machine learning server became unhealthy` on a loop as a result — harmless, but you can silence it:

**Administration → Settings → Machine Learning → disable.**

**Video transcoding must also go off, and it is not a switch.** Immich's `encoded-video/` output is
never read by us — HomeMediaHub transcodes on demand at `/api/media/{id}/stream` — but the default
policy is `required`, which still transcodes every HEVC clip an iPhone produces. Left on through
the first backup it cost **1.1 GB for 21 clips**, on the same 4-core box that runs YOLO and CLIP.

**Administration → Settings → Video Transcoding Settings → Transcode Policy → "Don't transcode any
videos".**

To clear transcodes already made, run **Jobs → Transcode Videos → All** afterwards: Immich deletes
each encoded file *and* its `asset_file` row (`media.service.js:434`). **Do not delete
`encoded-video/` by hand** — the files would go but the rows would remain, pointing at nothing.

## 3. Enable the storage template

**The template ships switched off, and this is the setting that matters most.** Immich v3.1.0's
`server/dist/config.js` reads
`storageTemplate: { enabled: false, template: '{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}' }` — so
until you enable it, originals sit in the **staging** folder
`upload/<userId>/<xx>/<yy>/<uuid>.HEIC` under GUID names and `library/` stays empty.

**Administration → Settings → Storage Template**, in this order:

1. **Set the user's Storage Label first** (Administration → Users → the user). Immich builds the
   folder from `storageLabel || id`, so without one you get the raw user GUID — and it defaults to
   `admin`, which is a poor name for a family photo folder. `mike` is what prod uses.
2. **Enable the template.** Keep the default value and leave **Hash verification** on — it
   checksums each file around the move.
3. **Administration → Jobs → Storage Template Migration** to move assets already uploaded.

Do all three **before the first scan.** It rewrites every `originalPath`, which is free while
nothing is indexed and a migration afterwards. Verify commands: `docs/delivery-plan.md` → Phase 14
→ Step 1.

A photo then lands at

```
/mnt/storage/Photo/iPhone/library/mike/2026/2026-04-11/IMG_5181.heic
```

Note the **lowercase extension**: the template lowercases every extension and also canonicalises
`jpeg` → `jpg`. Harmless here — every extension comparison HomeMediaHub makes is case-insensitive,
and both `.jpg` and `.jpeg` are supported — but it means the name on disk is not byte-identical to
the name on the phone.

Two things this buys beyond tidiness. The original filename reaches the disk, so HomeMediaHub never
has to read Immich's database to get it — without the template the only copy of `IMG_5181.HEIC`
lives in Immich's `asset.originalFileName`, and the lightbox would show a GUID path. And `upload/`
becomes pure staging: a file reaches `library/` only by an atomic same-filesystem rename once fully
written, which is what makes a scan safe to run while the phone is backing up.

---

## 4. iPhone app

1. Install **Immich** from the App Store (free, open-source).
2. Open it → **Get Started** → Server URL `http://192.168.0.192:2283` → log in with the admin
   account.
3. **Profile → Backup** → enable **Background backup**.
4. Select albums — **Recents** is the whole camera roll.
5. Enable **Require Wi-Fi** unless you want it running on cellular.

The first backup of a full camera roll takes a while and the phone must stay on Wi-Fi and
(realistically) on charge. iOS background execution is unreliable — leaving the app in the
foreground is much faster.

---

## 5. Getting the photos into HomeMediaHub

- **`/admin` → Library Scan → Trigger Scan** (leave the path blank for `/photo`), or
  `POST /api/admin/scan`.
- Watch progress on the same page, or with `docs/monitoring.md`.

Check the scan's own log line before trusting the result — it reports every filter, even at zero:

```
Scan {id}: discovered 1342 new files under /photo
Scan {id}: skipped 14 AppleDouble sidecar(s), 0 zero-length file(s) and 0 file(s)
           written too recently; pruned 5 excluded director(ies)
```

**`pruned 5` is the one to look at.** If it says 0, `SCAN_EXCLUDE_DIRS` is missing from `.env.prod`
or the worker was restarted rather than recreated, and the scan is about to ingest Immich's
thumbnails.

There is no automatic scan: `Scan:Enabled=false`, and `ScanScheduler` logs
`ScanScheduler disabled` at startup. Turning it on would run monthly at 02:00 on the 1st.

> The old version of this document told you to trigger the scan from n8n's **W2 Library Scanner**.
> n8n was removed entirely in Phase 8 Step 7 — there is no workflow, and no `:5678`.

---

## Notes

- **HEIC**: iPhone photos are HEIC. The pipeline converts a copy for metadata and thumbnails, and
  the API converts on demand for display; the original HEIC is never modified.
- **Videos**: iPhone `.mov` (HEVC) is handled the same way. `/api/media/{id}/stream` transcodes to
  H.264 on first play and caches it — which is why Immich's own transcoding is redundant here.
- **Duplicates**: ingest hashes every file before insert, so scanning twice processes each file
  once. Note the known wrinkle in `docs/backlog.md` — files already recorded as
  `skipped_duplicate` are re-hashed on every scan. A photo you deleted in Review is remembered by
  hash in `deleted_hashes` and lands on `skipped_deleted` instead, so it cannot come back from a
  second copy on disk.
- **Deletions do not sync.** Immich's backup is one-way: removing a photo from the iPhone camera
  roll leaves the server asset alone, and HomeMediaHub never sees a deletion. Nothing under
  `library/` disappears unless someone acts in Immich's own UI (its trash is enabled, 30 days).
- **Immich's own UI** at `:2283` will show the backed-up photos, but HomeMediaHub on port 80 is the
  library.
