# Architecture

ClipForge is a modular monolith with three deployable units plus infrastructure:

```
┌──────────────┐  HTTPS (cookies)  ┌──────────────────────┐   SQL    ┌────────────┐
│  Web (Next)  │ ────────────────▶ │  API (ASP.NET Core)  │ ───────▶ │ PostgreSQL │
│ localhost:3000│ ◀──────────────── │  localhost:5080      │          └────────────┘
└──────────────┘    JSON / signed  └───────────▲──────────┘
                    media URLs                 │ internal REST (X-Internal-Api-Key)
                                               │ claim / heartbeat / report
                                    ┌──────────┴───────────┐  signed URLs  ┌────────────────┐
                                    │ Processing (FastAPI  │ ────────────▶ │ Object storage │
                                    │ + worker, Python)    │ ◀──────────── │ Local | S3     │
                                    │ localhost:8008       │               └────────────────┘
                                    └──────────────────────┘
                                     FFmpeg · faster-whisper · OpenCV

  Local LLM (llama.cpp llama-server, 127.0.0.1:8090, OpenAI-compatible): the API's settings
  assistant and the worker's optional re-ranking/titles talk to it over HTTP (ADR-010).
```

## Components

### Web (`frontend/`)
Next.js 16 App Router. Public landing page, auth pages and the authenticated application
(dashboard, new project, projects, library, clip editor, live, billing, settings). Talks only
to the API with `credentials: "include"`. Video playback and downloads use short-lived signed
URLs returned by the API; the browser never needs storage credentials.

### API (`backend/`)
ASP.NET Core 10 Minimal APIs. Owns the database (EF Core migrations), authentication
(ASP.NET Identity for credentials + JWT in HttpOnly cookies), authorization and quotas, the
job queue (PostgreSQL table + `FOR UPDATE SKIP LOCKED` claim), storage abstraction
(`IObjectStorage`: Local or S3), e-mail, payment provider integration and the internal API
used by workers, and the settings assistant (`Features/Assistant`: rule layer + local/Gemini model +
validation + composed replies). Background hosted services: job reaper (expired leases), retention
cleanup, plan seeding, assistant prompt-cache warm-up.

### Processing (`processing/`)
Python 3.14 FastAPI service that also runs the job worker loop. Pulls jobs from the API,
downloads the source through a signed URL, runs the pipeline (probe → audio features →
transcription → visual features → fusion/scoring → candidate windows → render → thumbnails),
uploads outputs through signed URLs and reports results/progress through the internal API.
Stateless: everything it needs is in the job payload; it can be scaled horizontally.

### Local LLM (`llama-server`)
llama.cpp's OpenAI-compatible server with a small open model (default Qwen3-1.7B Q4_K_M,
Apache-2.0, ~1.1 GB, CPU). Started by `scripts/dev-llm.ps1` / `scripts/stack.ps1` on Windows and by
the `llm` service in Docker. Optional: without it the assistant reports itself unavailable and the
worker analyses without an LLM (`signals.llm.available = false`).

### Infrastructure (`infra/`, `scripts/`)
PostgreSQL 18, object storage (MinIO in Docker, local disk on Windows dev), Docker Compose
for Linux/macOS/CI, PowerShell scripts for Windows machines without Docker.

## Main flow (analyze a recording)

1. Web asks `POST /api/media/uploads` → API validates size/type against the plan and returns
   a signed upload target (Local: API endpoint; S3: presigned PUT).
2. Web uploads the file with XHR (progress bar) and calls `.../complete`; API verifies the
   object exists, size matches and magic bytes are a known video container.
3. Web creates a project `POST /api/projects` (Idempotency-Key) → API stores the project and
   a `ProcessingJob(kind=analyze_source, status=queued)`.
4. Worker claims the job (`POST /internal/workers/{id}/claim`), probes the file and reports
   `POST /internal/jobs/{id}/probe`. API enforces duration/quota limits (reserving minutes) or
   fails the job with a clear reason.
5. Worker runs analysis, sending heartbeats (lease extension + cancel check) and status
   transitions. Candidates are reported (`.../candidates`); the top N are rendered and
   reported (`.../clips`); analysis artifacts (transcript, signals) are uploaded.
6. Worker completes the job; API records usage and notifies the user.
7. Web polls the job (2 s) and shows candidates, previews, clips, downloads.
8. The user edits a clip (trim, title, subtitles, format) → `POST /api/clips/{id}/render`
   → `ProcessingJob(kind=render_clip)` → worker renders a new `ClipRender` version.

## Decisions (ADRs)

**ADR-001 Queue in PostgreSQL, not Redis.** Jobs are rows; claiming uses
`SELECT ... FOR UPDATE SKIP LOCKED`, leases + heartbeats handle worker crashes, attempts are
bounded. One source of truth, transactional with the rest of the data, no extra service to
operate for the MVP. Redis can be added later for pub/sub (live updates) and a SignalR
backplane without changing the job model.

**ADR-002 Storage abstraction with signed URLs.** `IObjectStorage` has Local and S3
implementations. Both return signed download/upload URLs (Local: HMAC-signed API endpoints;
S3: presigned URLs). Workers and browsers only ever see signed URLs. This lets development
run without MinIO and production use any S3-compatible bucket.

**ADR-003 The API never runs FFmpeg.** Media probing and all heavy work happen in the
worker. Uploaded content is validated by magic bytes and size at upload completion; duration
and codec limits are enforced when the worker reports the probe (defence in depth: the API
checks, not the worker).

