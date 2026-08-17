# Telegram Photo of the Day Setup

HomeMediaHub posts one photo a day to a private Telegram channel at 15:00 local time. This guide
covers creating the bot, creating the channel, and configuring the Api.

> Sections 1–3 date from the original n8n implementation and are unchanged — the bot and the
> channel are the same thing. Everything from section 4 on was rewritten for **Phase 11**
> (2026-08-17), which rebuilt the feature as `PhotoOfTheDayScheduler : BackgroundService` inside
> the Api. There is no workflow, no webhook, no queue and no consumer.
> Design: [`architecture.md`](architecture.md) §14.

---

## 1. Create a Telegram bot

1. Open Telegram and search for **@BotFather**
2. Send `/newbot`
3. Choose a name (e.g. `HomeMediaHub`)
4. Choose a username (must end in `bot`, e.g. `homemediahub_bot`)
5. BotFather replies with the **bot token** — save it: `7123456789:AAF...`

---

## 2. Create a Telegram channel

1. In Telegram: new channel → pick a name (e.g. `Family Photos`)
2. Set it to **Private** (only invited members see it)
3. Add the bot as an **administrator** with permission to post messages:
   - Channel Info → Administrators → Add Administrator → search for your bot username

---

## 3. Get the channel chat ID

The bot needs the channel's numeric chat ID (not its name).

**Option A — via the bot itself:**

1. Send any message to the channel
2. Open this URL in a browser (replace `TOKEN` with your bot token):
   ```
   https://api.telegram.org/botTOKEN/getUpdates
   ```
3. Find `"chat":{"id":-100xxxxxxxxxx}` in the response — that negative number is the chat ID

**Option B — via @username:**

If your channel has a public username (e.g. `@familyphotos`), use that string directly as the
chat ID.

---

## 4. Configure the Api

Four variables, in the env file for the environment that should post — **`.env.prod`, not
`.env.stage`**:

| Variable | Meaning | Default |
|---|---|---|
| `POTD_ENABLED` | Gates the **scheduler** only. Must be literally `true`. | `false` |
| `POTD_BOT_TOKEN` | From section 1. | — |
| `POTD_CHAT_ID` | From section 3, including the leading `-100`. | — |
| `POTD_TIMEZONE` | An IANA zone id — what "15:00" means. | `Europe/Berlin` |

```bash
POTD_ENABLED=true
POTD_BOT_TOKEN=7123456789:AAF...
POTD_CHAT_ID=-1001234567890
POTD_TIMEZONE=Europe/Berlin
```

Then recreate the Api so it picks them up:

```bash
docker compose --env-file .env.prod up -d --force-recreate api
docker compose --env-file .env.prod logs api | grep PhotoOfTheDay
# → "PhotoOfTheDayScheduler enabled — 15:00:00 Europe/Berlin"
```

**Keep `POTD_ENABLED=false` in `.env.stage`.** Stage and prod share ports and run from one compose
file; stage must never post to the family channel. The Admin button below still works on stage —
it deliberately ignores this flag, because a button press is not an accident.

Two further settings exist in `appsettings.json` and are rarely worth changing:
`Potd:SendAtLocal` (`15:00`) and `Potd:MinConfidence` (`0.5`, the tier-1 `person` floor — see §14).

---

## 5. Test it

Go to **`/admin`** → the **Photo of the Day** card. It holds your JWT already, so there is no curl
and no hand-copied token.

1. Leave the date blank (or type a `dd.mm` — the year is ignored, it looks at that calendar date
   across every year), keep **Dry run** ticked, press **Preview Pick**.
   You get the photo, the tier it came from, and the exact caption — without posting anything.
2. Untick **Dry run** and press **Send Now**. The photo should appear in the channel within a
   few seconds.

The same thing over HTTP, if you prefer:

```bash
curl -X POST http://192.168.0.192/api/admin/potd/send \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"month":8,"day":17,"dryRun":true}'
# → {"sent":false,"tier":"person","mediaFileId":"…","caption":"17 August 2019 · Berlin, Deutschland","skippedReason":"dryRun"}
```

`mediaFileId` is also accepted, which skips the pick and sends one exact photo — the quickest way
to confirm the HEIC→JPEG path works end to end.

The manual trigger differs from the 15:00 run in three deliberate ways: it ignores
`POTD_ENABLED`, it ignores the already-sent-today guard so you can run it repeatedly, and it
**does not write `potd_sends`** — a test at 11:00 will not make the real 15:00 run skip the day.

### When it doesn't work

The response's `skippedReason` names the problem; these are the ones worth recognising.

| `skippedReason` / symptom | Cause | Fix |
|---|---|---|
| `Potd:BotToken not configured` | Variable missing or blank in the env file | Section 4, then `--force-recreate api` |
| `chat not found` | Chat ID wrong | Negative `-100…` number for channels, not a username or user id |
| `bot was kicked` / `not enough rights` | Bot is not a channel admin | Section 2, step 3 |
| `No postable photo for dd.mm in any tier` | Nothing on that calendar date | Only possible on an empty library — on prod all 366 days have candidates |
| `Not an image (video/…)` | An explicit `mediaFileId` pointed at a video | `sendPhoto` takes a still; the automatic pick already excludes videos |
| Nothing at 15:00, no error | Scheduler never started | `docker compose logs api \| grep PhotoOfTheDay` — it logs `disabled` or the resolved timezone at startup |

---

## What it does

```
PhotoOfTheDayScheduler (Api)  — 15:00 in POTD_TIMEZONE, daily
  → claim the day in potd_sends (PK on sent_on = the idempotency guard)
  → pick one photo at random from today's calendar date across all years:
        tier 1  a `person` tag at ≥ 0.5 confidence
        tier 2  else, geotagged
        tier 3  else, any non-video
  → DisplayMediaRequest in-process (handles HEIC→JPEG + caching)
  → POST https://api.telegram.org/bot{TOKEN}/sendPhoto
```

The base set is the On This Day set minus what can't be posted: no deleted files, no memes, no
videos. The random choice happens **inside whichever tier matched**, so a geotagged photo never
competes with a person photo, and the tier is written to the log — a channel running on tier 3
most days would mean `person` coverage is worse than assumed.

Measured on prod 2026-08-17: every one of the 366 calendar days has at least 31 tier-1 candidates
(mean 281), so tiers 2 and 3 are insurance rather than everyday behaviour.

If the send fails, the claim row is released, so a restart before midnight retries the day rather
than silently skipping it.
