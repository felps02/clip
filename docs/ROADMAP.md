# Roadmap and phase status

Legend: ✅ done & verified · 🔧 in progress · ⏳ planned · ⚠️ needs external credentials

## Phase 1 — Foundation
- 🔧 Repository structure, conventions (`CLAUDE.md`), docs, `.env.example`
- 🔧 Backend solution (.NET 10), EF Core + PostgreSQL, migrations
- 🔧 Authentication (register/login/logout/refresh/reset/verify), profile, account deletion
- 🔧 Frontend app shell, design system, auth pages
- 🔧 Processing service skeleton (FastAPI + worker loop + FFmpeg wrappers)
- 🔧 Dev scripts (Windows without Docker), docker-compose, CI

## Phase 2 — Functional MVP
- 🔧 Upload with progress, content validation, temp expiry
- 🔧 Project creation (idempotent), job queue, claim/lease/heartbeat/reaper, cancel
- 🔧 Pipeline: probe → audio features → transcription → visual → fusion → candidates → render → thumbnails
- 🔧 Dashboard, projects, project detail with progress + candidates, library, player, download
- 🔧 Quotas (minutes, file size, duration, concurrency) and usage records

## Phase 3 — Smart editing
- 🔧 Synchronized subtitles (ASS), styles, title, 9:16 / 16:9 / 1:1, framing, webcam split
- 🔧 Clip editor (trim, title, subtitles, format) + re-render versions

## Phase 4 — Monetisation
- 🔧 Plans as configuration, subscriptions, usage, payment events
- ⚠️ Stripe Checkout + webhooks (code present; needs test keys to run end-to-end)
- ⏳ Mercado Pago provider

## Phase 5 — Real-time detection
- ⏳ OBS companion (obs-websocket), live sessions, segment upload, incremental detection
- ⏳ Lives page with real connection states, notifications

## Phase 6 — Production
- ⏳ Observability (OpenTelemetry), rate limiting review, security review
- ⏳ Deployment configuration (Docker images, reverse proxy, HTTPS), cost controls
- ⏳ Operational docs, backups, retention jobs

## External dependencies pending
- Payment provider credentials (Stripe test mode and/or Mercado Pago)
- SMTP credentials for real e-mail delivery (dev uses the log sender)
- S3 bucket or MinIO for object storage in production (dev uses local disk)
- Optional: YouTube Data API key / Twitch client for link metadata
- Optional: Anthropic API key for the LLM re-ranking stage
