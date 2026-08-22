# -*- coding: utf-8 -*-
"""Builds the HomeMediaHub 'n8n -> RabbitMQ/MassTransit' deck."""
from pptx import Presentation
from pptx.util import Inches, Pt
from pptx.dml.color import RGBColor
from pptx.enum.text import PP_ALIGN, MSO_ANCHOR
from pptx.enum.shapes import MSO_SHAPE, MSO_CONNECTOR
from pptx.oxml.ns import qn

OUT = r"C:\Users\Mike\Projects\HomeMediaHub\docs\presentation\HomeMediaHub-n8n-to-MassTransit.pptx"

FONT = "Segoe UI"
MONO = "Consolas"

INK = RGBColor(0x1A, 0x22, 0x2C)
MUTED = RGBColor(0x5C, 0x66, 0x73)
FAINT = RGBColor(0x94, 0x9D, 0xA8)
WHITE = RGBColor(0xFF, 0xFF, 0xFF)
RULE = RGBColor(0xDD, 0xE1, 0xE6)
PANEL = RGBColor(0xF4, 0xF5, 0xF7)

ORANGE = RGBColor(0xC2, 0x41, 0x0C)
ORANGE_L = RGBColor(0xFD, 0xEE, 0xE3)
TEAL = RGBColor(0x0F, 0x76, 0x6E)
TEAL_L = RGBColor(0xE1, 0xF0, 0xEE)
BLUE = RGBColor(0x1E, 0x4E, 0x79)
BLUE_L = RGBColor(0xE7, 0xEE, 0xF6)
DARK = RGBColor(0x11, 0x1A, 0x22)

prs = Presentation()
prs.slide_width = Inches(13.333)
prs.slide_height = Inches(7.5)
BLANK = prs.slide_layouts[6]


def slide(bg=WHITE):
    s = prs.slides.add_slide(BLANK)
    bgshape = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, 0, 0, prs.slide_width, prs.slide_height)
    bgshape.fill.solid()
    bgshape.fill.fore_color.rgb = bg
    bgshape.line.fill.background()
    bgshape.shadow.inherit = False
    return s


def notes(s, text):
    s.notes_slide.notes_text_frame.text = text


def tbox(s, x, y, w, h):
    tb = s.shapes.add_textbox(Inches(x), Inches(y), Inches(w), Inches(h))
    tf = tb.text_frame
    tf.word_wrap = True
    tf.margin_left = tf.margin_right = tf.margin_top = tf.margin_bottom = 0
    return tf


def para(tf, text, size=14, bold=False, color=INK, before=0, after=6,
         align=PP_ALIGN.LEFT, font=FONT, italic=False, first=False, spacing=None):
    p = tf.paragraphs[0] if first else tf.add_paragraph()
    p.alignment = align
    p.space_before = Pt(before)
    p.space_after = Pt(after)
    if spacing:
        p.line_spacing = spacing
    r = p.add_run()
    r.text = text
    r.font.size = Pt(size)
    r.font.bold = bold
    r.font.italic = italic
    r.font.color.rgb = color
    r.font.name = font
    return p


def heading(s, title, kicker=None, accent=INK):
    if kicker:
        tf = tbox(s, 0.62, 0.34, 11.5, 0.3)
        para(tf, kicker.upper(), size=11, bold=True, color=accent, after=0, first=True)
        tf2 = tbox(s, 0.62, 0.62, 12.1, 0.6)
        para(tf2, title, size=27, bold=True, color=INK, after=0, first=True)
        ry = 1.30
    else:
        tf2 = tbox(s, 0.62, 0.44, 12.1, 0.7)
        para(tf2, title, size=28, bold=True, color=INK, after=0, first=True)
        ry = 1.22
    ln = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.62), Inches(ry), Inches(12.1), Pt(1.6))
    ln.fill.solid()
    ln.fill.fore_color.rgb = RULE
    ln.line.fill.background()
    ln.shadow.inherit = False
    return ry


