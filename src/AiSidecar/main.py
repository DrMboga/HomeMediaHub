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
_clip_tokenizer = None

_ORIGIN_LABELS = [
    "a real photograph taken with a camera",
    "a screenshot of a computer or phone screen",
    "a scanned document or text page",
]
_ORIGIN_CLASSES = ["photo", "screenshot", "document"]
_YOLO_CONFIDENCE_THRESHOLD = 0.30


@asynccontextmanager
async def lifespan(app: FastAPI):
    global _yolo, _clip_model, _clip_preprocess, _clip_tokenizer
    _yolo = YOLO("yolov8n.pt")
    _clip_model, _, _clip_preprocess = open_clip.create_model_and_transforms(
        "ViT-B-32", pretrained="openai"
    )
    _clip_model.eval()
    _clip_tokenizer = open_clip.get_tokenizer("ViT-B-32")
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

    try:
        pil_image = Image.open(path).convert("RGB")
    except Exception as e:
        raise HTTPException(status_code=422, detail=f"Cannot open image: {e}")

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
    text_tokens = _clip_tokenizer(_ORIGIN_LABELS)

    with torch.no_grad():
        image_features = _clip_model.encode_image(image_tensor)
        text_features = _clip_model.encode_text(text_tokens)
        image_features = image_features / image_features.norm(dim=-1, keepdim=True)
        text_features = text_features / text_features.norm(dim=-1, keepdim=True)
        probs = (100.0 * image_features @ text_features.T).softmax(dim=-1)

    embedding: list[float] = image_features.squeeze(0).tolist()
    origin_idx = int(probs.argmax(dim=-1).item())

    return AnalyzeResponse(
        tags=tags,
        embedding=embedding,
        originClass=_ORIGIN_CLASSES[origin_idx],
        originConfidence=float(probs[0][origin_idx]),
    )
