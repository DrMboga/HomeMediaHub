# Telegram Photo of the Day Setup

Workflow 3 runs daily at 15:00 and sends a photo to a Telegram channel. This guide covers creating the bot, creating the channel, and wiring them into n8n.

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

If your channel has a public username (e.g. `@familyphotos`), use that string directly as the chat ID in n8n.

---

## 4. Add credentials to n8n

Open n8n at `http://192.168.0.192:5678`.

1. Go to **Settings** → **Credentials** → **Add Credential**
2. Search for **Telegram**
3. Paste your bot token
4. Save as e.g. `Telegram Bot`

---

## 5. Configure Workflow 3

Open **W3 Photo of the Day** in n8n:

1. Click the **Telegram: Send Photo** node
2. Set **Credential** → select the credential you just created
3. Set **Chat ID** → enter the channel chat ID (e.g. `-100xxxxxxxxxx`)
4. Save the workflow

---

## 6. Test it

Trigger W3 manually to confirm it works before waiting for the 15:00 cron:

1. Open W3 in n8n
2. Click **Execute Workflow** (top right)
3. Check your Telegram channel — a photo should appear within a few seconds

If the execution fails, check the node output in n8n's execution detail view. Common issues:

| Problem | Cause | Fix |
|---------|-------|-----|
| `chat not found` | Chat ID wrong | Re-check chat ID format (negative number for channels) |
| `bot is not a member` | Bot not added to channel | Add bot as administrator |
| No photos in query result | DB empty or no photos with `taken_at` | Run Library Scanner first |
| `Forbidden: bot can't initiate conversation` | Bot tried to message a user, not a channel | Use channel chat ID, not a personal user ID |

---

## What W3 does

```
Daily cron 15:00
  → Postgres: SELECT photo with high-confidence tags or is_favorite = true
              ORDER BY md5(id || current_date)  ← deterministic daily pick
              LIMIT 1
  → Read thumbnail from disk
  → POST https://api.telegram.org/bot{TOKEN}/sendPhoto
```

The query excludes `origin_class = 'meme'` items. If no high-confidence or favorite photos exist, it falls back to a random photo from On This Day (same calendar date, any year).