def box(s, x, y, w, h, title, body=None, fill=PANEL, line=RULE, tcolor=INK,
        bcolor=MUTED, tsize=12.5, bsize=10.5, rounded=True, tbold=True,
        align=PP_ALIGN.CENTER, mono_body=False, anchor=MSO_ANCHOR.MIDDLE):
    shp = s.shapes.add_shape(
        MSO_SHAPE.ROUNDED_RECTANGLE if rounded else MSO_SHAPE.RECTANGLE,
        Inches(x), Inches(y), Inches(w), Inches(h))
    if rounded:
        shp.adjustments[0] = 0.10
    shp.fill.solid()
    shp.fill.fore_color.rgb = fill
    shp.line.color.rgb = line
    shp.line.width = Pt(1.1)
    shp.shadow.inherit = False
    tf = shp.text_frame
    tf.word_wrap = True
    tf.vertical_anchor = anchor
    tf.margin_left = tf.margin_right = Inches(0.10)
    tf.margin_top = tf.margin_bottom = Inches(0.06)
    para(tf, title, size=tsize, bold=tbold, color=tcolor, align=align, after=0, first=True)
    if body:
        for i, line_ in enumerate(body if isinstance(body, list) else [body]):
            para(tf, line_, size=bsize, color=bcolor, align=align,
                 before=3 if i == 0 else 1, after=0,
                 font=MONO if mono_body else FONT)
    return shp


def arrow(s, x1, y1, x2, y2, color=FAINT, width=1.4, dashed=False):
    c = s.shapes.add_connector(MSO_CONNECTOR.STRAIGHT,
                               Inches(x1), Inches(y1), Inches(x2), Inches(y2))
    c.line.color.rgb = color
    c.line.width = Pt(width)
    ln = c.line._get_or_add_ln()
    if dashed:
        ln.append(ln.makeelement(qn('a:prstDash'), {'val': 'dash'}))
    ln.append(ln.makeelement(qn('a:tailEnd'), {'type': 'triangle', 'w': 'med', 'len': 'med'}))
    return c


def label(s, x, y, w, text, size=9.5, color=FAINT, align=PP_ALIGN.CENTER, bold=False):
    tf = tbox(s, x, y, w, 0.22)
    para(tf, text, size=size, color=color, align=align, bold=bold, after=0, first=True)


def bullets(s, x, y, w, items, size=14, gap=9, color=INK, bullet_color=None):
    tf = tbox(s, x, y, w, 0.4)
    for i, it in enumerate(items):
        if isinstance(it, tuple):
            head, rest = it
            p = tf.paragraphs[0] if i == 0 else tf.add_paragraph()
            p.space_before = Pt(0 if i == 0 else gap)
            p.space_after = Pt(0)
            r = p.add_run(); r.text = "\u2014 "
            r.font.size = Pt(size); r.font.color.rgb = bullet_color or FAINT; r.font.name = FONT
            r = p.add_run(); r.text = head
            r.font.size = Pt(size); r.font.bold = True; r.font.color.rgb = color; r.font.name = FONT
            r = p.add_run(); r.text = rest
            r.font.size = Pt(size); r.font.color.rgb = MUTED; r.font.name = FONT
        else:
            para(tf, "\u2014 " + it, size=size, color=MUTED, before=0 if i == 0 else gap,
                 after=0, first=(i == 0))
    return tf


def stat(s, x, y, w, h, value, caption, accent=TEAL, fill=PANEL):
    return box(s, x, y, w, h, value, [caption], fill=fill, line=RULE,
               tcolor=accent, bcolor=MUTED, tsize=23, bsize=10.5)


# ------------------------------------------------------------------ slide 1
s = slide(DARK)
bar = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.9), Inches(2.35), Inches(1.5), Pt(4))
bar.fill.solid(); bar.fill.fore_color.rgb = TEAL
bar.line.fill.background(); bar.shadow.inherit = False

tf = tbox(s, 0.9, 2.75, 11.6, 2.2)
para(tf, "From n8n to a message-driven pipeline", size=44, bold=True, color=WHITE, after=10, first=True)
para(tf, "Rebuilding a 190,000-photo home media library on RabbitMQ + MassTransit",
     size=19, color=RGBColor(0x9F, 0xB4, 0xB2), after=0)

tf = tbox(s, 0.9, 5.55, 11.6, 0.9)
para(tf, "HomeMediaHub  \u00b7  self-hosted, single box, LAN-only", size=13,
     color=RGBColor(0x6E, 0x7C, 0x88), after=4, first=True)
para(tf, "Michael Shabanov  \u00b7  August 2026", size=13, color=RGBColor(0x6E, 0x7C, 0x88), after=0)
notes(s, "One-line pitch: we started with a low-code orchestrator, hit a wall at scale, and "
         "replaced it with a broker plus typed consumers. This talk is what broke and what we got back.")

# ------------------------------------------------------------------ slide 2
s = slide()
heading(s, "What the system actually does", kicker="Context", accent=BLUE)
bullets(s, 0.62, 1.62, 6.3, [
    ("iPhone photos land automatically. ", "Immich is used as a backup endpoint only \u2014 it writes originals to disk, no Immich UI."),
    ("Everything gets ingested. ", "Hash and dedup, HEIC to JPEG, video frame extraction, EXIF, 400 px WebP thumbnails."),
    ("Everything gets enriched. ", "YOLOv8n object tags and CLIP embeddings for semantic search, reverse-geocoded addresses."),
    ("A React frontend on top. ", "Gallery, semantic search, tag facets, map, albums, On This Day, a daily Telegram post."),
], size=14, gap=13)

