# ClipForge processing service

Python 3.14 service that turns recorded streams into short clips: a FastAPI app
(`/health`, `/version`, optional `POST /analyze` for development) plus the job worker that
claims jobs from the API (`/internal`), runs the media pipeline and reports back.

The contracts live in `docs/API.md` (internal endpoints, payloads) and
`docs/PROCESSING_PIPELINE.md` (job state machine, signals, fusion, rendering).

## Layout

```
app/
  main.py            FastAPI app, worker thread lifecycle, /health /version /analyze
  config.py          pydantic-settings (every variable of the Processing section of .env.example)
  api_client.py      httpx client for /internal (X-Internal-Api-Key, retries, typed 409) + signed transfers
  models.py          pydantic models mirroring docs/API.md (camelCase)
  worker/            loop.py (claim loop) · runner.py (job execution) · heartbeat.py · errors.py (+ CancelToken)
  media/             ffmpeg.py (the only place that spawns ffmpeg/ffprobe) · probe.py · audio.py · frames.py
                     concat.py (stream-copy join of live segments, session time -> file time)
  analysis/          audio_features.py · transcription.py · visual_features.py · webcam.py · lexicon.py
                     fusion.py · llm.py · pipeline.py (orchestration shared by worker and /analyze)
                     live.py (live_segment: window plan, commit rule, session sample, language)
  render/            subtitles.py (ASS) · layouts.py (filter graphs) · renderer.py (ffmpeg encode + thumbnail)
assets/fonts/        DejaVu Sans (bundled, free license) used by the subtitles filter
assets/models/       YuNet face detector (MIT) used for webcam detection
tests/               pytest suite; tests/fixtures/make_fixture.py synthesises the test video
```

## Running locally (Windows, no Docker)

```powershell
cd processing
.venv\Scripts\python -m pip install -r requirements-dev.txt
# the API must be reachable at CLIPFORGE_API_BASE_URL (default http://localhost:5080)
.venv\Scripts\python -m uvicorn app.main:app --port 8008
```

Configuration comes from the environment or from `.env` (repository root or
`processing/.env`). All variables are listed in the *Processing* section of
`.env.example`; `%LOCALAPPDATA%` references are expanded. Defaults on Windows point to
`%LOCALAPPDATA%\ClipForge\tools\ffmpeg\bin\ffmpeg.exe`, `%LOCALAPPDATA%\ClipForge\work`
and `%LOCALAPPDATA%\ClipForge\models`; on Linux to `ffmpeg`/`ffprobe` on the `PATH` and
`/var/lib/clipforge/{work,models}`.

Extra variables (not in `.env.example`):

| Variable | Default | Purpose |
|---|---|---|
| `DEV_ENDPOINTS` | `false` | enables `POST /analyze` (analysis of a local file path, no API needed) |
| `LOG_LEVEL` / `LOG_FORMAT` | `INFO` / `console` | structlog level and renderer (`json` for containers) |
| `WHISPER_LOCAL_FILES_ONLY` | `false` | never contact Hugging Face (use the cached model only) |
| `TRANSCRIPTION_CHUNK_SECONDS` | `300` | audio is transcribed in chunks snapped to silences |
| `VISUAL_FPS` / `VISUAL_WIDTH` | `2` / `320` | frame sampling for the visual signals |
| `WEBCAM_DETECTION` / `WEBCAM_SAMPLES` | `true` / `40` | webcam overlay detection |
| `API_TIMEOUT_SECONDS` / `API_MAX_RETRIES` | `30` / `4` | internal API client |
| `WORK_DIR_STALE_HOURS` | `6` | at worker start, job directories left in `WORK_DIR` by a crashed worker and untouched for this long are removed: only those carrying the `.clipforge-job` marker the worker writes when it creates them, nothing else; `0` disables |
| `WORK_DIR_ORPHAN_MINUTES` | `15` | after every job, the directories **this** worker created (marker with its `WORKER_ID`), never the current job's, untouched for this long are removed too; `0` disables |
| `WHISPER_DETECT_IGNORE` | `la,sa` | languages never taken from the automatic detection (the best other candidate is used) |
| `LLM_LOCAL_MODEL` | (empty) | label reported for the local model; empty = the id the server reports |
| `LLM_TIMEOUT_SECONDS` / `LLM_BUDGET_SECONDS` | `60` / `100` | one LLM request / the whole re-rank + titles stage of a job |
| `JOB_KINDS` | `analyze_source,render_clip,live_segment` | job kinds this worker claims |
| `LIVE_LLM_BUDGET_SECONDS` | `20` | LLM assist stage of a `live_segment` (also capped by `LLM_BUDGET_SECONDS`; `0` skips it: live latency first) |
| `LIVE_WEBCAM_SAMPLES` | `8` | frames sampled for webcam detection in a live window (each one an ffmpeg seek) |