**ADR-004 Processing in Python.** The AI/media ecosystem (faster-whisper, OpenCV, NumPy)
is Python-native. The worker is a thin loop around pure functions so the analysis can be
unit-tested on synthetic signals and swapped for better models behind provider interfaces
(`TranscriptionProvider`, `AudioEventProvider`, `LlmProvider`).

**ADR-005 Honest signals.** Every candidate carries the signals used and their availability.
No fixtures, no fake candidates, no fake "virality" numbers. The 0–100 score is an internal
relevance estimate until validated empirically.

**ADR-006 Auth = ASP.NET Identity + JWT cookies.** Identity provides hashing, lockout,
e-mail confirmation and reset tokens. Access tokens (15 min) and rotating refresh tokens
(30 days, hashed in DB) are HttpOnly, SameSite cookies, so the browser never handles tokens.
A refresh token rotates exactly once (conditional update); presenting a rotated token again,
even concurrently, revokes every session of the user (reuse detection).
`Authorization: Bearer` is also accepted (future mobile/companion clients).

**ADR-007 Plans as configuration.** Plan limits and prices are configuration (`Plans`
section) upserted into the `plans` table on startup; nothing commercial is hard-coded.

**ADR-008 Link sources are explicit about support.** Pasting a Twitch/YouTube link
identifies the platform and returns `supported: false` with alternatives (upload, OBS
companion) unless an authorized integration exists for that platform. No scraping.

**ADR-009 Live capture via an OBS companion app.** Real-time detection will be fed by a
local companion connected to OBS through obs-websocket (user-authorized), slicing the local
recording/replay buffer into segments and uploading them. See `docs/LIVE_CAPTURE.md`.

**ADR-010 The product's own AI: a local model by default, free Gemini optional, no paid
provider.** The settings assistant (and the worker's optional re-ranking) uses an open model served by
llama.cpp on our own hardware (default Qwen3-1.7B Q4_K_M, `Assistant:Provider=local`): no per-call
cost, no data leaves the server. Google's Gemini API on its free tier is an optional alternative
(`Assistant:Provider=gemini`, Flash models, low rate limits; Google may use free-tier prompts to improve
its products, so it is an explicit admin choice, disclosed in the privacy page and
`docs/SETUP_PROVIDERS.md`). `none` turns the assistant off. No paid provider is wired for the
assistant.
A 1.7B model extracts structured settings well but is not reliable in free text: during tuning it
claimed "clipes ajustados para mais curtos" without changing durations and invented "adicionado a
opção de postar no Instagram"; its JSON copied context values and listed every unsupported kind.
Therefore the model only returns schema-constrained JSON (`{ intent, patch, unsupported, question }`),
a deterministic rule layer decides every explicit mention (platforms, numbers, subtitle placement,
moment types, languages, unsupported requests) and never applies a negated one ("não quero clipes
curtos", "nada de legenda no topo": the setting keeps a value the user did not refuse, and the refused
value is vetoed for the model too), model fields are accepted only when the message talks
about that setting, the result goes through the same validator and plan caps as project settings, and
the server composes the pt-BR reply from the validated changes only (fixed explanations for unsupported
requests; the model's clarifying question only when nothing changed). The system prompt is byte-stable
(few-shot examples, no per-request data) so llama.cpp keeps its prefix cached (`cache_prompt`, warmed at
API startup): measured on a 4-core desktop CPU, ~20 s for the first call, 5–9 s afterwards.

## Security model (summary)

- Ownership checks on every query (`UserId` from claims). Integration tests assert isolation.
- Internal API protected by a shared key and never exposed through the web app; in Docker
  it is only reachable on the internal network. Every job report must come from the worker
  holding the lease (`X-Worker-Id`), so a stale worker cannot interfere with a re-claimed job.
- FFmpeg/ffprobe invoked with argument lists; all paths are generated; user text is rendered
  through ASS subtitle files (escaped), never through filter expressions.
- Storage keys are generated under `users/{userId}/…` and validated against that prefix.
- Signed URLs use HMAC-SHA256 with expiry; comparison is constant-time.
- Uploads: allow-list of containers, magic-byte check, per-plan size limits, request body
  limits, temp upload expiry; the signed Local upload only lands while its target accepts it
  (pending asset, running job), so a verified or deleted object cannot be replaced or re-created.
- Rate limiting on authentication, upload and signed-transfer endpoints; behind the reverse
  proxy the client address is resolved from `X-Forwarded-For` of trusted proxies only
  (`ReverseProxy` section).
- Quota checks and the rows they guard are serialised per user (advisory lock), so plan caps
  hold under concurrent requests; cancelling an analysis that already started consumes its
  reserved minutes.
- Secrets only from configuration; structured logs never include tokens or passwords (the Gemini key
  travels in a header, never in a URL; assistant logs carry timings and counts, not the messages).
- The assistant's model output is untrusted data: it is schema-constrained, filtered against the
  user's own words and validated like any settings request before anything reaches the user.
- LGPD: account deletion purges data and storage; retention policy per plan; consent flag
  on registration (terms) and explicit confirmation that the user owns the content.

## Observability

Serilog structured logs (console JSON in Docker), request logging with correlation id,
`/health` (liveness) on API and processing, `/health/ready` (database + storage) on the API,
`GET /api/assistant/status` for the model server, job events table for per-job audit trail.
Metrics/OTel exporters are a phase-6 item.
