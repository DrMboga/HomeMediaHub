# Immich iPhone Setup

Immich runs on the server as the iPhone backup endpoint. It receives photos directly from the iOS app and writes them to `/mnt/usb-hdd/Photo/iPhone/`. HomeMediaHub's Library Scanner (W2) picks them up from there.

You do not use the Immich web UI day-to-day — it is purely an ingest mechanism.

---

## One-time server setup

### 1. Create the Immich admin account

Open the Immich web UI in a browser (from a device on your LAN):

```
http://192.168.0.192:2283
```

On first visit, Immich prompts you to create an admin account. Use any name/email/password — this account is only used for Immich administration, not for HomeMediaHub login.

### 2. Verify the storage template

Immich should already be configured via the env file to write uploads to `/mnt/usb-hdd/Photo/iPhone/`. To confirm:

Admin → Server Settings → Storage Template — verify it shows:

```
{{y}}/{{y}}-{{MM}}-{{dd}}/{{filename}}
```

This produces paths like `iPhone/2026/2026-05-31/IMG_1234.HEIC`.

---

## iPhone app setup

### 1. Install the Immich app

Search for **Immich** in the App Store (it's free, open-source).

### 2. Connect to the server

- Open the app → **Get Started**
- Server URL: `http://192.168.0.192:2283`
- Log in with the admin account you created above (or create a separate user in Immich Admin → Users if you want per-person accounts)

### 3. Enable automatic backup

- Go to **Profile** (bottom right) → **Backup**
- Enable **Background backup**
- Select the albums to back up (typically **Recents** = entire photo library)
- Optionally enable **Require Wi-Fi** to avoid mobile data usage

---

## What happens after backup

1. Immich receives the photo and writes the original file to `/mnt/usb-hdd/Photo/iPhone/{y}/{y-MM-dd}/`
2. The file sits there until Library Scanner runs
3. To ingest immediately: open n8n (`http://192.168.0.192:5678`) → find **W2 Library Scanner** → click **Execute Workflow**
4. The monthly cron (`0 2 1 * *`) catches any backups automatically

The photo will appear in HomeMediaHub's gallery once W2 processes it.

---

## Notes

- **HEIC files**: iPhone photos are HEIC format. W1 converts them to JPEG for metadata extraction and thumbnails; the original HEIC is never modified.
- **Videos**: iPhone `.mov` (HEVC/H.265) files are processed similarly. The `/api/media/{id}/stream` endpoint transcodes them to H.264 on first play and caches the result.
- **Duplicate detection**: W1 hashes every file before inserting. Running W2 twice processes each file only once.
- **Immich UI**: You can use the Immich web UI at `:2283` to see backed-up photos, but the HomeMediaHub gallery at port 80 is the primary UI.
