# Immich iPhone Setup

Immich runs on the server purely as the **iPhone backup endpoint**. The iOS app uploads to it, it
writes the originals under `/mnt/storage/Photo/iPhone/`, and HomeMediaHub's scan picks them up from
there. You do not use the Immich web UI day-to-day.

**State as of 2026-08-17:** container up and healthy (`v3.1.0`), reachable on `:2283`, storage tree
created — and **0 users, 0 assets**. Nothing has been set up yet.

---

## ⚠️ Read this before you back anything up

**A scan of `/photo` today would ingest Immich's own derivative files as if they were photos.**

`ScanService.DiscoverAsync` (`src/MediaWorker/Services/ScanService.cs`) walks
`SearchOption.AllDirectories` from `/photo` and filters on three things only: file extension, the
AppleDouble `._` prefix, and zero length. **There is no directory exclusion.** Immich's media root
is bind-mounted *inside* the scanned tree:

```
/mnt/storage/Photo/iPhone/          ← ${PHOTO_DIR}/iPhone, mounted as Immich's /usr/src/app/upload
├── library/                        ← the originals. These are what we want.
├── thumbs/                         ← Immich's own previews — JPEG by default
├── encoded-video/                  ← Immich's H.264 transcodes, .mp4
├── upload/                         ← in-flight uploads
├── profile/                        ← avatars
└── backups/                        ← .sql.gz, harmless (extension not supported)
```

`.mp4` and `.jpeg` are both in `SupportedExtensions`, so every transcode and every preview would be
queued as a new media file. **Dedup will not save you** — a preview is a different image at a
different resolution, so it hashes differently and becomes a genuine second row. The result is
roughly one junk photo per photo and one junk video per video.

*(Certain: the directory layout above, the scan's lack of directory filtering, and that
`encoded-video` holds `.mp4`. The preview extension is Immich's documented default of JPEG, which
I could not verify inside the running image — worth confirming after your first backup.)*

**You are not at risk right now.** `Scan:Enabled=false` on prod, so nothing scans on a timer; the
only way a scan starts is you pressing **Trigger Scan** on `/admin`. So:

> **Safe order: set Immich up and back up your phone freely. Do not run a scan until the directory
> exclusion is in.** Backing up is harmless on its own — the files just sit on disk.

The fix is a directory exclusion in `ScanService` (skip `thumbs`, `encoded-video`, `upload`,
`profile`, `backups`), so the scan sees only `library/`.

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

## 3. Check the storage template

**Administration → Settings → Storage Template.** The default

```
{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}
```

is what we want. It applies *beneath* `library/<userId>/`, so a photo lands at roughly

```
/mnt/storage/Photo/iPhone/library/<userId>/2026/2026-08-17/IMG_1234.HEIC
```

Confirm the real path after your first upload rather than trusting this line — the `library/<userId>/`
prefix is Immich's, not something the template controls.

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

Once the exclusion above is in place:

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
