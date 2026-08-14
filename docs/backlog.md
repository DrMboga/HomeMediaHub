# Backlog

Deferred work items. Each entry records what was investigated, what the cause actually is (read
off code or measured, not guessed), and what would need to change — so the work can be picked up
cold without re-deriving anything.

Items that graduate into planned work move out of here and into `docs/architecture.md` (design)
plus `docs/delivery-plan.md` (steps), leaving a pointer behind.

Ordered newest first.

---

## Video ingest failures found during the prod import — 2026-08-14

> **Do not touch until the prod archive import (Phase 9) finishes.** Mike's call: the import runs
> for days and nothing here is worth restarting the worker over. Eleven files out of ~190K.
> Revisit when the queue drains.

Found while looking at the Admin page's Failed Jobs table mid-import. Prod job counts at the time:
26,558 completed · 70,876 enriching · 91,965 queued · 42 skipped_duplicate · **11 failed** ·
2 processing. All 11 failed at the `ingest` step. They are **two unrelated problems**.

### 1. `ffmpeg exited 183` — 7 files, bad source data, nothing to fix in code

Exit code 183 is `AVERROR_INVALIDDATA` — "Invalid data found when processing input".
(`FFERRTAG('I','N','D','A')` is `-0x41444E49`; as an 8-bit exit status that is `0xB7` = 183.)

ffprobe run against all seven inside the worker container:

| File | Size | ffprobe |
|------|------|---------|
| `2021/video/IMG_1355.MOV` | **0 bytes** | moov atom not found |
| `2021/video/IMG_1365.MOV` | **0 bytes** | moov atom not found |
| `2021/video/IMG_1470.MOV` | **0 bytes** | moov atom not found |
| `2021/video/IMG_1873.MOV` | **0 bytes** | moov atom not found |
| `2021/video/IMG_2364.MOV` | 10,707,815 | moov atom not found |
| `2021/video/IMG_6366.MOV` | 2,621,440 | moov atom not found |
| `iPhoneBadger2025-01-02/202412_a/IMG_6207.MOV` | 8,178,550 | moov atom not found |

Four are zero-length. The other three are truncated: iPhone writes the `moov` index atom **last**,
so an interrupted recording or an interrupted copy leaves an `mdat` payload with no index.
Verified byte-by-byte on `IMG_6159.MOV` (a sibling case, since retried): `ftyp` → `wide` →
`mdat` declaring 6,523,582 bytes, then 26,080 further bytes of raw payload and **no atom header
at all** where `moov` should begin.

These files are unplayable in any player. Retrying them just re-fails. **Nothing to fix — the
correct end state is that they stay `failed`, or get culled from the library.** Worth a decision
after the import: leave them as a permanent 7-row entry in Failed Jobs, or delete the source files.

### 2. `Could not find file '/shared-temp/<guid>.jpg'` — 4 files, **our bug**

| File | Duration |
|------|----------|
| `iPhoneBadger2025-06-09/IMG_7161.MOV` | 0.865 s |
| `iPhoneBadger2025-06-09/IMG_7164.MOV` | 0.565 s |
| `iPhoneBadger2025-06-09/IMG_7165.MOV` | 0.465 s |
| `iPhoneBadger2025-06-09/IMG_6847.MOV` | 0.333 s |

All four are **under one second long**, and all four are perfectly good HEVC videos we are
wrongly rejecting.

`FileConvertService.ExtractVideoFrameAsync` (`src/MediaWorker/Services/FileConvertService.cs:22`)
hardcodes the seek:

```csharp
await RunAsync("ffmpeg",
    ["-i", sourcePath, "-ss", "00:00:01", "-vframes", "1", "-q:v", "2", destPath, "-y"], ct);
```

`-ss 00:00:01` seeks past the end of a sub-second file. ffmpeg finds no frame, writes no JPEG,
and **still exits 0** — so `RunAsync` does not throw. The failure surfaces one step later, in the
thumbnail stage, as a missing temp file. That is why the error message names `/shared-temp/…`
rather than ffmpeg.

### Fixes to make (all in `FileConvertService.cs`)

1. **Clamp the seek.** Probe duration and use `min(1s, duration/2)`, or drop the seek entirely and
   use `-ss 0 -vf thumbnail`. Fixes all four files above. They can then be retried from the Admin
   page — no re-scan needed.
2. **Assert the output file exists** after `RunAsync` returns 0. "ffmpeg said OK but wrote
   nothing" should fail at the ffmpeg step with a clear message instead of confusing the next
   stage. This is what turned a one-line bug into a five-minute investigation.
3. **Add `-hide_banner -loglevel error`** to the ffmpeg invocation. Today `RunAsync` throws with
   the *entire* stderr (line 41), and ffmpeg prints its version banner plus the full `configure`
   line before anything else — so `processing_jobs.error_message` is ~2 KB of `--enable-libfoo`
   and the actual reason is off the end of the Admin table. The error is unreadable in the UI
   precisely when you need it.
4. **Latent deadlock, worth fixing while in there.** `RunAsync` calls `WaitForExitAsync` (line 36)
   and only *then* drains stderr (line 40). ffmpeg's banner fits the ~64 KB pipe buffer so it
   survives today, but any child process emitting more than that would block on write forever and
   hang the consumer. Read stderr concurrently with the wait.

Items 1 and 2 are correctness; 3 and 4 are the reason this cost time to diagnose.

---

## Photo of the Day — Telegram bot

**Specified 2026-08-14, not started.** Promoted out of this backlog into the specs — it is planned
work now, not a loose note:

- **Design & rationale:** `docs/architecture.md` §14 — why a `BackgroundService` in the Api rather
  than a queue and consumer, the pick query, the `potd_sends` table, config, and the five ways it
  differs from `ScanScheduler`.
- **Implementation steps & verification:** `docs/delivery-plan.md` → Phase 11.

Prior art is the old Raspberry Pi solution at `C:\Users\Mike\Projects\PhotoLibrary\backend`;
file-by-file pointers are in the delivery-plan section. Both open questions were answered by Mike
on 2026-08-14 (three-tier fallback so a photo goes out every day; confidence floor 0.5), so the
spec is complete and nothing blocks implementation.
