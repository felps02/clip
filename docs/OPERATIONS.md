# Operations guide

How to run ClipForge in a real environment: topology, configuration, deploys, backups,
monitoring and the runbooks for the failures we know about. Everything here refers to what
exists in the repository today; items marked **(planned)** are not implemented yet.

## 1. Topology

| Unit | Scales | State | Notes |
|---|---|---|---|
| Web (`frontend/`, Next.js standalone) | horizontally, stateless | none | Serves pages; talks to the API from the browser |
| API (`backend/`, ASP.NET Core) | horizontally, stateless | PostgreSQL + object storage | Owns auth, quotas, queue, signed URLs, webhooks; runs the maintenance services below |
| Worker (`processing/`, FastAPI + loop) | horizontally, one job per process by default | local scratch (`WORK_DIR`) | CPU heavy (FFmpeg, Whisper); never exposed publicly |
| PostgreSQL 18 | vertically / managed | all relational data, job queue | Daily backups + PITR |
| Object storage (S3-compatible) | managed | sources, renders, thumbnails, analysis | Presigned URLs; lifecycle rules |
| Local LLM (`llama-server`, optional) | one per host, `-np` slots | model file only (prompt cache in RAM) | Settings assistant (API) and the worker's optional re-ranking; internal network only (`127.0.0.1:8090` / `llm:8090`) |

Maintenance services inside the API (every instance runs them; they are idempotent):

| Service | Interval | Does |
|---|---|---|
| `JobReaper` | `Jobs:ReaperIntervalSeconds` (30 s) | Jobs whose lease expired → back to `queued` (or `failed` when attempts are exhausted) |
| `RetentionCleanup` | `Retention:CleanupIntervalMinutes` (15 min) | Expires pending uploads after `Retention:TempUploadHours`; purges soft-deleted projects/clips after the plan's `retentionDays` (storage first, then rows) |
| `AccountPurge` | `Retention:CleanupIntervalMinutes` | Purges accounts whose deletion was requested `Retention:AccountPurgeDelayHours` ago |
| `PlanSeeder` | on startup | Upserts plans from the `Plans` configuration |
| `AssistantWarmup` | once, in the background after startup (local provider) | Sends the assistant's system prompt to `llama-server` so its prefix is cached (3 attempts, logs "Assistant prompt cache warmed in … s") |

## 2. Configuration and secrets

All configuration comes from environment variables (see `.env.example`; ASP.NET uses `__`
for sections). Secrets that must be unique per environment and rotated independently:

| Secret | Used for | Rotation impact |
|---|---|---|
| `Auth__JwtSigningKey` | access tokens (15 min) | every user is signed out of the current access token; refresh tokens keep working |
| `Storage__SigningKey` | Local-storage signed URLs | outstanding signed URLs stop working (they expire in minutes anyway) |
| `Internal__ApiKey` / `CLIPFORGE_INTERNAL_API_KEY` | worker ↔ API | rotate both sides together; running jobs fail their next report and are re-queued by the reaper |
| `Payments__Stripe__SecretKey`, `Payments__Stripe__WebhookSecret` | billing | rotate in the Stripe dashboard first, then here |
| SMTP credentials, S3 keys, `Assistant__Gemini__ApiKey` / `GEMINI_API_KEY`, `ANTHROPIC_API_KEY` | providers | provider specific (the Gemini key only matters with `Assistant__Provider=gemini` / `LLM_PROVIDER=gemini`) |

Production checklist: `ASPNETCORE_ENVIRONMENT=Production`, `Database__MigrateOnStartup=false`,
`Auth__CookieSecure=true`, `Auth__CookieDomain` when web and API are on different hosts,
`Cors__AllowedOrigins__0` = the web origin, `Storage__Provider=S3`, `Email__Provider=Smtp`,
`Frontend__BaseUrl` / `Api__PublicBaseUrl` / `Api__WorkerBaseUrl` set to the real URLs,
`Assistant__Provider` (`local` with `Assistant__Local__BaseUrl` reachable from every API instance,
`gemini`, or `none`).

## 3. Deploying a release

1. CI (`.github/workflows/ci.yml`) must be green: backend build + tests against PostgreSQL,
   processing lint + tests with FFmpeg, frontend lint + typecheck + tests + build.