box(s, 0.62, 5.35, 6.3, 1.5,
    "One Dell Precision 3620 in a cupboard",
    ["i7-6700 (4c/8t)  \u00b7  32 GB RAM  \u00b7  2 TB SATA HDD  \u00b7  Ubuntu Server 24.04",
     "Docker Compose  \u00b7  no cloud  \u00b7  no public exposure  \u00b7  all AI local and CPU-only"],
    fill=BLUE_L, line=BLUE_L, tcolor=BLUE, bcolor=MUTED, tsize=13, bsize=11, align=PP_ALIGN.LEFT)

sx, sw, sg = 7.25, 2.62, 0.24
stat(s, sx, 1.62, sw, 1.35, "188,416", "media files processed", accent=BLUE)
stat(s, sx + sw + sg, 1.62, sw, 1.35, "750 GB", "on a single spindle", accent=BLUE)
stat(s, sx, 3.15, sw, 1.35, "~190K", "AI inferences, CPU-only", accent=BLUE)
stat(s, sx + sw + sg, 3.15, sw, 1.35, "0", "jobs left failed", accent=BLUE)
box(s, sx, 4.68, sw * 2 + sg, 2.17,
    "Why it is interesting",
    ["Everything about this workload is bursty and slow: 2\u20135 s of CPU per image, one disk, "
     "one broker \u2014 and a scan that can enqueue 190,000 units of work in a couple of minutes.",
     "",
     "That gap between how fast you can enqueue and how fast you can consume is the whole "
     "story of this talk."],
    fill=PANEL, line=RULE, tcolor=INK, bcolor=MUTED, tsize=13, bsize=11.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
notes(s, "Set up the scale: 190K files, one CPU box, AI at seconds per image. "
         "The enqueue/consume gap is what n8n could not express.")

# ------------------------------------------------------------------ slide 3
s = slide()
heading(s, "Architecture v1 \u2014 n8n as the orchestration hub", kicker="How it was", accent=ORANGE)

box(s, 0.62, 1.68, 2.5, 0.82, "Immich :2283", ["iPhone backup endpoint"], fill=PANEL, tcolor=INK, tsize=12)
box(s, 0.62, 2.72, 2.5, 0.82, "Samba share", ["archive drag and drop"], fill=PANEL, tcolor=INK, tsize=12)
box(s, 0.62, 4.05, 2.5, 1.0, "/mnt/storage/Photo", ["single source of truth", "~190K files"],
    fill=BLUE_L, line=BLUE_L, tcolor=BLUE, tsize=12)
arrow(s, 1.87, 2.50, 1.87, 2.72)
arrow(s, 1.87, 3.54, 1.87, 4.05)

box(s, 3.95, 1.68, 4.15, 4.28, "n8n :5678", None,
    fill=ORANGE_L, line=ORANGE, tcolor=ORANGE, tsize=17, anchor=MSO_ANCHOR.TOP)
label(s, 3.95, 2.12, 4.15, "ORCHESTRATION  HUB", size=10.5, color=ORANGE, bold=True)
wf = [("Workflow 1", "Process Single File \u2014 ~30 nodes"),
      ("Workflow 2", "Library Scanner \u2014 cron / manual"),
      ("Workflow 3", "Photo of the Day \u2014 daily 15:00"),
      ("Workflow 4", "Error Handler \u2014 global trigger")]
for i, (a, b) in enumerate(wf):
    box(s, 4.15, 2.52 + i * 0.78, 3.75, 0.66, a, [b], fill=WHITE,
        line=RGBColor(0xE8, 0xC9, 0xB6), tcolor=ORANGE, bcolor=MUTED, tsize=11.5, bsize=9.5)
label(s, 3.95, 5.62, 4.15, "workflow JSON + execution history  \u2192  n8n_db", size=9.5, color=ORANGE)
arrow(s, 3.12, 4.55, 3.95, 4.10, color=ORANGE)
label(s, 2.90, 4.62, 1.2, "scan", size=9, color=ORANGE)

rx, rw = 9.05, 3.68
rows = [
    (1.68, 1.12, ".NET API :8080", ["/api/tools/hash \u00b7 metadata \u00b7 thumbnail",
                                    "convert \u00b7 cleanup \u00b7 new-id \u00b7 scan"], True),
    (2.94, 0.88, "AI Sidecar :8000", ["POST /analyze \u2014 YOLOv8n + CLIP"], False),
    (3.96, 0.72, "GeoService :8001", ["GET /reverse \u2014 Nominatim cache"], False),
    (4.82, 0.72, "PostgreSQL :5432", ["direct SQL nodes, inline in the flow"], False),
    (5.62, 0.62, "Telegram Bot API", None, False),
]
for y, h, t, b, mono in rows:
    box(s, rx, y, rw, h, t, b, fill=PANEL, tcolor=INK, tsize=12, bsize=9.5, mono_body=mono)
    arrow(s, 8.10, min(y + h / 2, 5.65), rx, y + h / 2, color=FAINT)
label(s, 8.05, 3.14, 1.0, "HTTP", size=9, color=FAINT)

box(s, 0.62, 6.48, 12.1, 0.72,
    "Every file was one synchronous HTTP chain. n8n held the queue, the retries, the concurrency "
    "and the state \u2014 inside its own engine.",
    None, fill=ORANGE_L, line=ORANGE_L, tcolor=ORANGE, tsize=13, align=PP_ALIGN.CENTER)
notes(s, "n8n was doing four jobs at once: scheduler, queue, state machine and glue. "
         "The .NET API existed mostly as a set of tool endpoints n8n could call.")

# ------------------------------------------------------------------ slide 4
s = slide()
heading(s, "One file = one workflow run, start to finish", kicker="How it was", accent=ORANGE)

steps = [
    ("1 \u00b7 Webhook", "one call per file"),
    ("2 \u00b7 SQL", "INSERT processing_job"),
    ("3 \u00b7 HTTP", "/api/tools/hash"),
    ("4 \u00b7 SQL", "dedup by file_hash"),
    ("5 \u00b7 IF heic", "/api/tools/convert"),
    ("6 \u00b7 IF video", "ffmpeg frame grab"),
    ("7 \u00b7 HTTP", "/api/tools/metadata"),
    ("8 \u00b7 HTTP", "/api/tools/thumbnail"),
    ("9 \u00b7 HTTP", "geo /reverse"),
    ("10 \u00b7 SQL", "INSERT media rows"),
    ("11 \u00b7 HTTP", "/analyze  \u2190  2\u20135 s"),
    ("12 \u00b7 SQL", "tags + embedding, done"),
]
bw, bh, gap = 1.86, 0.84, 0.17
x0 = 0.62
for i, (t, b) in enumerate(steps):
    r, c = divmod(i, 6)
    x = x0 + c * (bw + gap)
    y = 1.72 + r * 1.30
    hot = (i == 10)
    box(s, x, y, bw, bh, t, [b],
        fill=ORANGE_L if hot else PANEL, line=ORANGE if hot else RULE,
        tcolor=ORANGE if hot else INK, bcolor=ORANGE if hot else MUTED,
        tsize=11.5, bsize=9.5)
    if c < 5:
        arrow(s, x + bw, y + bh / 2, x + bw + gap, y + bh / 2)

box(s, 0.62, 4.35, 5.95, 1.05, "The photo did not exist until node 12",
    ["Nothing appeared in the gallery until the AI call came back. On a CPU-only box that "
     "is 2\u20135 s per image \u2014 days for the archive."],
    fill=PANEL, line=RULE, tcolor=INK, bcolor=MUTED, tsize=13, bsize=11, align=PP_ALIGN.LEFT)
box(s, 6.77, 4.35, 5.95, 1.05, "A failure was a red row in a list",
    ["No queue to inspect, no depth to watch, no dead-letter. Retry meant re-firing the "
     "webhook by hand and hoping n8n was up."],
    fill=PANEL, line=RULE, tcolor=INK, bcolor=MUTED, tsize=13, bsize=11, align=PP_ALIGN.LEFT)

label(s, 0.62, 5.75, 12.1,
      "~30 nodes  \u00b7  8 HTTP round-trips per file  \u00b7  1 execution record per file  \u00b7  "
      "concurrency capped at 4 by hand",
      size=13, color=ORANGE, bold=True)
notes(s, "Walk the chain quickly. The two boxes at the bottom are the punchline: latency to "
         "first visibility, and no operational surface when it broke.")

# ------------------------------------------------------------------ slide 5
s = slide()
heading(s, "Where n8n broke down", kicker="The problems", accent=ORANGE)

cards = [
    ("The sandbox blocked the actual work",
     "Code nodes ran in n8n's task runner: no require(), no child_process, no crypto global, and "
     "the Execute Command node was unavailable. We could not shell out to heif-convert or ffmpeg."),
    ("So we wrote endpoints to escape it",
     "/api/tools/convert and /cleanup \u2014 and even /new-id, an HTTP call whose only job was to "
     "generate a UUID, because crypto.randomUUID() was blocked. The API grew to serve the orchestrator."),
    ("No backpressure, anywhere",
     "The scanner fanned out as fast as it could and buried the API and the AI sidecar. The fix "
     "was an n8n knob: max 4 parallel executions. One global number for every stage."),
    ("Load broke things underneath it",
     "Sustained runs left Npgsql connections stale through the Docker bridge, timed out through "
     "nginx, and needed retry policies bolted on \u2014 all symptoms of unmanaged flow."),
    ("Error handling lost the data",
     "continueOnFail does not preserve the original item, so the summary node had to "
     "index-correlate outputs back to inputs just to recover which file had failed."),
    ("The logic was not code",
     "Orchestration lived as JSON inside n8n's own database: no compile step, no unit tests, no "
     "reviewable diff, no way to run it locally. The pipeline could not be tested at all."),
]
cw, ch, gx, gy = 3.88, 1.88, 0.24, 0.30
for i, (t, b) in enumerate(cards):
    r, c = divmod(i, 3)
    box(s, 0.62 + c * (cw + gx), 1.58 + r * (ch + gy), cw, ch, t, [b],
        fill=WHITE, line=ORANGE if r == 0 else RULE, tcolor=ORANGE, bcolor=MUTED,
        tsize=13, bsize=11, align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)

box(s, 0.62, 5.90, 12.1, 1.05,
    "None of these were bugs in n8n — they were a workload it was never shaped for.",
    ["The real cost was not any single one of them. It was that orchestration had become the "
     "thing we maintained, and we still could not answer the only question that mattered "
     "during an import: how much work is left?"],
    fill=ORANGE_L, line=ORANGE_L, tcolor=ORANGE, bcolor=MUTED, tsize=13.5, bsize=11.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
notes(s, "Be fair here: n8n is a fine glue tool. The problem was using it as a high-throughput "
         "work queue in front of CPU-bound consumers.")

# ------------------------------------------------------------------ slide 6
s = slide()
heading(s, "The git log tells the story", kicker="The problems", accent=ORANGE)
label(s, 0.62, 1.48, 12.1, "Every one of these commits exists only because of the orchestrator.",
      size=13, color=MUTED, align=PP_ALIGN.LEFT)

commits = [
    ("feat(n8n): fix W1 workflow sandbox compatibility",
     "require('crypto') is blocked \u2014 so we added a server-side endpoint just to mint UUIDs"),
    ("feat(api+n8n): move shell commands to API tool endpoints",
     "the task-runner sandbox blocks child_process and executeCommand entirely"),
    ("fix(n8n): limit W2 concurrency to 4 parallel W1 executions",
     "the only backpressure control available was one global number"),
    ("chore: nginx n8n timeouts added",
     "long synchronous chains outlived the reverse proxy's patience"),
    ("fix(infra+geo+n8n): prevent stale Npgsql connections and fix W2 error reporting",
     "conntrack dropped idle connections under load; continueOnFail lost the file path"),
    ("feat(api+n8n): add W4 Error Handler and record-failure endpoint",
     "a whole extra workflow, plus an API endpoint, just to find out that something failed"),
]
y = 1.92
for c, why in commits:
    bar = s.shapes.add_shape(MSO_SHAPE.RECTANGLE, Inches(0.62), Inches(y), Pt(3.2), Inches(0.72))
    bar.fill.solid(); bar.fill.fore_color.rgb = ORANGE
    bar.line.fill.background(); bar.shadow.inherit = False
    tf = tbox(s, 0.90, y + 0.03, 11.8, 0.7)
    para(tf, c, size=12.5, color=ORANGE, font=MONO, after=2, first=True)
    para(tf, why, size=11.5, color=MUTED, after=0)
    y += 0.78

box(s, 0.62, 6.62, 12.1, 0.62,
    "Months of fighting the tool \u2014 and the pipeline still could not tell us how much work was left.",
    None, fill=ORANGE_L, line=ORANGE_L, tcolor=ORANGE, tsize=13)
notes(s, "This is the credibility slide \u2014 these are real commit subjects from the repository.")

# ------------------------------------------------------------------ slide 7
s = slide()
heading(s, "Architecture v2 \u2014 RabbitMQ + MassTransit", kicker="How it is now", accent=TEAL)

box(s, 0.62, 1.72, 3.05, 1.5, ".NET 10 API :8080",
    ["queries \u00b7 JWT auth \u00b7 admin", "publishes ScanRequested",
     "publish-only bus, no consumers"],
    fill=BLUE_L, line=BLUE_L, tcolor=BLUE, bcolor=MUTED, tsize=13, bsize=10)
box(s, 0.62, 3.42, 3.05, 1.22, "React frontend",
    ["gallery, search, /admin", "scan trigger + job retry"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10)
box(s, 0.62, 4.86, 3.05, 1.22, "Prometheus + Grafana",
    ["queue depth, consume rate", "failed-job gauge"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10)

box(s, 4.05, 1.72, 3.15, 4.36, "RabbitMQ 4", None,
    fill=TEAL_L, line=TEAL, tcolor=TEAL, tsize=17, anchor=MSO_ANCHOR.TOP)
label(s, 4.05, 2.16, 3.15, "DURABLE  QUEUES", size=10.5, color=TEAL, bold=True)
queues = [("media-scan", "batching"), ("media-ingest", "prefetch 2"),
          ("media-enrichment-saga", "state"), ("ai-analysis", "prefetch 1"),
          ("geo-lookup", "prefetch 1"), ("apply-enrichment", "idempotent")]
for i, (q, p_) in enumerate(queues):
    box(s, 4.22, 2.55 + i * 0.57, 2.12, 0.47, q, None, fill=WHITE,
        line=RGBColor(0xBF, 0xDA, 0xD7), tcolor=TEAL, tsize=10)
    label(s, 6.40, 2.67 + i * 0.57, 0.78, p_, size=8, color=FAINT, align=PP_ALIGN.LEFT)

box(s, 8.35, 1.72, 4.37, 2.30, "MediaWorker (.NET 10 Worker Service)",
    ["ScanConsumer \u00b7 IngestConsumer",
     "EnrichmentStateMachine (saga)",
     "AiResultBridgeConsumer",
     "ApplyEnrichmentConsumer \u00b7 JobFailureConsumer",
     "hash \u00b7 HEIC \u00b7 ffmpeg \u00b7 EXIF \u00b7 thumbnails"],
    fill=TEAL_L, line=TEAL, tcolor=TEAL, bcolor=MUTED, tsize=13, bsize=10.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
box(s, 8.35, 4.20, 4.37, 0.92, "AI Sidecar (Python 3.11)",
    ["pika consumer, prefetch 1 \u00b7 YOLOv8n + CLIP \u00b7 always ACKs"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10.5, align=PP_ALIGN.LEFT)
box(s, 8.35, 5.30, 4.37, 0.78, "GeoService (.NET 10)",
    ["MassTransit consumer at 1 msg/s \u2014 Nominatim's limit"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10.5, align=PP_ALIGN.LEFT)

arrow(s, 3.67, 2.47, 4.05, 2.47, color=TEAL)
for yy in (2.87, 4.66, 5.69):
    arrow(s, 7.20, yy, 8.35, yy, color=TEAL)

box(s, 0.62, 6.42, 12.1, 0.7,
    "Every stage is its own durable queue with its own prefetch. Backpressure is a property of "
    "the topology, not a knob.",
    None, fill=TEAL_L, line=TEAL_L, tcolor=TEAL, tsize=13)
notes(s, "The API is publish-only. All processing moved into a worker service. The AWS analogue: "
         "queues are SQS/SNS, the saga is Step Functions, consumers are Lambda.")

# ------------------------------------------------------------------ slide 8
s = slide()
heading(s, "Save early, enrich later", kicker="How it is now", accent=TEAL)

box(s, 0.62, 1.62, 3.7, 0.95, "Scan requested",
    ["POST /api/admin/scan  \u00b7  or the monthly scheduler"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10.5)
box(s, 4.82, 1.62, 3.7, 0.95, "ScanConsumer",
    ["enumerate, skip known files, insert job rows in batches"],
    fill=PANEL, tcolor=INK, tsize=13, bsize=10.5)
box(s, 9.02, 1.62, 3.7, 0.95, "IngestConsumer   (prefetch 2)",
    ["hash \u2192 dedup \u2192 convert \u2192 EXIF \u2192 thumbnail"],
    fill=TEAL_L, line=TEAL, tcolor=TEAL, bcolor=MUTED, tsize=13, bsize=10.5)
arrow(s, 4.32, 2.09, 4.82, 2.09, color=TEAL)
arrow(s, 8.52, 2.09, 9.02, 2.09, color=TEAL)

box(s, 6.30, 2.85, 6.42, 0.62,
    "media row saved  \u2192  the photo is in the gallery, seconds after ingest",
    None, fill=TEAL, line=TEAL, tcolor=WHITE, tsize=13)
arrow(s, 10.87, 2.57, 10.87, 2.85, color=TEAL)

box(s, 4.35, 3.78, 4.6, 0.8, "EnrichmentStateMachine (saga)",
    ["one row per file \u2014 fan out, then wait for both"],
    fill=TEAL_L, line=TEAL, tcolor=TEAL, bcolor=MUTED, tsize=13, bsize=10.5)
arrow(s, 9.51, 3.47, 6.65, 3.78, color=TEAL)

box(s, 2.20, 4.92, 4.0, 0.85, "AI Sidecar \u2014 tags + embedding",
    ["prefetch 1, single-flight, never dead-letters"],
    fill=PANEL, tcolor=INK, tsize=12.5, bsize=10)
box(s, 7.10, 4.92, 4.0, 0.85, "GeoService \u2014 reverse geocode",
    ["skipped entirely when the file has no GPS"],
    fill=PANEL, tcolor=INK, tsize=12.5, bsize=10)
arrow(s, 6.10, 4.58, 4.20, 4.92, color=TEAL)
arrow(s, 7.20, 4.58, 9.10, 4.92, color=TEAL)

box(s, 4.05, 6.02, 5.2, 0.8, "ApplyEnrichmentConsumer",
    ["idempotent upsert of tags, embedding and address \u2192 job completed"],
    fill=TEAL_L, line=TEAL, tcolor=TEAL, bcolor=MUTED, tsize=13, bsize=10.5)
arrow(s, 4.20, 5.77, 6.10, 6.02, color=TEAL)
arrow(s, 9.10, 5.77, 7.20, 6.02, color=TEAL)

tf = tbox(s, 0.62, 4.95, 1.4, 1.7)
para(tf, "the slow half", size=12, bold=True, color=MUTED, after=4, first=True)
para(tf, "the AI pass over the archive took days \u2014 and that was fine, because nothing was "
         "waiting on it", size=10, color=FAINT, after=0)
notes(s, "The key architectural move. Ingest is I/O-bound and fast; enrichment is CPU-bound and "
         "slow. Decoupling them meant the library was browsable on day one.")

# ------------------------------------------------------------------ slide 9
s = slide()
heading(s, "Concept by concept", kicker="The trade", accent=TEAL)

data = [
    ("", "n8n", "RabbitMQ + MassTransit"),
    ("Orchestration logic", "workflow JSON inside n8n's database", "C# consumers and a saga, in git, code-reviewed"),
    ("The work queue", "hidden inside the engine, not inspectable", "six durable queues, visible in the management UI"),
    ("Concurrency control", "one global 'max 4 parallel executions'", "per-queue prefetch: ingest 2, AI 1, geo 1"),
    ("Shell work", "blocked by the sandbox, so: HTTP endpoints", "runs natively inside the worker process"),
    ("Retry", "re-fire the webhook; lost if n8n is down", "redelivery from a durable queue; retry = republish"),
    ("Failure visibility", "a red row in the execution list", "job ledger + a Prometheus gauge + Grafana panels"),
    ("Testability", "none \u2014 the logic was not code", "128 API + 60 worker unit tests, green on every build"),
    ("Time to first pixel", "after the AI call, for every file", "seconds \u2014 save early, enrich later"),
]
tbl = s.shapes.add_table(len(data), 3, Inches(0.62), Inches(1.55),
                         Inches(12.1), Inches(0.4 * len(data))).table
tbl.columns[0].width = Inches(2.75)
tbl.columns[1].width = Inches(4.35)
tbl.columns[2].width = Inches(5.0)
for r, row in enumerate(data):
    tbl.rows[r].height = Inches(0.5 if r == 0 else 0.55)
    for c, txt in enumerate(row):
        cell = tbl.cell(r, c)
        cell.margin_left = Inches(0.14); cell.margin_right = Inches(0.10)
        cell.margin_top = Inches(0.05); cell.margin_bottom = Inches(0.05)
        cell.vertical_anchor = MSO_ANCHOR.MIDDLE
        cell.fill.solid()
        cell.fill.fore_color.rgb = WHITE if (r == 0 or r % 2 == 0) else PANEL
        tf = cell.text_frame
        tf.word_wrap = True
        p = tf.paragraphs[0]
        p.space_before = Pt(0); p.space_after = Pt(0)
        run = p.add_run(); run.text = txt
        run.font.name = FONT
        if r == 0:
            run.font.size = Pt(12.5); run.font.bold = True
            run.font.color.rgb = ORANGE if c == 1 else (TEAL if c == 2 else INK)
        else:
            run.font.size = Pt(11.5)
            run.font.bold = (c == 0)
            run.font.color.rgb = INK if c == 0 else (MUTED if c == 1 else TEAL)
box(s, 0.62, 6.55, 12.1, 0.62,
    "What we gave up: a UI that anyone could edit without a deploy. Changing the pipeline now "
    "means shipping code — and that turned out to be the point.",
    None, fill=PANEL, line=RULE, tcolor=MUTED, tsize=13, tbold=False)
notes(s, "The right-hand column is what we bought. The left column is also what we gave up: "
         "a UI anyone could edit without a deploy.")

# ------------------------------------------------------------------ slide 10
s = slide()
heading(s, "What we got", kicker="Results, measured on the real library", accent=TEAL)

sw, sg = 2.83, 0.26
row1 = [("188,416", "files completed"), ("0", "jobs left failed"),
        ("100%", "enrichment coverage"), ("992", "duplicates skipped")]
for i, (v, c) in enumerate(row1):
    stat(s, 0.62 + i * (sw + sg), 1.60, sw, 1.3, v, c, accent=TEAL)
row2 = [("119,298", "addresses resolved \u2014 100% of GPS rows"),
        ("146,767", "files carrying at least one AI tag"),
        ("354 ms", "semantic search over 188K vectors"),
        ("~200", "automated tests across four services")]
for i, (v, c) in enumerate(row2):
    stat(s, 0.62 + i * (sw + sg), 3.06, sw, 1.3, v, c, accent=TEAL)

box(s, 0.62, 4.60, 5.95, 2.15, "The archive import actually finished",
    ["The full 190K-file import drained through the new pipeline, with the gallery usable the "
     "whole time it ran.",
     "",
     "171 files failed \u2014 and for the first time we could see them, list them and triage them. "
     "125 turned out to be our own bugs (a hardcoded one-second video seek, truncated JPEGs); "
     "all 125 came back after the fix."],
    fill=PANEL, line=RULE, tcolor=INK, bcolor=MUTED, tsize=14, bsize=11.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
box(s, 6.77, 4.60, 5.95, 2.15, "Operations became boring",
    ["Queue depth is the progress bar. A multi-day AI pass is something you watch on a Grafana "
     "panel, not something you babysit.",
     "",
     "Worth naming honestly: handled per-file failures never fault a consumer, so the MassTransit "
     "error panels stay flat. We export a failed-jobs gauge instead."],
    fill=PANEL, line=RULE, tcolor=INK, bcolor=MUTED, tsize=14, bsize=11.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
notes(s, "Mention the honest part: we introduced our own blind spot and had to add a dedicated "
         "metric for it. Observability is a design decision, not a checkbox.")

# ------------------------------------------------------------------ slide 11
s = slide()
heading(s, "Would I use n8n again?  Yes \u2014 for different work.", kicker="Takeaways", accent=BLUE)

items = [
    ("It was the right call to start with. ",
     "A working pipeline in days, no infrastructure to run, and it showed us exactly which stages were slow."),
    ("It stopped being right when throughput started to matter. ",
     "Once the orchestrator needed per-stage backpressure, durable retries and tests, it had become the product rather than the glue."),
    ("The dividing line is who owns the work. ",
     "n8n for low-volume glue between SaaS APIs, where a human wants to edit the flow. A broker for high-volume work you own and must not lose."),
    ("Decouple fast producers from slow consumers, always. ",
     "Save early, enrich later was worth more than the broker itself \u2014 it turned a multi-day blocking import into a background detail."),
    ("Make the queue visible before you need it. ",
     "Every operational question we could not answer under n8n \u2014 how much is left, what failed, what is stuck \u2014 is now a panel."),
]
bullets(s, 0.62, 1.62, 12.1, items, size=15, gap=16)

box(s, 0.62, 5.60, 12.1, 1.08, "The AWS analogue, for anyone mapping this onto cloud services",
    ["RabbitMQ exchanges and queues \u2248 SNS + SQS   \u00b7   MassTransit saga \u2248 Step Functions   "
     "\u00b7   consumers \u2248 Lambda with a concurrency limit",
     "Same shapes, same failure modes \u2014 running on one second-hand workstation in a cupboard."],
    fill=BLUE_L, line=BLUE_L, tcolor=BLUE, bcolor=MUTED, tsize=13.5, bsize=11.5,
    align=PP_ALIGN.LEFT, anchor=MSO_ANCHOR.TOP)
notes(s, "Close on the fair version: this is not 'n8n bad'. It is a story about matching the "
         "orchestration tool to the shape of the work.")

prs.save(OUT)
print("saved:", OUT)
print("slides:", len(prs.slides._sldIdLst))
