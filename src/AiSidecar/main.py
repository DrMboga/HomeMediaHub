import threading
from contextlib import asynccontextmanager
from pathlib import Path

import torch
from fastapi import FastAPI, HTTPException
from PIL import Image
from pydantic import BaseModel
from ultralytics import YOLO
import open_clip

# ── Models (loaded once at startup) ─────────────────────────────────────────

_yolo: YOLO | None = None
_clip_model = None
_clip_preprocess = None
_text_features = None  # pre-computed at startup — prompts never change

_ORIGIN_LABELS = [
    "a real photograph taken with a camera",
    "a screenshot of a computer or phone screen",
    "a scanned document or text page",
]
_ORIGIN_CLASSES = ["photo", "screenshot", "document"]
_YOLO_CONFIDENCE_THRESHOLD = 0.30

# Serialise all inference — PyTorch CPU inference is not thread-safe under
# concurrent load; FastAPI runs sync endpoints in a thread pool so without
# this lock multiple threads would call the models simultaneously.
_inference_lock = threading.Semaphore(1)


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _yolo, _clip_model, _clip_preprocess, _text_features
    _yolo = YOLO("yolov8n.pt")
    _clip_model, _, _clip_preprocess = open_clip.create_model_and_transforms(
        "ViT-B-32", pretrained="openai"
    )
    _clip_model.eval()
    tokenizer = open_clip.get_tokenizer("ViT-B-32")
    # Pre-compute text features once — reused for every /analyze call
    text_tokens = tokenizer(_ORIGIN_LABELS)
    with torch.no_grad():
        tf = _clip_model.encode_text(text_tokens)
        _text_features = tf / tf.norm(dim=-1, keepdim=True)
    yield


app = FastAPI(title="HomeMediaHub AI Sidecar", lifespan=lifespan)


# ── Schemas ──────────────────────────────────────────────────────────────────

class AnalyzeRequest(BaseModel):
    filePath: str


class DetectedTag(BaseModel):
    tag: str
    confidence: float
    model: str


class AnalyzeResponse(BaseModel):
    tags: list[DetectedTag]
    embedding: list[float]
    originClass: str
    originConfidence: float


# ── Endpoints ────────────────────────────────────────────────────────────────

@app.get("/health")
def health():
    return {"status": "ok", "phase": 4}


@app.post("/analyze", response_model=AnalyzeResponse)
def analyze(req: AnalyzeRequest):
    path = Path(req.filePath)
    if not path.exists():
        raise HTTPException(status_code=404, detail=f"File not found: {req.filePath}")

    # Open and immediately close the file handle; convert() returns a new object
    try:
        img = Image.open(path)
        pil_image = img.convert("RGB")
        img.close()
    except Exception as e:
        raise HTTPException(status_code=422, detail=f"Cannot open image: {e}")

    try:
        with _inference_lock:
            return _run_inference(path, pil_image)
    except HTTPException:
        raise
    except Exception as e:
        raise HTTPException(status_code=500, detail=f"Inference error: {e}")


def _run_inference(path: Path, pil_image: Image.Image) -> AnalyzeResponse:
    # ── YOLOv8n object detection ─────────────────────────────────────────────
    yolo_results = _yolo(str(path), verbose=False)[0]
    seen: dict[str, float] = {}
    for box in yolo_results.boxes:
        label = _yolo.names[int(box.cls[0])]
        conf = float(box.conf[0])
        if conf >= _YOLO_CONFIDENCE_THRESHOLD:
            seen[label] = max(seen.get(label, 0.0), conf)

    tags = [
        DetectedTag(tag=label, confidence=conf, model="yolov8n")
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

    return AnalyzeResponse(
        tags=tags,
        embedding=embedding,
        originClass=_ORIGIN_CLASSES[origin_idx],
        originConfidence=float(probs[0][origin_idx]),
    )
