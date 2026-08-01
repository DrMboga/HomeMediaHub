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
ENV OMP_NUM_THREADS=4
EXPOSE 8000 8001
CMD ["python", "worker.py"]
