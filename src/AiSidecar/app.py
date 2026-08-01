from contextlib import asynccontextmanager

from fastapi import FastAPI
from pydantic import BaseModel

import inference


@asynccontextmanager
async def lifespan(app: FastAPI):
    inference.load_models()
    yield


app = FastAPI(title="HomeMediaHub AI Sidecar", lifespan=lifespan)


class EmbedTextRequest(BaseModel):
    text: str


class EmbedTextResponse(BaseModel):
    embedding: list[float]


@app.get("/health")
def health():
    return {"status": "ok", "phase": 8}


@app.post("/embed/text", response_model=EmbedTextResponse)
def embed_text(req: EmbedTextRequest):
    return EmbedTextResponse(embedding=inference.embed_text(req.text))
