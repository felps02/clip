# API contract

Base URL: `http://localhost:5080`. Public routes under `/api`, worker routes under
`/internal`. OpenAPI document at `/openapi/v1.json`, interactive UI at `/scalar`.

## Conventions

- JSON, `camelCase`, enums as lower-case strings (`"9:16"`, `"queued"`, `"reaction"`).
- Dates: ISO-8601 UTC. Seconds as decimal numbers (`123.45`).
- Errors: RFC 9457 `application/problem+json` — `{ type, title, status, detail, code, errors? }`,
  plus the optional extensions `instance` (request path) and `traceId`. `code` is
  machine-readable: `validation_failed` (400; `errors` maps camelCase field paths such as
  `editingPreferences.minClipSeconds` to messages), `unauthorized` (401), `invalid_credentials`
  (401), `forbidden` (403), `email_not_confirmed` (403), `account_deletion_pending` (403),
  `not_found` (404), `conflict` (409), `quota_exceeded` (409), `plan_limit` (409; 413 for the
  file size), `payments_not_configured` (409), `job_not_cancellable` (409), `email_taken` (409),
  `invalid_token` (400), `invalid_signature` (400, payment webhooks), `payload_too_large` (413),
  `invalid_media` (422; 409 from the worker's `/probe`), `source_not_supported` (422),
  `locked_out` (423), `rate_limited` (429), `internal_error` (500), `payment_provider_error` (502),
  `assistant_unavailable` (503).
- Pagination: `?page=1&pageSize=20` → `{ items, page, pageSize, total }`. Defaults: page 1,
  pageSize 20; pageSize is capped at 100 and the echoed `pageSize` is the effective value
  (missing or non-positive values fall back to the defaults).
- Auth: HttpOnly cookies `cf_access` (JWT, 15 min, path `/`) and `cf_refresh` (30 days, path
  `/api/auth`), or `Authorization: Bearer <jwt>`. Browser calls use `credentials: "include"`.
  On `401` the web client calls `POST /api/auth/refresh` once and retries; if the retry is
  still `401` it calls `POST /api/auth/logout` (clears both cookies) before showing the login
  page. Because `cf_access` expires with the JWT, the login and register pages call
  `POST /api/auth/refresh` once on load and continue to the app when it succeeds and the web
  host can see the access cookie (they ask the web's own `GET /auth/handoff` first, so a session
  the web host cannot see never becomes a redirect loop); right after "Sair"
  (`/login?signedOut=1`) they skip that probe. The presence of `cf_access` is never treated as
  proof of a session. The web's server-side guard reads `cf_access`, so in production the cookie
  must reach the web host: same host, or `Auth__CookieDomain` covering web and API hosts;
  otherwise the web is built with `NEXT_PUBLIC_COOKIE_GUARD=false` and only its client-side
  guard (`GET /api/auth/me`) applies. With `Auth:CookieDomain` set (e.g. `example.com`) both
  cookies are written and cleared with that `Domain` attribute; host-only copies left from
  before the setting are expired in the same responses. Empty (default) = host-only cookies.
- Companion (OBS) endpoints under `/api/live` authenticate with a device token instead
  (`Authorization: Bearer cfd_…`) and refuse the user's cookie/JWT; every other endpoint refuses
  device tokens (see Live).
- Rate limits (fixed one-minute windows, all configurable in `RateLimiting`; `429 rate_limited`
  with `Retry-After: 60`): credential endpoints — `register`, `login`, `forgot-password`,
  `reset-password`, `verify-email`, `resend-verification`, `change-password` and
  `DELETE /api/account` — 10 req/min per IP (`AuthPerMinute`); `POST /api/auth/refresh` its own
  60 req/min per IP (`RefreshPerMinute`); `GET /api/auth/me` and `POST /api/auth/logout` are not
  limited; uploads (`/api/media/uploads*`) 30 req/min per user (`UploadsPerMinute`); signed
  transfers (`PUT /api/storage/upload`) 600 req/min per IP (`TransfersPerMinute`); the assistant
  chat (`POST /api/assistant/chat`) 20 req/min per user (`AssistantPerMinute`). Behind a
  reverse proxy the client IP is taken from `X-Forwarded-For` only when the proxy is listed in
  the `ReverseProxy` section (`docs/SETUP_PROVIDERS.md`).
- Idempotency: `Idempotency-Key` header on `POST /api/projects` (stored as
  `clientRequestId`; repeated key returns the original `201` payload).
- Signed URLs (`downloadUrl`, `streamUrl`, `thumbnailUrl`, `previewUrl`) expire in minutes;
  clients must re-fetch the resource to get fresh ones.

## Health

| Method | Path | Response |
|---|---|---|
| GET | `/health` | Liveness (unchanged): `200 { status: "ok", storage: "Local"\|"S3", time }` |
| GET | `/health/ready` | Readiness: the database answers `SELECT 1` and the object storage is usable (Local: the root directory exists; S3: a one-key listing of the bucket), each within 3 s, checked in parallel. `200 { status: "ready", checks: { database: "ok", storage: "ok" } }` or `503 { status: "not_ready", checks: { database, storage } }` where a failing check is `failed` (it threw; the reason is only logged) or `timeout`. Anonymous, not rate limited |

## Auth — `/api/auth`

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/register` | `{ email, password, displayName, acceptedTerms: true }` | `201 { user }` + cookies (or `202` message when e-mail confirmation is required); `409 email_taken` |
| POST | `/login` | `{ email, password }` | `200 { user }` + cookies; `401 invalid_credentials`; `423 locked_out`; `403 email_not_confirmed`; `403 account_deletion_pending` (only after the right password: a wrong password on such an account is the plain `401`) |
| POST | `/logout` | – | `204`, revokes refresh token, clears cookies |
| POST | `/refresh` | – (cookie) | `200 { user }` + rotated cookies; `401 unauthorized` (missing, expired, revoked or reused token) also expires both cookies |
| GET | `/me` | – | `200 { user, profile, subscription: { planCode, planName, status, currentPeriodStart, currentPeriodEnd, cancelAtPeriodEnd }, limits }` (`limits` = the plan's `PlanLimits`, including `assistantMessagesPerMonth`) |
| POST | `/forgot-password` | `{ email }` | `202` always |
| POST | `/reset-password` | `{ email, token, newPassword }` | `204`; `400 invalid_token` |
| POST | `/verify-email` | `{ userId, token }` | `204`; `400 invalid_token` |
| POST | `/resend-verification` | – | `202` |
| POST | `/change-password` | `{ currentPassword, newPassword }` | `204` (revokes other sessions); `401 invalid_credentials`; `423 locked_out` — wrong current passwords count toward the login lockout |

`user`: `{ id, email, displayName, emailConfirmed, createdAt }`.
Password policy: ≥ 8 chars, at least one letter and one digit. Lockout: `Auth:LockoutMaxFailedAttempts`
(5) wrong passwords — on login, change-password or account deletion — lock the account for
`Auth:LockoutMinutes` (5).

Refresh tokens rotate on every `/refresh` and a token rotates exactly once: the API revokes it
with a conditional update, so of several requests presenting the same token (even at the same
instant) at most one gets a new session. Presenting a token that was already rotated — late or
concurrently — is treated as theft and revokes every session of the user.

## Account — `/api/account`

| Method | Path | Notes |
|---|---|---|
| GET | `/profile` | `{ displayName, locale, timezone, editingPreferences, notificationPreferences }` (both preference objects complete, defaults filled in) |
| PUT | `/profile` | same shape, every field optional; nested preference objects are merged field by field (omitted fields keep the stored value); invalid values → `400 validation_failed` |
| GET | `/usage` | `{ plan: { code, name, limits }, period: { start, end }, used: { processingSeconds, renders, storageBytes, activeProjects, assistantMessages }, remaining: { processingSeconds, renders, storageBytes, activeProjects, assistantMessages }, history: [ { periodStart, periodEnd, processingSeconds, renders } ] }` — a `remaining` entry is `null` when the plan has no limit for it (`assistantMessages`: `assistantMessagesPerMonth` 0); `used.assistantMessages` counts the answered assistant messages of the period (usage kind `assistant_message`) |
| DELETE | `/` | `{ password }` → `202` schedules deletion; revokes sessions. `401 invalid_credentials`, `423 locked_out` (wrong passwords count toward the lockout); rate limited like the credential endpoints |

`editingPreferences` — the starting values of every new project in the web:

| Field | Values | Default |
|---|---|---|
| `aspectRatio` | `"9:16"` \| `"16:9"` \| `"1:1"` | `"9:16"` |
| `subtitlesEnabled` | bool | `true` |
| `subtitleStyle` | `"bold"` \| `"clean"` \| `"boxed"` | `"bold"` |
| `subtitlePosition` | `"bottom"` \| `"center"` \| `"top"` | `"bottom"` |
| `minClipSeconds` | int, 5–300 | `15` |
| `maxClipSeconds` | int, `minClipSeconds` ≤ value ≤ 300 | `60` |
| `targetClipSeconds` | int, between `minClipSeconds` and `maxClipSeconds` | `30` |
| `language` | `"auto"` \| `"pt"` \| `"en"` \| `"es"` — the language spoken in the recordings (transcription), not the interface language | `"auto"` |

`notificationPreferences` — e-mail delivery only (e-mails only go to a confirmed address); in-app
notifications are always created and cannot be turned off:

| Field | Default |
|---|---|
| `emailOnJobCompleted` | `false` |
| `emailOnJobFailed` | `true` |
| `emailOnQuotaWarning` | `true` |
| `emailOnBilling` | `true` |

Invalid values → `400 validation_failed` with `errors` keyed by field
(`editingPreferences.aspectRatio`, `editingPreferences.targetClipSeconds`, …); the relations
min ≤ target ≤ max are checked on the merged document (omitted fields at their stored values),
and nothing is written when any field is invalid. Documents stored before a field existed are
returned with that field at its default (a stored target length outside the default range widens
min/max to keep it). New projects created without `settings` start from `editingPreferences`
(format, subtitles, clip lengths, language), capped by the plan (e.g. `bold`/`bottom` subtitles
without `allowCustomSubtitleStyles`). What the e-mail switches control: see Notifications.

`locale` is informational: the interface exists only in pt-BR and the web always sends
`"pt-BR"`. The web's editor offers clip lengths of 10–120 s (the range of its new-project form).
When the plan's `allowCustomSubtitleStyles` is false, every web form (these preferences, new
project, re-analysis, clip from a candidate, clip editor) disables the subtitle styles other than
`bold` and the positions other than `bottom`, which the API would replace with `bold`/`bottom`
anyway (likewise the `webcam-split` framing for `allowWebcamLayout`); a saved preference the plan
does not include is kept and flagged as not applied. The API validates the full ranges above.

## Media uploads — `/api/media`

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/uploads` | `{ fileName, sizeBytes, contentType, declaredDurationSeconds? }` | `201 { asset: { id, status }, upload: { method: "PUT", url, headers, expiresAt }, warnings: [ "..." ] }` — `413 plan_limit` when size exceeds the plan; `409 quota_exceeded` when the storage quota would be exceeded; `warnings` may contain a quota warning based on `declaredDurationSeconds` (hint only, not enforced) |
| POST | `/uploads/{assetId}/complete` | – | `200 { asset }` with `status: "uploaded"` (idempotent once uploaded); `422 invalid_media` if missing/size mismatch/unknown container |
| GET | `/{assetId}` | – | `200 { asset }` |
| DELETE | `/{assetId}` | – | `204`; `409 conflict` while referenced by a project |

Accepted containers (magic bytes): MP4/MOV (`ftyp`), Matroska/WebM (EBML `1A 45 DF A3`),
plus `audio/*` (MP3 `ID3`/frame sync, WAV `RIFF`) for audio-only sources. Accepted
content types: `video/mp4`, `video/quicktime`, `video/x-matroska`, `video/webm`, `audio/mpeg`,
`audio/wav` (also `audio/x-wav`, `audio/wave`), `application/octet-stream` (validated by magic
bytes).

`asset`: `{ id, kind, originalFileName, contentType, sizeBytes, status, durationSeconds,
width, height, fps, container, videoCodec, audioCodec, hasAudio, createdAt, uploadedAt,
expiresAt }` (`expiresAt`: when a pending upload expires).

### Local storage transfer endpoints (only when `Storage:Provider=Local`)

`PUT /api/storage/upload?key=&exp=&sig=&max=` (raw body streamed to disk; anonymous but
signed) and `GET /api/storage/download?key=&exp=&sig=&name=` (supports `Range`, sets
`Content-Disposition` when `name` is given). Signature = HMAC-SHA256 over
`"{method}|{key}|{exp}|{max}"` (upload) or `"{key}|{exp}"` (download) with `Storage:SigningKey`.
The PUT is not part of the per-user `uploads` budget (the signature already bounds who can call
it and `max` bounds the size): it has its own per-IP window, `RateLimiting:TransfersPerMinute`
(600), sized for the burst of clips, thumbnails and analysis files a worker uploads per job.

The PUT is also bound to the state of its target, not only to the signature: a source upload
(`users/{userId}/sources/{assetId}/…`) is accepted only while its asset is `pending`, a job
artifact (`users/{userId}/jobs/{jobId}/…`) only while the job is running. Once `/complete`
verified the file, or the asset was deleted or expired, the still-valid URL answers
`409 conflict` and the object cannot change or come back (the check is repeated, under the
per-user lock `/complete` and the deletion take, when the uploaded file is moved into place).
Responses: `200 { key, sizeBytes }`; `403 forbidden` (bad or expired signature, or a key that
is not an upload target); `409 conflict`; `413 payload_too_large` (body larger than `max`).

## Sources — `/api/sources`

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/validate` | `{ url }` | `200 { platform: "twitch"|"youtube"|"kick"|"unknown", supported: false, reason, alternatives: [ { kind: "upload", title, description }, { kind: "obs_companion", ... } ], metadata?: { title, durationSeconds, thumbnailUrl } }` |

In the MVP no platform supports direct processing (no scraping, official APIs don't allow
VOD download for third parties). `metadata` is only filled when an official API key is
configured. The web never shows a "process this link" button unless `supported` is `true`.

## Projects — `/api/projects`

| Method | Path | Body / query | Response |
|---|---|---|---|
| POST | `/` | `{ title?, source: { kind: "upload", mediaAssetId } \| { kind: "link", url }, settings?: AnalysisSettings }` | `201 { project, job }` (without `settings`: the profile's `editingPreferences`, capped by the plan); `400 validation_failed` (also an `Idempotency-Key` over 128 characters); `404 not_found` (unknown asset); `422 invalid_media` (upload not completed); `409 conflict` (the asset backs another project); `422 source_not_supported`; `409 plan_limit` (active projects / concurrent jobs). Processing minutes are checked when the worker probes the file (`quota_exceeded` then fails the job), not here |
| GET | `/` | `?page&pageSize&status` | paged `{ items: ProjectSummary[] }` |
| GET | `/{id}` | – | `{ project, jobs: JobSummary[], candidateCount, clipCount, source: { asset, previewable: bool, previewUrl? } }` |
| PATCH | `/{id}` | `{ title }` | `200 { project }` |
| DELETE | `/{id}` | – | `202` |
| POST | `/{id}/jobs` | `{ settings? }` | `201 { job }` (re-analyse with new settings; without them the project's settings); `409 conflict` while an analysis of the project is active; `422 invalid_media` (source no longer usable); `409 plan_limit` (concurrent jobs; for a `failed` or `cancelled` project also the active-projects cap, since the analysis makes it active again) |
| GET | `/{id}/jobs/{jobId}` | – | `Job` (with `events: last 20`) |
| GET | `/{id}/jobs/{jobId}/events` | – | `{ items: JobEvent[] }` |
| POST | `/{id}/jobs/{jobId}/cancel` | – | `202 { job }` (queued → cancelled at once; running → the worker aborts at its next heartbeat; once the analysis started the reserved minutes are consumed, see `docs/PLANS_AND_BILLING.md`); `409 job_not_cancellable` |
| GET | `/{id}/candidates` | `?sort=score\|time&category` | `{ items: Candidate[] , signals: { transcript: { available }, audio: { available }, visual: { available } } }` |
| POST | `/{id}/candidates/{candidateId}/clips` | `{ title?, aspectRatio?, subtitles?, framing?, startSeconds?, endSeconds? }` | `202 { clip: ClipSummary, job: Job }` (creates clip + `render_clip` job); `409 plan_limit` (renders/month or concurrent jobs); `409 conflict` after 10 clips of the same candidate; `404 not_found` (unknown candidate); `422 invalid_media` (source no longer usable) |
| GET | `/{id}/clips/archive` | – | `200 application/zip`, streamed, with `Content-Disposition: attachment; filename="<project title slug>-clipes.zip"`: one entry `NN-<clip title slug>.mp4` per clip (not deleted) whose current render is ready, best first (score, then creation order); `404 not_found` when the project has no ready clip (and for another user's project). Not a render: no quota is consumed |

The archive is written straight to the response (`ZipArchive` in Create mode, stored entries —
`CompressionLevel.NoCompression`, clips are already compressed video): each render is read from
`IObjectStorage` and copied into its entry as it is read, so neither the archive nor a whole clip is
held in memory (only a few bytes of zip headers). Slugs are lower-case ASCII letters, digits and
dashes (accents folded), so the file names need no escaping. A render whose storage object is gone is
left out (logged); a failure after the response started aborts the connection, so a truncated
download never looks like a complete zip. The web links it with a plain `<a href>` built from the API
base URL: a top-level navigation sends the `SameSite=Lax` `cf_access` cookie.

`jobs` in `GET /{id}` has no guaranteed order. Clients pick the job to track by state, not by
position: the web shows the running `analyze_source` job if any, otherwise the one with the
greatest `updatedAt`.

Plan caps (active projects, concurrent jobs, renders, storage) are checked and the row inserted
under a per-user lock, so parallel requests of one user queue up instead of all passing the check.

`Project` (`project` of `POST /`, `GET /{id}`, `PATCH /{id}`): `{ id, title, sourceKind,
sourcePlatform, sourceUrl, status, settings: AnalysisSettings, mediaAssetId, createdAt,
updatedAt }`. The source duration is `source.asset.durationSeconds` of `GET /{id}`.

`AnalysisSettings`: `{ minClipSeconds (5–300, default 15), maxClipSeconds (min–300, 60),
targetClipSeconds (5–300, 30), sensitivity (0–100, 50), maxCandidates (1–100, 20),
autoRenderTop (0–50, default = maxCandidates), aspectRatio ("9:16"|"16:9"|"1:1"), subtitles: { enabled, style:
"bold"|"clean"|"boxed", position: "bottom"|"center"|"top" }, language ("auto" or a language code
such as "pt", "pt-BR"), framing: { mode: "auto"|"center"|"webcam-split", focusX?, focusY? (0–1),
webcamRegion? }, title?, focusCategories (string[], default []), customKeywords (string[], default []),
instructions (string | null, default null) }`. Stored settings are capped by the plan (candidates,
auto-renders, subtitle style and position, webcam layout) and always carry explicit `focusX`/`focusY`
and a number in `autoRenderTop`.

- `autoRenderTop` omitted or `null` = every candidate found becomes a clip (the web shows "Todos os
  momentos encontrados"): it takes the value of `maxCandidates` (after the plan cap on candidates) and
  is then capped by the plan's `maxAutoRenderPerJob` (free 15, creator 30, pro 50) like an explicit
  value; it never exceeds `maxCandidates`.
- `focusCategories`: a subset of `highlight|reaction|funny|hype|tension|gameplay|quote`
  (case-insensitive, duplicates removed, order kept). An unknown category is `400 validation_failed`
  (`errors["settings.focusCategories[i]"]`). The worker favours these categories (fusion weights and
  rank priority, reported in `signals.focus` — `docs/PROCESSING_PIPELINE.md`).
- `customKeywords`: words to find in the speech, matched like the built-in lexicon. At most 20
  distinct entries, each 1–40 characters after trimming; inner whitespace is collapsed and entries are
  deduplicated case-insensitively (the first spelling is kept). More than 20 distinct entries, an empty
  entry or one over 40 characters is `400 validation_failed` (`settings.customKeywords…`).
- `instructions`: what the user asked in their own words (the web stores the assistant conversation's
  last request), at most 500 characters (trimmed; empty → `null`; longer → `400 validation_failed`).
  Only a worker with an LLM provider uses it; otherwise it is stored and ignored.

The three fields are validated on `POST /api/projects` and `POST /api/projects/{id}/jobs`, stored in
`project.settings` and passed through, camelCase, in the worker's `JobPayload.settings`.

`ProjectSummary`: `{ id, title, sourceKind, sourcePlatform, status, createdAt, updatedAt,
durationSeconds, latestJob: JobSummary, candidateCount, clipCount, thumbnailUrl? }`.

`JobSummary` (every list of jobs: `latestJob`, `jobs` of a project, a clip's `activeJob`, the
dashboard's `activeJobs`, `GET /api/jobs`): `{ id, projectId, projectTitle, kind, status,
progress, statusMessage, updatedAt }`. The dashboard links each active job to
`/projects/{projectId}`.

`Job`: `{ id, projectId, kind, status, progress, statusMessage, attempt, maxAttempts,
errorCode, errorMessage, cancelRequested, queuedAt, startedAt, finishedAt, createdAt, updatedAt,
result?: { candidatesFound, clipsRendered, processedSeconds, signals, webcamRegion?, language? },
events? }` — `events` (`JobEvent[]`: `{ id, createdAt, level, fromStatus, toStatus, message,
data }`) only in `GET /{id}/jobs/{jobId}` (last 20), `null` elsewhere; `webcamRegion` is
`{ x, y, w, h, confidence? }` (normalised 0–1).

`Candidate`: `{ id, rank, startSeconds, endSeconds, durationSeconds, category, score,
reasoning, transcript, signals, status, clipId?, previewUrl? (signed source URL; the client
uses media fragments `#t=start,end`), previewable }`.

## Clips — `/api/clips`

| Method | Path | Body / query | Response |
|---|---|---|---|
| GET | `/` | `?page&pageSize&projectId&category&status&q&sort=createdAt\|score\|duration&order=asc\|desc` | paged `{ items: ClipSummary[] }` |
| GET | `/{id}` | – | `ClipDetail` |
| PATCH | `/{id}` | `{ title?, startSeconds?, endSeconds?, aspectRatio?, subtitles?, framing? }` | `200 { clip, needsRender: bool }` |
| POST | `/{id}/render` | – | `202 { clip, job, render }`; `409 plan_limit` (renders/month or concurrent jobs); `409 conflict` while a render of this clip is pending |
| GET | `/{id}/download` | `?renderId?` | `200 { url, expiresAt, fileName }`; `404 not_found` (the clip has no render, or `renderId` is not one of its renders); `409 conflict` (the render is not ready yet) |
| DELETE | `/{id}` | – | `204` |

`ClipSummary`: `{ id, projectId, projectTitle, title, category, score, status,
aspectRatio, durationSeconds, startSeconds, endSeconds, thumbnailUrl?, createdAt,
updatedAt, currentRender?: Render }`.

`Render`: `{ id, version, aspectRatio, width, height, sizeBytes, durationSeconds, hasSubtitles,
status, error, createdAt, completedAt, streamUrl, downloadUrl }` — `streamUrl`/`downloadUrl`
are signed URLs only on `ClipDetail.currentRender` and `null` elsewhere.

`ClipDetail` = `ClipSummary` + `{ subtitles, framing, reasoning, transcript,
currentRender?: Render (with streamUrl, downloadUrl), renders: Render[], source: { previewable,
previewUrl?, durationSeconds }, activeJob?: JobSummary }`. `reasoning`/`transcript` come from
the clip's candidate and are `null` once the candidate is gone (e.g. replaced by a new analysis).

## Assistant — `/api/assistant`

The chat on `/new` and in "Analisar novamente" that changes the analysis settings with the product's
own model (llama.cpp, default) or Google's free Gemini API (ADR-010). No paid provider is wired. Both
endpoints require authentication.

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/status` | – | `200 { available, provider: "local"\|"gemini"\|"none", model, reason? }` — `reason` (pt-BR) when unavailable: provider `none`, server down or still loading the model, Gemini key missing/invalid or free quota exhausted. The probe of the provider is cached `Assistant:StatusCacheSeconds` (10 s). Never carries keys or server paths: `model` is a label (`Assistant:Local:Model`, else the model file name, e.g. `Qwen3-1.7B-Q4_K_M`) |
| POST | `/chat` | `{ messages: [ { role: "user"\|"assistant", content } ], currentSettings: AnalysisSettings (partial: missing fields take their defaults), context?: { fileName?, durationSeconds? } }` | `200 { reply, settingsPatch, changes: [ { field, label, value } ], provider, model }`; `400 validation_failed` (1–20 messages, each 1–2000 non-blank characters, roles `user`/`assistant`, the last one from the user); `409 plan_limit` (`assistantMessagesPerMonth` reached); `429 rate_limited` (20 per minute per user, `Retry-After: 60`); `503 assistant_unavailable` (see `/status`, or the call timed out after `Assistant:TimeoutSeconds`) |

How the answer is produced — the model never writes the text the user reads (small models hallucinate
in free text: "clipes ajustados" without any change, "postar no Instagram"):

1. **Model** (structured output only): `{ intent: "configure"|"question"|"help"|"other", patch: { only
   the fields the user asked to change }, unsupported: ["auto_post"|"music"|"effects"|"edit_video"|"other"],
   question: string|null }`, constrained by a JSON schema (llama.cpp `response_format: json_schema`,
   Gemini `responseSchema`). The system prompt (pt-BR, with few-shot examples) is byte-stable; the
   current settings, the plan caps and the file duration go in the last user message, after the request.
2. **Rule layer** (deterministic, on the last user message): TikTok/Reels/Shorts/Kwai → `9:16`,
   YouTube → `16:9`, quadrado/feed → `1:1`; "curtinhos/mais curtos/prefiro curtos" → 10/15/25 s,
   "mais longos" → 45/60/90 s, "N segundos" (digits or words, "meio minuto", "1 minuto") → target N with
   a range around it (30 s → 20–40 s), "entre A e B segundos", "no máximo/no mínimo N s" ("não quero mais
   de N s" is a maximum); legenda no meio/centro → `center`, em cima/topo → `top`, embaixo → `bottom`,
   sem legenda / não quero legenda → `subtitles.enabled` false, legenda limpa/com caixa/negrito → style;
   engraçado/risada/zoeira → `funny`, comemoração/hype → `hype`, jogada/clutch/kill → `gameplay`, frase
   marcante → `quote`, tensão/suspense → `tension`, reação/susto → `reaction` ("também" adds to the
   current focus, "sem X" removes X); quoted words, "quando eu falar X", "a palavra X" and
   "palavras-chave (são|:) X, Y e Z" → `customKeywords` (an unquoted list ends where another request
   starts — an imperative such as "coloca"/"deixa"/"quero" or a setting word such as "legenda"/"clipes" —
   so "marca a palavra gg e coloca legenda no meio" lists only "gg"); inglês/português/espanhol →
   `language`; "N clipes" → `autoRenderTop`, "N momentos" → `maxCandidates`; mais/menos sensível →
   `sensitivity` ±20; webcam → `webcam-split`, "sem/não quero/nada de (o layout de) webcam" → no webcam
   layout; posting, music, effects/filters/transitions, editing the video and translation → unsupported.
   Rules decide every field they detect (they win on explicit platforms and numbers); a field from the
   model is kept only when the message talks about that setting, so copied context values and invented
   fields are dropped.
   - **Negated mentions are never applied** ("não quero clipes curtos", "evita clipes curtos", "nada de
     legenda no topo", "a legenda não pode ficar no meio", "a live não é em inglês", "não quero webcam",
     "não quero mais sensível", "sem música"): "sem", "nada de", "evita", "tira"… negate the phrase right
     after them; "não", "nem", "nunca", "nenhum" the rest of their clause (it ends at punctuation, at
     "e"/"mas" and where a new request starts: "não sei mexer nisso, coloca a legenda em cima"); a
     trailing "não" too ("clipes curtos não"). A setting mentioned only that way keeps its current value
     (the reply says it is already set) unless that value is the refused one; then it takes the first
     value the user did not refuse: lengths 15/30/60 s, framing `auto` → `center`, subtitle position
     `bottom` → `center` → `top`, style `bold` → `clean` → `boxed`, language `auto`, format `9:16` →
     `16:9` → `1:1`. Refusing a subtitle position or style never turns the subtitles off ("não quero
     legenda" with no position or style after it does). A refused keyword is taken out of the list ("tira a
     palavra gg"); a refused unsupported request needs no explanation. Refused values are also dropped from
     the model's patch, so the model cannot bring them back. Not negations: "não só", "não esquece de",
     "não quero perder", "não pode faltar", "por que não", "não tem problema", "não quero mais de N" (a
     bound) and questions about a capability ("não dá pra colocar a legenda em cima?").
   - Setting and moment-type words ("legenda", "os clipes curtos", "engraçados") are never keywords, from
     the rules or from the model; game terms the user names as words ("a palavra clutch") are.
3. **Validation**: the merged request is applied to `currentSettings` and checked like project settings
   (the same validator and plan caps): clip lengths 5–120 s with min ≤ target ≤ max, `maxCandidates`
   and `autoRenderTop` capped by the plan, subtitle styles/positions other than `bold`/`bottom` only
   with `allowCustomSubtitleStyles`, `webcam-split` only with `allowWebcamLayout`, at most 20 keywords
   of 1–40 characters. What is dropped or capped is explained in the reply.
4. **Reply** (pt-BR, composed by the server): the validated changes ("Pronto! Configurei: …", a few
   fixed openers), "Já estava assim: …" / "Isso já está configurado: …" for requested values that were
   already set (including a refusal that the current settings already satisfy: "não quero webcam" → "Isso
   já está configurado: enquadramento automático."), the reason for
   each dropped/capped value, a fixed sentence per unsupported request ("Ainda não consigo postar
   automaticamente: baixe os clipes e publique.") and — only when nothing changed — the model's
   clarifying question (one question, at most 160 characters, discarded when it offers something the
   product does not do). `intent: help` (or a greeting) → a fixed text listing what the assistant can
   change.

`settingsPatch` is a partial `AnalysisSettings` with only the fields that change: `focusCategories`
and `customKeywords` are complete new lists, the three clip lengths always travel together, and
`subtitles` / `framing` are partial objects to merge into the current ones. `changes[]` has one entry
per change: `field` is the settings field (`focusCategories`, `customKeywords`, `targetClipSeconds`
— or `minClipSeconds`/`maxClipSeconds` when the target did not move — for the length range,
`aspectRatio`, `subtitles.enabled`, `subtitles.position`, `subtitles.style`, `language`,
`sensitivity`, `maxCandidates`, `autoRenderTop`, `framing.mode`), `label` the pt-BR chip text
("Foco: momentos engraçados", "Duração: 20–40 s (alvo 30 s)", "Formato: 9:16 (vertical)") and `value`
the new value.

Each answered message is one `assistant_message` usage record counted against the plan's
`assistantMessagesPerMonth` (free 100, creator 1000, pro 5000; 0 = unlimited). It is reserved under
the per-user lock before the model is called (parallel requests cannot exceed the plan) and given back
when no answer is produced (`503`, aborted request). See `GET /api/account/usage`
(`used.assistantMessages`, `remaining.assistantMessages`) and `GET /api/auth/me`
(`limits.assistantMessagesPerMonth`).

Example — `POST /api/assistant/chat` with `{ messages: [ { role: "user", content: "quero clipes mais
engraçados, de uns 30 segundos, pro tiktok" } ], currentSettings: { …, aspectRatio: "16:9",
minClipSeconds: 15, targetClipSeconds: 45, maxClipSeconds: 60 } }`:

```json
{
  "reply": "Feito! Novas configurações: foco em momentos engraçados, clipes de 20 a 40 s (alvo de 30 s) e formato 9:16 (vertical).",
  "settingsPatch": { "focusCategories": ["funny"], "minClipSeconds": 20, "maxClipSeconds": 40, "targetClipSeconds": 30, "aspectRatio": "9:16" },
  "changes": [
    { "field": "focusCategories", "label": "Foco: momentos engraçados", "value": ["funny"] },
    { "field": "targetClipSeconds", "label": "Duração: 20–40 s (alvo 30 s)", "value": 30 },
    { "field": "aspectRatio", "label": "Formato: 9:16 (vertical)", "value": "9:16" }
  ],
  "provider": "local",
  "model": "Qwen3-1.7B-Q4_K_M"
}
```

## Dashboard & jobs

| Method | Path | Response |
|---|---|---|
| GET | `/api/dashboard/summary` | `{ clipsTotal, clipsThisPeriod, activeJobs: JobSummary[] (with project title), minutesProcessedThisPeriod, recentClips: ClipSummary[] (6), usage: { processingSecondsUsed, processingSecondsLimit, rendersUsed, rendersLimit, storageBytesUsed, storageBytesLimit }, warnings: [ { code, message } ] }` |
| GET | `/api/jobs?status=active\|all&page&pageSize` | paged jobs across projects |

## Notifications — `/api/notifications`

| Method | Path | Response |
|---|---|---|
| GET | `/?unreadOnly=true&page&pageSize` | paged `{ items: [ { id, type, title, body, data, readAt, createdAt } ] }`, newest first |
| GET | `/unread-count` | `{ count }` |
| POST | `/{id}/read` | `204`; `404 not_found` (unknown id or another user's notification) |
| POST | `/read-all` | `204` |

`type`: `job_completed`, `job_failed`, `clip_ready`, `quota_warning`, `billing`, `system`.
`data` is type-specific (`{ jobId, projectId, … }` for jobs, `{ threshold, usedSeconds,
limitSeconds, periodStart, periodEnd }` for `quota_warning`).

In-app notifications are always created. An e-mail copy is sent to the account address when the
user's `notificationPreferences` switch for the topic is on, the address is confirmed
(`emailConfirmed`) and the account is not being deleted. Registration does not require a
confirmed address (`Auth:RequireConfirmedEmail` defaults to false) and these e-mails carry text
the account chose (project titles, display name), so until the owner confirms the address with
the verification e-mail sent at registration only the in-app notification is created (nothing is
queued for later). The switches: `emailOnJobCompleted` — an `analyze_source` job completed;
`emailOnJobFailed` — an analysis or a render failed for good (not a retryable failure that is
re-queued);
`emailOnQuotaWarning` — `quota_warning`, raised when the processing minutes used in the current
period reach 80 % and then 100 % of the plan's monthly minutes (checked whenever minutes are
charged; once per threshold and period, and a jump straight past 100 % raises only the 100 %
warning); `emailOnBilling` — plan activated or changed, payment failed, cancellation scheduled,
subscription ended or suspended (from the payment webhooks). E-mails are pt-BR, link to
`Frontend:BaseUrl` (`/projects/{id}`, `/clips/{id}`, `/billing`) and name the switch that caused
them. They leave after the change is committed, through a background queue: a slow or failing
mail server never fails or delays the request, and failures are only logged.

## Plans & billing

| Method | Path | Notes |
|---|---|---|
| GET | `/api/plans` | public: `{ items: [ { code, name, description, priceMonthlyCents, currency, limits, features, isCurrent?, purchasable } ] }` — `isCurrent` is `null` for anonymous calls; `purchasable` is true when the plan has a positive price and a provider price id. A plan priced at 0 is free (the web shows "Grátis"; `null` = "Sob consulta") and is never purchasable, even with a provider price id configured; `limits` include `assistantMessagesPerMonth` |
| GET | `/api/billing/subscription` | `{ subscription, plan, paymentsConfigured: bool, portalAvailable: bool }` |
| POST | `/api/billing/checkout` | `{ planCode, successUrl?, cancelUrl? }` → `{ checkoutUrl, planChanged }`. New subscription: `checkoutUrl` is the provider checkout page. Active paid subscription: the provider subscription is switched to the new plan in place (prorated), `planChanged: true` and `checkoutUrl` is the success page — the entitlement changes when the provider's subscription-updated webhook arrives. `409 conflict` for the current plan, a plan without a price or a free plan (price 0 — never sent to the payment provider; returning to free is `POST /cancel`); `404 not_found` for an unknown or inactive plan; `409 payments_not_configured`; `502 payment_provider_error` when the provider refuses the change |
| POST | `/api/billing/portal` | `{ url }` (provider customer portal); `409 conflict` when the user has no provider customer; `409 payments_not_configured` |
| POST | `/api/billing/cancel` | `202` (cancel at period end); `409 conflict` without a paid subscription; `409 payments_not_configured` |
| GET | `/api/billing/payments` | paged payment events visible to the user `{ id, type, amountCents?, currency?, status, createdAt }` |
| POST | `/api/webhooks/stripe` | raw body + `Stripe-Signature` → `200 { received: true, status }` once the event is stored and handled (`status`: `processed` or `ignored`; a duplicate of a handled event answers the same without being applied again); `500 internal_error` when processing fails — the event stays stored with `status=failed`, so the provider redelivers it and the redelivery is processed again; `400 invalid_signature`; `409 payments_not_configured` (no webhook secret) |

Entitlements change only through the webhooks and only for a confirmed payment
(`docs/PLANS_AND_BILLING.md`): `checkout.session.completed` must have `payment_status` `paid` or
`no_payment_required`, and a provider subscription is bound to the user and grants its plan only
while `active` or `trialing` — never while `incomplete` (Checkout creates it before the first
invoice is paid). `past_due` keeps the plan as a grace period; `unpaid`, `canceled`,
`incomplete_expired` and `paused` return the user to the free plan.

The provider does not deliver events in order, and a failed event is redelivered later, so a late
event never undoes newer state; such an event is answered `200` with `status: ignored`:

- A cancelled subscription stays cancelled: after `customer.subscription.deleted` (or status
  `canceled`) nothing about that subscription changes the user's plan (the provider never
  reactivates a canceled subscription; buying again creates a new one).
- A `customer.subscription.*` event created before the newest one already applied to the
  subscription (Stripe `Event.Created`; one-second resolution, events of the same second apply in
  arrival order) changes nothing — e.g. a `past_due` delivered after `unpaid`.
- `invoice.paid` / `invoice.payment_failed` older than that newest subscription event change
  nothing; a late `invoice.payment_failed` is still announced while the subscription is
  `past_due`.
- `checkout.session.completed` grants nothing when the subscription's row lapsed since
  (`expired`/`cancelled`), and on a row its subscription events already bound it leaves status,
  cancellation and period to them.
- A checkout or an `active` subscription event of a subscription the provider already reported
  deleted is never bound to the user — also when the deletion arrived before anything bound it.

## Live (phase 5) — `/api/live`

Live capture from the creator's own OBS recording through the companion. The full contract —
companion behaviour, resume rules, worker payloads — is `docs/LIVE_CAPTURE.md`; this section is
what the API implements (`src/ClipForge.Api/Features/Live`).

**Two kinds of callers, never mixed.** User endpoints take the cookie/JWT like the rest of the
API; a device token there is `401`. Companion endpoints take only a device token
(`Authorization: Bearer cfd_…`, scheme `Device`); the user's cookie or JWT there is `401`.
Device tokens are `cfd_` + 40 random base62 characters, shown once at creation; the API keeps
their SHA-256 hex and the first 8 characters after the prefix (`tokenPrefix`). An unknown or
revoked token (or the token of an account being deleted) is `401 unauthorized`. Each
authenticated device request updates `lastSeenAt` (written at most every 30 s).

User endpoints (cookie/JWT):

| Method | Path | Body / query | Response |
|---|---|---|---|
| POST | `/companion/tokens` | `{ name }` (1–60 characters, trimmed) | `201 { device: Device, token }` — the only time the token is returned |
| GET | `/companion/devices` | – | `{ items: Device[] }` — active devices newest first, revoked ones last |
| DELETE | `/companion/devices/{id}` | – | `204` (also when already revoked); the device's `waiting`/`live`/`ending` sessions go to `error` with `endReason` "dispositivo revogado" (segments already received are still analysed); `404` for another user's device |
| GET | `/sessions` | `?page&pageSize&status` (`waiting\|live\|ending\|ended\|error`) | paged `{ items: LiveSession[] }`, newest first (sessions of deleted projects are hidden) |
| GET | `/sessions/{id}` | – | `LiveSessionDetail`; `404` for another user's session |
| POST | `/sessions/{id}/stop` | – | `202 { session }` with `stopRequested: true` (the companion ends the session when its heartbeat sees it); `409 conflict` when the session is already `ended`/`error` |

Companion endpoints (device token):

| Method | Path | Body | Response |
|---|---|---|---|
| GET | `/companion/me` | – | `200 { device: { id, name }, user: { displayName }, plan: { code, name } }` |
| POST | `/sessions` | `{ title?, segmentSeconds? (20–120, default 60), settings? (partial AnalysisSettings), obs?: { version?, recordingPath? } }` | `201 { session: LiveSession, settings }`; `400 validation_failed`; `409 plan_limit` when the device already has a `waiting`/`live`/`ending` session, or the plan's active-project / concurrent-job caps are reached |
| POST | `/sessions/{id}/heartbeat` | `{ state: { obsConnected, streaming, recording, uploading, queueLength, lastError } }` | `200 { status, stopRequested, nextSequence }` (`nextSequence` = highest stored sequence + 1); `409 conflict` (`quota_exceeded` when the minutes ran out) once the session is `ended`/`error` |
| POST | `/sessions/{id}/segments` | `multipart/form-data`: `sequence`, `startOffsetSeconds`, `durationSeconds`, `final?`, then `file` | `201 { segment: LiveSegment, duplicate: false }`; `200 { segment, duplicate: true }`; `400 validation_failed`; `409 conflict` (session terminal); `409 quota_exceeded`; `413 payload_too_large`; `422 invalid_media` |
| POST | `/sessions/{id}/end` | `{ lastSequence? }` | `200 { session }` — `waiting`/`live` → `ending`, or straight to `ended` when no segment job is pending; repeating it (or calling it on a terminal session) returns the session unchanged |

A session id of another user is `404` on every endpoint (the session is scoped to the token's
user, not to the device: a user's other device may end it).

- **Session creation.** Runs under the per-user lock like a project creation. The session owns
  a new project (`sourceKind: "live"`, `sourcePlatform: "obs"`, `status: "processing"`, title
  from the companion or "Live de dd/MM HH:mm" in the profile's time zone). `settings` are the
  given fields over the profile's `editingPreferences` (top-level fields replace; `subtitles`
  and `framing` merge field by field), validated like project settings
  (`errors["settings.<field>"]`) and capped by the plan. Live projects count as active
  projects. An open session (or one whose segments are still being analysed) takes **one**
  concurrent-job slot of the plan, however many segments are queued; its `live_segment` jobs
  are not counted one by one.
- **Statuses.** `waiting` (created) → `live` (first segment, or a heartbeat with
  `recording: true`) → `ending` (`/end`; segments may still arrive and are analysed) → `ended`
  once every `live_segment` job of the session is terminal. `error` from `waiting`/`live`/
  `ending`: device revoked ("dispositivo revogado"), monthly minutes exhausted ("minutos de
  processamento do mês esgotados"), companion silent ("companion offline", see below), project
  deleted ("projeto excluído"). `endReason` (pt-BR) is also set when the companion ends the
  session ("encerrada pelo companion", or "encerrada pelo usuário na página Lives" after a stop).
  Pending segments of an `error` session are still analysed.
- **Reaper.** Every `Live:ReaperIntervalSeconds` (30), a `waiting`/`live` session without a
  heartbeat nor a segment for `Live:SessionTimeoutMinutes` (10) goes to `error` "companion
  offline"; an `ending` session whose segment jobs are all terminal goes to `ended`.
- **Segment upload.** The text fields must come before `file` (the companion sends them first):
  `sequence` int 0–1 000 000, `startOffsetSeconds` ≥ 0, `durationSeconds` > 0 and ≤ 300
  (stored with 3 decimals), `final` `true|false`. Idempotent per `(session, sequence)`: a stored
  sequence answers `200 duplicate` with the stored segment, without storing anything (parallel
  retries of one sequence also end with one segment). Uploads are accepted while the session is
  `waiting`, `live` or `ending`. When the plan's monthly minutes are exhausted (used + reserved ≥
  limit) the session goes to `error` and the upload is `409 quota_exceeded`. The file must start
  with the Matroska/WebM (EBML `1A 45 DF A3`) or MP4 (`ftyp`) magic bytes (`422 invalid_media`
  otherwise) and is streamed to object storage as it arrives (never buffered whole), at most
  `Live:MaxSegmentMb` (512; `413 payload_too_large`, also when `Content-Length` already says
  so). Each segment creates a `media_assets` row (kind `live_segment`, status `uploaded`, key
  `users/{userId}/live/{sessionId}/seg_{sequence:00000}.{mkv|webm|mp4|mov}`), a `live_segments`
  row and one queued `processing_jobs` row (kind `live_segment`, input = the session's
  settings). Error answers are sent after the request body was read, so the companion gets the
  problem instead of a reset connection.

`Device`: `{ id, name, tokenPrefix, createdAt, lastSeenAt, revokedAt, online }` — `online` =
not revoked and `lastSeenAt` within 60 s.

`LiveSession`: `{ id, projectId, title, status, deviceId, deviceName, segmentSeconds,
startedAt, endedAt, lastSegmentAt, lastHeartbeatAt, online, stopRequested, endReason,
companionState: { obsConnected, streaming, recording, uploading, queueLength, lastError },
stats: { segmentsReceived, segmentsAnalyzed, segmentsFailed, highlights, recordedSeconds } }` —
`title` is the project's title; `online` = heartbeat within 30 s; `companionState` is the last
heartbeat (all `false`/`0` before the first one); `highlights` = clips of the session's project
(not deleted); `recordedSeconds` = sum of the stored segments' durations.

`LiveSessionDetail`: `{ session: LiveSession, settings: AnalysisSettings, segments:
LiveSegment[] (latest 50 by sequence, newest first), highlights: ClipSummary[] (clips of the
session's project, newest first, max 50), candidateCount }` (`candidateCount` = moments detected
by all the session's segments).

`LiveSegment`: `{ id, sequence, startOffsetSeconds, durationSeconds, status, jobId, error,
createdAt }` — `status` mirrors the segment's job: `received` (stored, before its job exists),
`queued`, `analyzing` (claimed), `analyzed` (completed), `failed` (failed or cancelled; `error`
holds the job's message). A failed segment raises no notification of its own (the Lives page
shows it); each clip a segment job registers raises a `clip_ready` notification "Novo destaque
na live" with `data { clipId, renderId, projectId, sessionId, jobId }`.

A live project's moments come from every segment job: `GET /api/projects/{id}/candidates` lists
the candidates of all of them (their commit ranges are disjoint) and `candidateCount` counts
them; re-analysing a live project is not possible (no single source file), but its clips can be
cut from a candidate and re-rendered like any other (`render_clip` then carries the session's
segments, see Internal). Deleting a live project sets its open session to `error`; purging it
deletes the segment files and assets.

## Internal — `/internal` (headers `X-Internal-Api-Key`, `X-Worker-Id`)

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/workers/{workerId}/claim` | `{ kinds: ["analyze_source","render_clip","live_segment"], capabilities?: {} }` (without `kinds`: `analyze_source` and `render_clip`) | `200 JobPayload` (the job is now `preparing_media`, leased to `workerId`; an `analyze_source` claim also moves the project to `processing`, a `render_clip` claim the render to `rendering`, a `live_segment` claim the segment to `analyzing`) or `204` (queue empty — also when the claimed job cannot be dispatched, e.g. its source is gone or its clip was deleted: that job is failed server-side with the reason). A `live_segment` job is claimable only when every lower-sequence job of the same session is terminal (`completed`/`failed`/`cancelled`), checked inside the same `FOR UPDATE SKIP LOCKED` statement |
| POST | `/jobs/{id}/heartbeat` | `{ progress? (0–100), message? (≤ 500) }` | `200 { cancelRequested, leaseExpiresAt }`; `409` if the lease belongs to another worker or the job is not running |
| POST | `/jobs/{id}/probe` | `{ durationSeconds, width, height, fps, hasAudio, hasVideo, container, videoCodec, audioCodec, sizeBytes }` | `200 { ok: true, remainingProcessingSeconds }` (this job's reservation — replaced on a re-sent probe or a later attempt, never counted twice — already deducted) or `409` problem with `code: quota_exceeded\|plan_limit\|invalid_media` — the job is already marked failed: `invalid_media` for `hasVideo: false` ("arquivo sem vídeo") and for `durationSeconds ≤ 0`, `plan_limit` for a source longer than the plan allows, `quota_exceeded` for not enough minutes left. A `live_segment` probe reserves the segment's duration exactly like an uploaded source (and is charged at `/complete` the same way); its `quota_exceeded` also sets the live session to `error` |
| POST | `/jobs/{id}/status` | `{ status, progress?, message? (≤ 500) }` | `200 Job`; re-posting the running status the job already has is an idempotent progress update (`200`); an invalid transition → `409` |
| POST | `/jobs/{id}/events` | `{ level: "info"\|"warning"\|"error", message (1–1000 characters), data? }` | `201`; a longer message → `400 validation_failed` |
| POST | `/jobs/{id}/upload-urls` | `{ files: [ { kind: "clip_render"\|"thumbnail"\|"analysis"\|"preview", fileName, contentType, sizeBytes? } ] }` | `200 { files: [ { fileName, key, url, method, headers } ] }` |
| POST | `/jobs/{id}/analysis` | `{ transcriptKey?, signalsKey?, summary: { signals: {...}, webcamRegion?, language?, processedSeconds, candidatesFound?, clipsRendered?, liveContext? } }` | `200` — `liveContext` (live_segment) is stored on the segment and becomes the `context` of the next segment's payload (also read from the `/complete` summary) |
| POST | `/jobs/{id}/candidates` | `{ candidates: CandidateInput[] }` | `200 { ids: [...] }` (replaces previous candidates of this job; order preserved). On a retried attempt (`attempt > 1`) it also removes the clips a previous attempt auto-rendered for this job — rows, renders and storage objects — so the new attempt's `/clips` never duplicates them (clips the user re-rendered through a render job are kept) |
| POST | `/jobs/{id}/clips` | `{ clips: [ { candidateId, title, aspectRatio, startSeconds, endSeconds, subtitles, framing, render: { storageKey, width, height, durationSeconds, sizeBytes, hasSubtitles }, thumbnailKey } ] }` | `200 { clips: [ { candidateId, clipId, renderId } ] }` |
| POST | `/jobs/{id}/render-result` | `{ renderId, status: "ready"\|"failed", storageKey?, width?, height?, durationSeconds?, sizeBytes?, hasSubtitles?, thumbnailKey?, error? }` | `200` |
| POST | `/jobs/{id}/complete` | `{ processedSeconds, summary? }` — `summary` is the `/analysis` summary object (`analyze_source`); `render_clip` sends none (its result goes through `/render-result`) | `200 Job` |
| POST | `/jobs/{id}/fail` | `{ code, message, retryable: bool, processedSeconds? }` | `200 Job` (re-queued when retryable and attempts remain; a terminal failure charges `processedSeconds`, capped by the reservation) |

Worker identity: besides the API key, every `/jobs/{id}/*` call must carry
`X-Worker-Id: {workerId}` (the id used in `/workers/{workerId}/claim`); `/heartbeat`,
`/probe`, `/status`, `/render-result`, `/complete` and `/fail` also accept it as a `workerId`
body field. A missing id is `400 validation_failed`. A job leased to another worker, not
leased at all (requeued by the reaper and not yet re-claimed) or not running answers `409` on
every one of these endpoints, so a stale worker whose lease expired and was re-claimed
elsewhere cannot report, fail or finish the new run. The claim performs
`queued → preparing_media` (bumping `attempt`, resetting `progress`), so the worker's first
transition is already done; posting the current running status again (e.g. `preparing_media`
right after the claim) is accepted as an idempotent progress update, and progress/`message`
updates within a status normally go through `/heartbeat` (which stores them and extends the
lease). `/status` is for the running-state transitions (`analyzing`, `detecting_moments`,
`generating_clips`, `finalizing`) and for `cancelled`; `completed` and `failed` go through
`/complete` and `/fail`, which carry `processedSeconds`, `code` and `retryable`. The API also
accepts them on `/status`, without those fields: `completed` there charges the reserved
seconds like `/complete`, and `failed` is a terminal failure with `errorCode: "failed"` —
never re-queued and nothing charged.

Re-sending after a lost answer (read timeout, dropped connection, 5xx): `/heartbeat`,
`/probe`, `/upload-urls`, `/candidates` (replaces the job's candidates), `/analysis` and
`/render-result` can be re-sent safely; re-sending the current running status is a progress
update; a re-sent `/status cancelled`, `/complete` or `/fail` whose first request was applied
answers `409` (the job is no longer running). `/claim`, `/clips` and `/events` create rows on
every call and are not idempotent: the worker never re-sends them once the request may have
reached the API (`docs/PROCESSING_PIPELINE.md`, "Retries of internal calls").

`JobPayload` (analyze_source):

```json
{
  "jobId": "…", "kind": "analyze_source", "attempt": 1, "userId": "…", "projectId": "…",
  "leaseSeconds": 180,
  "source": { "assetId": "…", "downloadUrl": "https://…signed…", "fileName": "live.mp4",
              "contentType": "video/mp4", "sizeBytes": 1234, "durationSeconds": null },
  "settings": { "minClipSeconds": 15, "maxClipSeconds": 60, "targetClipSeconds": 30,
                "sensitivity": 50, "maxCandidates": 15, "autoRenderTop": 15,
                "aspectRatio": "9:16", "subtitles": { "enabled": true, "style": "bold", "position": "bottom" },
                "language": "auto", "framing": { "mode": "auto", "focusX": 0.5, "focusY": 0.5, "webcamRegion": null }, "title": null,
                "focusCategories": ["funny"], "customKeywords": ["clutch"], "instructions": "quero os momentos engraçados" },
  "limits": { "maxRenderHeight": 1080, "maxSourceDurationSeconds": 21600,
              "remainingProcessingSeconds": 3600, "transcriptionModel": "base",
              "allowWebcamLayout": true }
}
```

`JobPayload` (render_clip):

```json
{
  "jobId": "…", "kind": "render_clip", "attempt": 1, "userId": "…", "projectId": "…",
  "leaseSeconds": 180,
  "source": { "assetId": "…", "downloadUrl": "…", "fileName": "…", "contentType": "…", "sizeBytes": 1, "durationSeconds": 3600.0 },
  "clip": { "clipId": "…", "renderId": "…", "version": 2, "title": "…",
            "startSeconds": 120.5, "endSeconds": 150.0, "aspectRatio": "9:16",
            "subtitles": { "enabled": true, "style": "bold", "position": "bottom" },
            "framing": { "mode": "auto", "focusX": 0.5, "focusY": 0.5, "webcamRegion": null } },
  "analysis": { "transcriptUrl": "…signed…|null", "webcamRegion": { "x": 0.75, "y": 0.70, "w": 0.25, "h": 0.30, "confidence": 0.9 } | null },
  "limits": { "maxRenderHeight": 1080, "allowWebcamLayout": true }
}
```

`JobPayload` (live_segment) — the `analyze_source` fields (`source` is the segment file, `settings`
the session's settings, `limits` as above) plus:

```json
{
  "live": { "sessionId": "…", "segmentId": "…", "sequence": 3, "startOffsetSeconds": 180.0,
            "durationSeconds": 60.0, "final": false, "segmentSeconds": 60 },
  "source": { "assetId": "…", "downloadUrl": "…", "fileName": "seg_00003.mkv",
              "contentType": "video/x-matroska", "sizeBytes": 123, "durationSeconds": 60.0 },
  "previous": { "segmentId": "…", "sequence": 2, "downloadUrl": "…", "startOffsetSeconds": 120.0,
                "durationSeconds": 60.0, "fileName": "seg_00002.mkv", "contentType": "video/x-matroska", "sizeBytes": 120 },
  "context": { "compositeSample": [1.2, 0.8], "language": "pt", "sequence": 2, "committedUntil": 172.0 }
}
```

`previous` is the segment `sequence − 1` with a signed download URL, `null` for sequence 0, when
that segment does not exist or when it failed. `context` is the `liveContext` the closest earlier
segment reported (normally `sequence − 1`; after a failed segment the one before it, so the score
sample and the language are not lost — the worker only trusts `committedUntil` when
`context.sequence` is `sequence − 1`), `null` before any. `source.durationSeconds` is the probed
duration once known, else the companion's.

`JobPayload` (render_clip of a live project): as above but with `segments` instead of `source`:
`segments: [ { downloadUrl, startOffsetSeconds, durationSeconds, fileName, contentType, sizeBytes } ]`
— every stored segment of the session overlapping `[clip.startSeconds, clip.endSeconds)` (session
times), in sequence order; `analysis.transcriptUrl`/`webcamRegion` come from the `live_segment` job
that detected the clip's moment (else the analysed segment holding the clip's middle). No usable
segment → the claim fails the job (`invalid_media`).

`settings` is the project's stored `AnalysisSettings`, capped by the plan when the job is claimed:
`autoRenderTop` is always a number, `focusCategories`/`customKeywords` are normalised lists (possibly
empty) and `instructions` is a string or `null`. `limits.transcriptionModel` is the plan's Whisper model
(free `base`, creator and pro `small`); the worker's `WHISPER_MODEL` is the largest it will load.

`CandidateInput`: `{ rank, startSeconds, endSeconds, score (0–100 int), category,
reasoning, transcript, signals }` where `signals` is a free-form object documented in
`docs/PROCESSING_PIPELINE.md`.
