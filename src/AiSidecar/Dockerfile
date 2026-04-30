FROM python:3.11-slim
WORKDIR /app
# libgl1 + libglib2.0-0 are required by opencv-python (ultralytics dependency)
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgl1 \
    libglib2.0-0 \
    && rm -rf /var/lib/apt/lists/*
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt
# Pre-download models into image layers — avoids runtime downloads
RUN python -c "from ultralytics import YOLO; YOLO('yolov8n.pt')"
RUN python -c "import open_clip; open_clip.create_model_and_transforms('ViT-B-32', pretrained='openai')"
COPY . .
EXPOSE 8000
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "8000"]
