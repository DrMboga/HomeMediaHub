import os
import threading
from pathlib import Path

import torch
from PIL import Image, ImageFile, ImageOps
from ultralytics import YOLO
import open_clip

# Pillow refuses a JPEG with no ff-d9 end-of-image marker by policy, not by inability — it can
# decode what is there and leaves the missing tail grey. Ingest already accepts these files
# (MetadataService reads headers only), so without this the AI step is the one that fails and the
# photo sits in the gallery with no tags and no embedding: 9 of them after the prod import
# (docs/backlog.md bucket 3). A tagged photo with a grey strip beats an untagged one.
ImageFile.LOAD_TRUNCATED_IMAGES = True

# ── Models (loaded once at startup via load_models()) ───────────────────────

_yolo: YOLO | None = None
_clip_model = None
_clip_preprocess = None
_text_features = None  # pre-computed at startup — prompts never change
_tokenizer = None

_ORIGIN_LABELS = [
    "a real photograph taken with a camera, such as a landscape, object, or scene",
    "a screenshot of a computer or phone screen",
    "a photograph of a document, printed paper, form, passport, ID card, credit card, or driver's license",
    "a drawing, painting, illustration, or digital artwork",
    "a photograph taken to record information: a utility meter, a sign, a notice, a label, or text on a surface",
    "a portrait or group photograph where the main subject is one or more people",
]
_ORIGIN_CLASSES = ["photo", "screenshot", "document", "artwork", "reference", "people"]
_YOLO_CONFIDENCE_THRESHOLD = 0.30

# ViT-B/32's input is fixed at 224x224 by its patch grid and learned positional
# embeddings, so the only lever is how much of the frame reaches it — not detail.
_CLIP_INPUT = 224
# "center" = stock CLIP preprocess (resize shortest side, then centre-crop, which
# silently discards ~12% per side on 4:3 and ~30% total on 16:9 — a subject near the
# edge never reaches the model). "full" = whole frame squashed to the square input.
# "both" = mean of the two views, renormalised. Costs one extra image encode.
_CLIP_VIEWS = os.environ.get("AI_CLIP_VIEWS", "both").lower()

# Serialise all inference — PyTorch CPU inference is not thread-safe under
# concurrent load; the FastAPI /embed/text handler and the pika message loop
# run on different threads in the same process, so this lock is what makes
# that safe.
_inference_lock = threading.Semaphore(1)


class InferenceError(Exception):
    """Raised for any analyze() failure — missing file, unreadable image, model error."""


def load_models() -> None:
    """Idempotent — safe to call from both worker.py's main() and app.py's lifespan."""
    global _yolo, _clip_model, _clip_preprocess, _text_features, _tokenizer
    if _yolo is not None:
        return
    torch.set_num_threads(4)
    _yolo = YOLO("yolov8n.pt")
    _clip_model, _, _clip_preprocess = open_clip.create_model_and_transforms(
        "ViT-B-32", pretrained="openai"
    )
    _clip_model.eval()
    _tokenizer = open_clip.get_tokenizer("ViT-B-32")
    # Pre-compute text features once — reused for every analyze() call
    text_tokens = _tokenizer(_ORIGIN_LABELS)
    with torch.no_grad():
        tf = _clip_model.encode_text(text_tokens)
        _text_features = tf / tf.norm(dim=-1, keepdim=True)


def embed_text(text: str) -> list[float]:
    tokens = _tokenizer([text])
    with _inference_lock:
        with torch.no_grad():
            features = _clip_model.encode_text(tokens)
            features = features / features.norm(dim=-1, keepdim=True)
    return features.squeeze(0).tolist()


def analyze(file_path: str) -> dict:
    """Returns {tags, embedding, originClass, originConfidence}. Raises InferenceError on any failure."""
    path = Path(file_path)
    if not path.exists():
        raise InferenceError(f"File not found: {file_path}")

    # Open and immediately close the file handle; exif_transpose() returns a new object.
    try:
        with Image.open(path) as img:
            # PIL does not honour the EXIF Orientation tag. Without this, a portrait
            # photo that stores its rotation as metadata rather than in the pixels
            # reaches CLIP and YOLO lying on its side — and the centre crop lands in
            # the wrong place too. (The HEIC path is already upright: heif-convert
            # writes rotated pixels and the exiftool copy skips --Orientation.)
            pil_image = ImageOps.exif_transpose(img).convert("RGB")
    except Exception as e:
        raise InferenceError(f"Cannot open image: {e}") from e

    try:
        with _inference_lock:
            return _run_inference(pil_image)
    except Exception as e:
        raise InferenceError(f"Inference error: {e}") from e


def _encode_image(pil_image: Image.Image) -> torch.Tensor:
    """Unit-norm CLIP embedding of one image, per the _CLIP_VIEWS strategy."""
    views = []
    if _CLIP_VIEWS in ("center", "both"):
        views.append(_clip_preprocess(pil_image))
    if _CLIP_VIEWS in ("full", "both"):
        # Pre-squashed to the square input so preprocess's Resize/CenterCrop become
        # no-ops — this reuses its official ToTensor/Normalize rather than rebuilding it.
        views.append(_clip_preprocess(pil_image.resize((_CLIP_INPUT, _CLIP_INPUT), Image.BICUBIC)))
    if not views:
        raise InferenceError(f"Invalid AI_CLIP_VIEWS: {_CLIP_VIEWS!r} (expected center|full|both)")

    features = _clip_model.encode_image(torch.stack(views))
    features = features / features.norm(dim=-1, keepdim=True)
    # Mean of unit vectors, renormalised — the pooled view has to stay on the unit
    # sphere the text queries are compared against.
    pooled = features.mean(dim=0)
    return pooled / pooled.norm()


def _run_inference(pil_image: Image.Image) -> dict:
    # ── YOLOv8n object detection ─────────────────────────────────────────────
    # Fed the already-oriented PIL image, not the path — ultralytics' own loader
    # ignores EXIF orientation, and this avoids a second decode of the same file.
    yolo_results = _yolo(pil_image, verbose=False)[0]
    seen: dict[str, float] = {}
    for box in yolo_results.boxes:
        label = _yolo.names[int(box.cls[0])]
        conf = float(box.conf[0])
        if conf >= _YOLO_CONFIDENCE_THRESHOLD:
            seen[label] = max(seen.get(label, 0.0), conf)

    tags = [
        {"tag": label, "confidence": conf, "model": "yolov8n"}
        for label, conf in seen.items()
    ]

    # ── CLIP ViT-B/32 — embedding + origin classification ────────────────────
    with torch.no_grad():
        image_features = _encode_image(pil_image)          # 1-D, unit norm
        probs = (100.0 * image_features @ _text_features.T).softmax(dim=-1)

    embedding: list[float] = image_features.tolist()
    origin_idx = int(probs.argmax().item())

    return {
        "tags": tags,
        "embedding": embedding,
        "originClass": _ORIGIN_CLASSES[origin_idx],
        "originConfidence": float(probs[origin_idx]),
    }
