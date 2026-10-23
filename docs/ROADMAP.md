# Roadmap and phase status

Legend: ✅ done and verified against the real stack · 🔧 in progress · ⏳ planned ·
⚠️ code present, needs external credentials to run end to end

Verification means: unit + integration tests per codebase **and** the end-to-end suites against a
running stack (API, worker, web, PostgreSQL, local LLM): `scripts/e2e/smoke.py` (API flow),
`scripts/e2e/live_smoke.py` (live capture with the real companion and a recording growing in real
time) and the Playwright suite in `e2e/` (installed Edge, including over a LAN address, which is
not a secure browser context). Status as of 2026-10-23.

## Phase 1 — Foundation
- ✅ Repository structure, conventions (`CLAUDE.md`), architecture/API/data model docs, `.env.example`
- ✅ Backend solution (.NET 10), EF Core + PostgreSQL 18, versioned migrations (`docs/migrations/CHANGELOG.md`)
- ✅ Authentication: register, login, logout, rotating refresh tokens, password reset, e-mail verification,
  lockout, account deletion with purge; cross-user isolation covered by tests
- ✅ Frontend app shell, design system (dark), auth pages, legal pages with placeholders
- ✅ Processing service (FastAPI + worker loop + FFmpeg wrappers)
- ✅ Windows dev scripts without Docker (`scripts/dev-*.ps1`, `scripts/stack.ps1` with demo/e2e
  profiles and LAN mode), docker-compose, GitHub Actions CI definition (not run on GitHub yet)

## Phase 2 — Functional MVP
- ✅ Upload with progress, magic-byte validation, size/duration limits, temp expiry
- ✅ Idempotent project creation, PostgreSQL job queue (SKIP LOCKED, leases, heartbeats, reaper,
  retries), cancel
- ✅ Pipeline: probe → audio features → transcription (faster-whisper, language detected once over
  the whole recording) → visual features → fusion → candidates → render → thumbnails
- ✅ Dashboard, projects, project page that leads with the finished clips ("Clipes prontos",
  "Baixar todos (.zip)"), library, player, downloads
- ✅ Quotas (minutes, file size, duration, concurrency, renders, storage), usage records,
  consumption estimate before upload

## Phase 3 — Smart editing
- ✅ Synchronized subtitles (ASS) with safe-area margins for 9:16, styles, title, 9:16 / 16:9 / 1:1,
  framing with webcam split, clip editor (trim, title, subtitles, format) and re-render versions
- ✅ Relevance score 0–100 (internal estimate) with pt-BR reasoning; focus categories and custom
  keywords that change the analysis; every detected moment rendered by default
- ✅ The product's own AI (local llama.cpp model, no paid API): assistant chat that turns requests
  ("quero clipes mais engraçados") into analysis settings, re-ranking and clip titles; Gemini free
  tier optional

## Phase 4 — Monetisation
- ✅ Plans as configuration (free/creator/pro), subscriptions, usage, payment events, webhook
  idempotency and ordering
- ⚠️ Stripe Checkout, Billing Portal and webhooks (implemented and tested with a fake provider;
  needs Stripe test keys to run end to end)
- ⏳ Mercado Pago provider

## Phase 5 — Real-time detection
- ✅ OBS companion (`companion/`): obs-websocket v5, slices the growing recording without
  re-encoding, persistent upload queue, reconnects, heartbeats — verified with a fake
  obs-websocket server and in file mode against the real stack (not yet against a real OBS)
- ✅ Live sessions, device tokens, segment upload, incremental `live_segment` analysis with the
  commit rule (no duplicate or lost moment at segment boundaries), live clip re-render
- ✅ Lives page with real states, segments timeline, highlights and notifications

## Phase 6 — Production
- 🔧 Security reviews done per area; readiness endpoint; structured logs; operations guide
  (`docs/OPERATIONS.md`)
- ⏳ OpenTelemetry export, Docker images built and tested on Linux, reverse proxy + HTTPS deploy
- ⏳ Installer for the companion (today it runs from this repository with Python)

## External dependencies pending
- Payment provider credentials (Stripe test mode and/or Mercado Pago)
- SMTP credentials for real e-mail delivery (dev uses the log sender)
- S3-compatible bucket for production storage (dev uses local disk)
- A real OBS Studio session to validate the companion's OBS mode
- Optional: YouTube Data API key / Twitch client for link metadata; Gemini API key (free tier)
- Disk space on the dev machine: C: is nearly full (Visual Studio download cache ~7 GB)
