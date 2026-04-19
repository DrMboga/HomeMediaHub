from fastapi import FastAPI

app = FastAPI(title="HomeMediaHub AI Sidecar")


@app.get("/health")
def health():
    return {"status": "ok", "phase": 1}