2. Build images (`infra/docker/api.Dockerfile`, `processing/Dockerfile`, `frontend/Dockerfile`).
   `NEXT_PUBLIC_*` values are baked into the web image at build time.
3. Apply migrations **before** switching traffic:
   `dotnet ef database update --project backend/src/ClipForge.Infrastructure --startup-project backend/src/ClipForge.Api`
   (or run the API once with `Database__MigrateOnStartup=true` in a one-off task).
   Migrations are listed in `docs/migrations/CHANGELOG.md`; they are additive unless noted.
4. Roll the API, then the workers, then the web. Workers drain safely: stopping a worker
   abandons its current job, the lease expires and the reaper re-queues it (`attempt + 1`).
5. Smoke test: `GET /health` on API and worker, `GET /health/ready` on the API (database and
   storage), `GET /api/assistant/status` (signed in) when the assistant is enabled, then `scripts/e2e/smoke.py --api <url> --fixture <video>`
   against a staging environment, and the Playwright suite in `e2e/` when the web changed.

## 4. Capacity and cost

- One worker process handles one job at a time (`MAX_PARALLEL_JOBS=1`). Measured on a 4-core
  desktop CPU (Ryzen 3 3200G) with the `base` Whisper model: a 70 s source is analysed and
  2 clips rendered in about 30 s, i.e. roughly 2–3× faster than real time. Plan for
  `hours of uploads per day ÷ (24 × 2)` worker processes, with headroom.
- Costs are bounded by plan limits (minutes per month, max duration, max file size,
  concurrent jobs, renders per month) enforced by the API, and by `Jobs__MaxAttempts`
  (each retry re-processes the file).
