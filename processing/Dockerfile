# ClipForge processing service (FastAPI + job worker)
FROM python:3.14-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1

# ffmpeg (libx264, aac, libass) and the DejaVu fonts as a system fallback for libass
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg fonts-dejavu-core ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN groupadd --system clipforge && useradd --system --gid clipforge --create-home clipforge \
    && mkdir -p /data/work /data/models && chown -R clipforge:clipforge /data

WORKDIR /srv/processing

COPY requirements.txt ./
RUN python -m pip install -r requirements.txt

COPY app ./app
COPY assets ./assets
COPY pyproject.toml README.md ./

ENV FFMPEG_PATH=ffmpeg \
    FFPROBE_PATH=ffprobe \
    WORK_DIR=/data/work \
    MODELS_DIR=/data/models \
    SERVICE_HOST=0.0.0.0 \
    SERVICE_PORT=8008 \
    LOG_FORMAT=json \
    WORKER_ENABLED=true \
    CLIPFORGE_API_BASE_URL=http://api:5080

USER clipforge
VOLUME ["/data/work", "/data/models"]
EXPOSE 8008

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8008/health', timeout=4).status == 200 else 1)"

CMD ["python", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8008"]
