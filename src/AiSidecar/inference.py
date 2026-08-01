import threading
from pathlib import Path

import torch
from PIL import Image
from ultralytics import YOLO
import open_clip

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

    # Open and immediately close the file handle; convert() returns a new object
    try:
        img = Image.open(path)
        pil_image = img.convert("RGB")
        img.close()
    except Exception as e:
        raise InferenceError(f"Cannot open image: {e}") from e

    try:
        with _inference_lock:
            return _run_inference(path, pil_image)
    except Exception as e:
        raise InferenceError(f"Inference error: {e}") from e


def _run_inference(path: Path, pil_image: Image.Image) -> dict:
    # ── YOLOv8n object detection ─────────────────────────────────────────────
    yolo_results = _yolo(str(path), verbose=False)[0]
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
    image_tensor = _clip_preprocess(pil_image).unsqueeze(0)
    with torch.no_grad():
        image_features = _clip_model.encode_image(image_tensor)
        image_features = image_features / image_features.norm(dim=-1, keepdim=True)
        probs = (100.0 * image_features @ _text_features.T).softmax(dim=-1)

    embedding: list[float] = image_features.squeeze(0).tolist()
    origin_idx = int(probs.argmax(dim=-1).item())

    return {
        "tags": tags,
        "embedding": embedding,
        "originClass": _ORIGIN_CLASSES[origin_idx],
        "originConfidence": float(probs[0][origin_idx]),
    }