`HF_HUB_DISABLE_SYMLINKS_WARNING` defaults to `1` (set by the service before faster-whisper
loads huggingface_hub; an explicit value is kept). stdout/stderr are switched to UTF-8 at
startup, so redirected Windows logs no longer show mojibake for "…" and accents.

### Development endpoint

```bash
curl -s localhost:8008/health
curl -s -X POST localhost:8008/analyze -H "content-type: application/json" \
  -d '{"path": "C:/path/to/recording.mp4", "settings": {"sensitivity": 60}, "transcription": true}'
```

`POST /analyze` returns the probe, the candidates (`CandidateInput` shape), the signal
availability flags, the detected webcam region and timings. It never renders or talks to the
API.

## Pipeline summary

1. download the source through the signed URL into `WORK_DIR/{jobId}/` (streamed);
2. `ffprobe` → `/probe` (a `409` from the API stops the job: it was already failed server-side,
   which is also how sources without a video stream are rejected — `invalid_media`, "arquivo
   sem vídeo");
3. audio features from a 16 kHz mono PCM pipe (energy dBFS, spectral centroid, ZCR, flux,
   onset) → per-second `energy_z` (robust rolling z-score), `burst`, `sustained`,
   `scream_like`, `laughter_like`, `silence` — all heuristic (`method: heuristic-v1`) — and
   the recording's typical level (median of the non-silent seconds);
4. transcription with faster-whisper (word timestamps + VAD, model from the plan —
   `limits.transcriptionModel`: free `base`, creator/pro `small` — bounded by
   `WHISPER_MODEL`, default `small`, ≈ 3× the CPU time of `base`; chunked, cancellable):
   a voice-activity scan first (no speech → `speechDetected: false`, no language, no
   transcription, clips without captions), then with `language: "auto"` one language
   detection per job over 30 s windows of speech spread over the whole recording, and every
   chunk transcribed with that language (`languageConfidence: "low"` below 0.5);
   unavailable model → `available: false` and the pipeline continues (a failed model load
   is retried after a backoff of 1 min doubling up to 30 min, not cached forever); a chunk
   that fails later is a gap (`coverage` < 1);
5. visual features (2 fps, 320 px, OpenCV): motion, scene cuts, flashes — a frame pipe that
   stops early leaves `coverage` < 1 and an `error`; webcam detection (YuNet faces on ~40
   sampled frames → stable rectangle);
6. optional LLM (`LLM_PROVIDER`: `none` default · `local` = the product's own model behind
   the OpenAI-compatible llama-server at `LLM_BASE_URL` · `gemini` with `GEMINI_API_KEY` ·
   `anthropic`, paid, opt-in): after fusion it re-ranks the top 20 candidates against
   `settings.instructions`/`focusCategories` (rank priority ±10, the score never changes)
   and suggests a validated pt-BR title (≤ 60 characters, grounded in the transcript) for
   every clip to render, within `LLM_BUDGET_SECONDS`; never required, a failure or a
   timeout only ends the stage (the anthropic provider also rates transcript windows
   before fusion, as before);
7. fusion exactly as documented: weighted clamped z-scores (weights renormalised when a
   signal is missing, and per second over a signal's gaps), threshold from the sensitivity,
   peak picking, window expansion with pre/post roll, snapping to silences/transcript
   boundaries, merge, NMS, 0–100 score (internal relevance estimate), category rules and
   pt-BR reasoning computed from the signals ("Volume 18 dB acima do nível típico da
   gravação, …"); `settings.focusCategories` multiply the matching signal terms and give
   +10 rank priority (score unchanged), `settings.customKeywords` count like lexicon hits
   of weight 3 and are named in the reasoning; a source shorter than `minClipSeconds`
   clamps min/target/max to its duration and may become one whole-video candidate (job
   event "Vídeo mais curto que a duração mínima; usando o vídeo inteiro como limite") →
   `/candidates`;
8. render the top `autoRenderTop` candidates (absent = every candidate; one failing render
   is an error event, the others go on) (16:9 letterbox, 9:16 center crop or
   webcam-split, 1:1; ASS subtitles from word timestamps inside the platform safe area —
   9:16 bottom captions at 20 % of the height, top captions ≥ 12 %, title at 8 % —, title;
   loudnorm; libx264 veryfast crf 22; thumbnail at the midpoint) → signed uploads →
   `/clips` (with the requested framing mode; the resolved layout goes to a job event);
9. upload `transcript.json` + `signals.json` → `/analysis` → `/complete`.

**Live segments** (`live_segment`, `docs/PROCESSING_PIPELINE.md` "live_segment"): the
segment and the previous one are downloaded, the segment is probed (`/probe`), the two are
joined without re-encoding (concat demuxer) and the last `min(45 s, previous)` of the
previous one + the segment are analysed on a grid of whole session seconds, with the same
signals and fusion. Only the candidates of the **commit rule** are reported: peaks from
the previous segment's hand-over point to this one's, which is `end − postRoll`
(`postRoll = clamp(maxClipSeconds / 2, 8, 30)`) but never before the next window's first
whole second + 8 s nor after `end − 1` (the first segment from the start, the final one to
its end), in session times, scored against the session's composite sample; the session
language is fixed by the first segment with speech. The top `autoRenderTop` are rendered
from the joined file; `/analysis` returns `summary.liveContext` (`compositeSample`,
`language`, `sequence`, `committedUntil`), which the next segment receives as `context`. A
`render_clip` of a live project receives `segments` instead of `source`: they are joined
the same way (a segment only needs a duration: the last one of a session is often a
sliver) and the clip's session range is cut from the result.

`maxRenderHeight` is treated as the resolution class of the shorter side: `>= 1080` →
1920×1080 / 1080×1920 / 1080×1080, otherwise 1280×720 / 720×1280 / 720×720 (480p below 720).

Status protocol: the claim already puts the job in `preparing_media`, so the worker posts
`/status` only for the following transitions and sends progress/message updates through
`/heartbeat`; every internal call carries `X-Worker-Id`. Errors: media/user errors →
`/fail retryable=false` (`invalid_media`, `render_failed`); a failed transfer with the
storage (any HTTP error or a network error after the retries, source download or upload)
→ `/fail code=transfer_failed retryable=true` (never charged); a missing ffmpeg/ffprobe →
`/fail code=worker_error retryable=true`; a claimed payload that does not match the
contract → `/fail code=invalid_payload retryable=false` (the loop claims again without
backing off); unexpected errors → `/fail retryable=true code=worker_error`; cancellation
(heartbeat `cancelRequested`, also while the source is still downloading or a clip is
uploading) kills the running ffmpeg process, aborts the transfer and reports `status:
cancelled`, with the heartbeats continuing until that final report so the lease never
expires on a cancelled job. Internal calls are retried with backoff, except that `/claim`,
`/clips` and `/events` are never re-sent once the API may have applied them, and a `409`
answering a re-sent `/status`, `/complete` or `/fail` counts as already applied. Event
messages are cut to the API's 1000 characters. The work directory is always removed, and
directories left by a crashed worker are swept at the next start (`WORK_DIR_STALE_HOURS`;
only directories carrying the `.clipforge-job` marker the worker writes into each job
directory, never anything else in `WORK_DIR`) and after every job for this worker's own
leftovers (`WORK_DIR_ORPHAN_MINUTES`).

## Tests

```bash
.venv/Scripts/python -m pytest -m "not integration"   # fast suite (~20 s on a 4-core CPU)
.venv/Scripts/python -m pytest -m integration          # needs the API running
.venv/Scripts/ruff check app tests
```

The live tests (`tests/test_live*.py`) cut the e2e fixture
(`%LOCALAPPDATA%/ClipForge/fixtures/e2e-fixture.mp4`, or `CLIPFORGE_E2E_FIXTURE`) into
Matroska segments of ~25 s and ~21 s with the segment muxer, the way the companion cuts a
recording (skipped without the fixture), and the 20 s synthetic video into a 19.8 s segment
and a final 0.2 s sliver, in temporary directories removed afterwards; the commit rule is
also tested on synthetic composite series (boundary layouts and seeded random sessions).

The fast suite synthesises its fixtures under `tests/fixtures/generated/` (git-ignored): a
90 s `testsrc2` video with three loud bursts and two scene cuts, a 20 s variant for the
worker tests, and — on Windows with a SAPI voice installed — a real spoken pt-BR WAV and the
e2e fixture's English phrases read by an en-US voice, used by the transcription tests
(skipped with a reason otherwise). The LLM clients are tested against `httpx.MockTransport`;
nothing in the fast suite needs the llama-server.

## Docker

`Dockerfile` builds a `python:3.14-slim` image with ffmpeg from apt, the bundled fonts and
model, running as a non-root user. Mount `/data/work` and `/data/models` (the whisper model
is downloaded on first use unless `WHISPER_LOCAL_FILES_ONLY=true`).