- Transcription model per plan: free `base`, creator and pro `small` (`limits.transcriptionModel`;
  the worker's `WHISPER_MODEL`, default `small`, is the largest it loads). On CPU `small` costs about 3×
  the transcription time of `base`; budget worker capacity for paid plans accordingly. GPU workers
  (`WHISPER_DEVICE=cuda`) make `small`/`medium` cheap.
- Local LLM: Qwen3-1.7B Q4_K_M needs ~1.5 GB RAM plus the KV cache (`-c`), and uses all cores while it
  answers (5–9 s per assistant message with a warm prompt cache on a 4-core CPU, ~20 s cold). On a
  shared host it competes with the worker; give it its own cores (`-t`) or its own machine when the
  assistant is busy. Each answered message is bounded by `assistantMessagesPerMonth` and the per-user
  rate limit.
- Storage: sources are the biggest objects. Retention per plan deletes them; add bucket
  lifecycle rules as a safety net (e.g. delete `users/*/sources/*` after 120 days).

## 5. Monitoring

- **Liveness:** `GET /health` (API: process up and storage provider name; worker: FFmpeg,
  Whisper, worker loop state, current job).
- **Readiness:** `GET /health/ready` on the API runs `SELECT 1` on the database and checks the
  object storage (Local: the root directory exists; S3: a one-key listing of the bucket), each with a
  3 s timeout, in parallel. `200 { status: "ready", checks: { database: "ok", storage: "ok" } }`, or
  `503 { status: "not_ready", checks }` with the failing check `failed` or `timeout` (the reason is
  logged as "Readiness check … failed"). Route traffic only to ready instances; liveness stays
  `GET /health`.
- **Assistant / LLM server:** `GET /api/assistant/status` (signed in) says whether the provider answers
  and why not (`reason`); `llama-server` itself answers `GET /health` (`503` while loading the model).
  Every call logs "Local LLM call took {ElapsedMs} ms (prompt … tokens, … cached; … generated)" — a
  `cached` count near zero on every call means the prompt cache is not being reused (slots too small, the
  server restarted, or a different system prompt). Unavailability answers `503 assistant_unavailable`
  and is logged as a warning, not an error.
- **Logs:** Serilog (API) and structlog (worker) write structured lines to stdout; ship them
  to the log platform. Correlation id per request (`cid`). Tokens, passwords and signed URL
  signatures are never logged.
- **Per-job audit:** `processing_job_events` (status transitions, warnings, failures) — also
  visible to the user on the project page.
- **What to alert on** (queries against PostgreSQL until metrics exist):
  - queue backlog: `select count(*) from processing_jobs where status = 'queued' and queued_at < now() - interval '15 minutes'`
  - failure rate: jobs `failed` in the last hour / jobs finished in the last hour > 20 %
  - stuck jobs: running statuses with `lease_expires_at < now() - interval '10 minutes'` (the reaper should have handled them)
  - webhooks: `payment_events` with `status = 'failed'`
- **(planned)** OpenTelemetry traces/metrics export.

## 6. Backups and restore

- PostgreSQL: daily full backup + WAL archiving (managed PITR). Test a restore monthly.
- Object storage: enable bucket versioning or replication for renders; sources can be
  re-uploaded by users, renders can be regenerated from sources while the source exists.
- Restore order: database first, then confirm storage keys referenced by `media_assets`,
  `clip_renders` and `processing_jobs.*_storage_key` exist; missing objects surface as 404 on
  download and can be re-rendered.

## 7. Runbooks

**Queue not draining** — check worker `/health` (`worker.running`, `lastError`), worker logs,
and that `CLIPFORGE_INTERNAL_API_KEY` matches `Internal__ApiKey` (401 on `/internal/*`).
Scale workers if they are all busy.

**Jobs failing with `worker_error`** — transient (network, storage, missing FFmpeg). They are
retried up to `Jobs__MaxAttempts`. Look at the job events for the stack summary.

**Jobs failing with `invalid_media`** — the user's file is unreadable or has no video; not
retried. The message is shown to the user.

**Transcription unavailable** — the worker could not load the Whisper model (offline host,
disk full, wrong `MODELS_DIR`). Analysis continues without transcript and the candidates say
so (`signals.transcript.available = false`). Pre-download models into `MODELS_DIR` on
hosts without internet access.

**Disk full on a worker** — work directories are deleted after each job and orphans are
removed at startup (directories carrying the worker's `.clipforge-job` marker, untouched for
`WORK_DIR_STALE_HOURS`; nothing else in `WORK_DIR` is ever deleted, so remove unmarked
leftovers by hand and keep `WORK_DIR` a directory of its own); check `WORK_DIR` size and the
size of the sources being processed. During an analysis `WORK_DIR` holds the source plus one
clip (each clip is uploaded and deleted right after its render); below `RENDER_MIN_FREE_MB`
(default 500) free on that volume the remaining clips are skipped with a warning event
("Espaço em disco insuficiente no servidor de processamento…") and the job still completes.

**Assistant unavailable (`503 assistant_unavailable`, "Assistente indisponível neste ambiente")** —
check `GET /api/assistant/status`: `reason` says whether the provider is off (`Assistant__Provider=none`),
the server is unreachable or still loading, or the Gemini key/quota failed. Local provider: is
`llama-server` running (`scripts/dev-llm.ps1`, Docker `llm` service) and reachable at
`Assistant__Local__BaseUrl` from the API host (`curl <url>/health`)? A restarted server loses its prompt
cache: the next call is slow (~20 s) and later calls are fast again (or restart the API to warm it).
Calls longer than `Assistant__TimeoutSeconds` (60) answer 503: on a saturated CPU give the server more
threads/cores or lower its parallel slots. Failed calls do not count against the user's monthly
messages. The quick presets in the web keep working without the assistant.

**Payment webhook failures** — the event is stored with `status = 'failed'` and the endpoint
answers 500 so Stripe redelivers; fix the cause and let the redelivery succeed, or resend from
the Stripe dashboard. Never grant plans manually without recording a `subscription_events` row.

## 8. Data protection (LGPD)

- Account deletion (`DELETE /api/account`) revokes sessions immediately and purges data after
  `Retention:AccountPurgeDelayHours`; payment and usage records are kept for accounting
  without personal data.
- Access/portability requests: until a self-service export exists **(planned)**, export the
  user's rows (projects, clips, usage, payments) with SQL filtered by `user_id` and generate
  signed URLs for their files.
- Retention: per plan (`retentionDays`) for sources and renders; pending uploads expire after
  `Retention__TempUploadHours`.
