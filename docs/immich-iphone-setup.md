# Immich iPhone Setup

Immich runs on the server purely as the **iPhone backup endpoint**. The iOS app uploads to it, it
writes the originals under `/mnt/storage/Photo/iPhone/`, and HomeMediaHub's scan picks them up from
there. You do not use the Immich web UI day-to-day.

**State as of 2026-08-29:** container up and healthy (`v3.1.0`), one user, **350 assets backed up
from the iPhone** — and deliberately **not yet scanned** into HomeMediaHub.

> **This document was written against 0 assets, and §2 and §3 below turned out to be wrong.** The
> real tree was measured on 2026-08-29; what it actually looks like, and the six steps that make a
> scan safe, are in `docs/delivery-plan.md` → **Phase 14**. Read that first. Corrections are marked
> inline below.

---

## ⚠️ Read this before you back anything up

**A scan of `/photo` today would ingest Immich's own derivative files as if they were photos.**

`ScanService.DiscoverAsync` (`src/MediaWorker/Services/ScanService.cs`) walks
`SearchOption.AllDirectories` from `/photo` and filters on three things only: file extension, the
AppleDouble `._` prefix, and zero length. **There is no directory exclusion.** Immich's media root
is bind-mounted *inside* the scanned tree:

```
/mnt/storage/Photo/iPhone/          ← ${PHOTO_DIR}/iPhone, mounted as Immich's /usr/src/app/upload
├── upload/     350 files, 2.1 G    ← the originals ARE HERE (storage template is off)
├── thumbs/     701 files, 135 M    ← Immich's own previews — 350 .jpeg + 350 .webp
├── encoded-video/ 21 files, 1.1 G  ← Immich's H.264 transcodes, .mp4
├── backups/     13 files, 231 M    ← .sql.gz, harmless (extension not supported)
├── library/    EMPTY               ← where the originals go once §3 is done
└── profile/    EMPTY               ← avatars
```

> **Correction (2026-08-29):** the tree above originally showed the originals under `library/`.
> They are not there — the storage template ships **disabled**, so they sit in `upload/` under GUID
> names. See §3. This matters: an exclusion list written against the old assumption would have
> skipped the entire camera roll.

`.mp4` and `.jpeg` are both in `SupportedExtensions`, so every transcode and every preview would be
queued as a new media file. **Dedup will not save you** — a preview is a different image at a
different resolution, so it hashes differently and becomes a genuine second row. The result is
roughly one junk photo per photo and one junk video per video.

*(All of this is now measured rather than predicted. A scan today discovers **721 files, 371 of
them junk**: 350 `_preview.jpeg` at 1440×1920 and 21 transcodes, against 350 real photos. The
`_thumbnail.webp` files escape only because `.webp` is not in `SupportedExtensions`.)*

**You are not at risk right now.** `Scan:Enabled=false` on prod, so nothing scans on a timer; the
only way a scan starts is you pressing **Trigger Scan** on `/admin`. So:

> **Safe order: set Immich up and back up your phone freely. Do not run a scan until the directory
> exclusion is in.** Backing up is harmless on its own — the files just sit on disk.

The fix is a directory exclusion in `ScanService` (skip `thumbs`, `encoded-video`, `upload`,
`profile`, `backups`), so the scan sees only `library/` — **but only after §3 has actually put the
originals in `library/`.** Order matters. There is also a second hazard the original version of
this document missed: Immich streams uploads **straight to their final path** with no temp name, so
a scan overlapping a phone backup can hash a half-written file and index it permanently. Both are
handled by `docs/delivery-plan.md` → Phase 14, Steps 1–3.

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

Also worth turning off, since nothing reads them and they cost CPU and disk on the same box that
runs the pipeline: **Video transcoding** (Immich's `encoded-video/` output is never used by us —
HomeMediaHub transcodes on demand at `/api/media/{id}/stream`).

> **Correction (2026-08-29): this was not done before the first backup, and it cost 1.1 GB** —
> 21 clips transcoded at ~52 MB each, on the same 4-core box that runs YOLO and CLIP. Turn both off
> *before* backing anything up.
>
> "Turn off" means **Video Transcoding Settings → Transcode Policy → "Don't transcode any videos"**
> — there is no on/off switch. The default is `required`, which still transcodes every HEVC clip an
> iPhone produces.
>
> To clear transcodes already made, run **Jobs → Transcode Videos → All** afterwards. Immich then
> deletes each encoded file *and* its `asset_file` row (`media.service.js:434`). **Do not delete
> `encoded-video/` by hand** — the files would go but the rows would remain, pointing at nothing.

## 3. Enable the storage template

> **Correction (2026-08-29): "check" was wrong — the feature ships switched off.** Immich v3.1.0's
> `server/dist/config.js` reads `storageTemplate: { enabled: false, template: '{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}' }`.
> Because it was left off, all 350 originals are in the **staging** folder
> `upload/<userId>/<xx>/<yy>/<uuid>.HEIC` under GUID names, and `library/` is empty. You must:
>
> 1. **enable** the template (keep the default value);
> 2. set the user's **Storage Label** under Administration → Users — without one the folder is the
>    raw user GUID, because Immich uses `storageLabel || id`;
> 3. run **Administration → Jobs → Storage Migration** to move the assets already uploaded.
>
> Do this **before the first scan** — it rewrites every path, which is free while nothing is
> indexed and a migration afterwards. Verify commands: `docs/delivery-plan.md` → Phase 14 → Step 1.

**Administration → Settings → Storage Template.** The default

```
{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}
```

is what we want. It applies *beneath* `library/<storageLabel>/`, so with the label set to `mike` a
photo lands at

```
/mnt/storage/Photo/iPhone/library/mike/2026/2026-04-11/IMG_5181.HEIC
```

Confirm the real path after the migration rather than trusting this line — the `library/<label>/`
prefix is Immich's, not something the template controls.

Two things this buys beyond tidiness: the original filename (`IMG_5181.HEIC`) reaches the disk, so
HomeMediaHub never has to read Immich's database to get it; and `upload/` becomes pure staging, with
files reaching `library/` only by an atomic same-filesystem rename once fully written — which is
what makes a scan safe to run while the phone is backing up.

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

Once **Phase 14 Steps 1–3** are done — the storage template enabled and migrated, the scan's
directory exclusion and file-age guard deployed:

- **`/admin` → Library Scan → Trigger Scan** (leave the path blank for `/photo`), or
  `POST /api/admin/scan`.
- Watch progress on the same page, or with `docs/monitoring.md`.

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
  `skipped_duplicate` are re-hashed on every scan.
- **Immich's own UI** at `:2283` will show the backed-up photos, but HomeMediaHub on port 80 is the
  library.
