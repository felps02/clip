# ClipForge (codename)

SaaS that turns recorded streams and long videos into short, auto-edited clips for social
media. Upload a recording, the pipeline finds the most interesting moments (audio energy,
speech and keywords, visual activity), scores them, renders clips (9:16, 16:9, 1:1) with
synchronized subtitles, and lets you review, trim, re-render and download them.

> Status: see `docs/ROADMAP.md` for what is done and verified, what is in progress and what
> still depends on external credentials.

## Stack

- **Frontend:** Next.js 16, React 19, TypeScript, Tailwind CSS v4, TanStack Query.
- **Backend:** .NET 10 / ASP.NET Core Minimal APIs, EF Core 10, PostgreSQL 18, ASP.NET Identity + JWT cookies.
- **Processing:** Python 3.14, FastAPI, FFmpeg 9, faster-whisper (CTranslate2), OpenCV, NumPy/SciPy.
- **Live capture:** OBS companion (`companion/`, obs-websocket v5) that uploads the recording in segments.
- **Infra:** PostgreSQL, S3-compatible object storage (MinIO in Docker) or local disk, Docker Compose, GitHub Actions.

## Quick start (Windows, no Docker)

```powershell
# 1. one-time: downloads FFmpeg + portable PostgreSQL into %LOCALAPPDATA%\ClipForge\tools,
#    creates the databases, the Python venv, installs packages
.\scripts\dev-setup.ps1
# 2. start the database
.\scripts\dev-db.ps1 start
# 3. in three terminals (development mode, hot reload)
.\scripts\dev-api.ps1      # http://localhost:5080  (OpenAPI UI at /scalar)
.\scripts\dev-worker.ps1   # processing service + worker, http://localhost:8008/health
.\scripts\dev-web.ps1      # http://localhost:3000
```

Copy `.env.example` to `.env` and adjust secrets. Defaults work for local development.

To run a production-like copy of the whole stack in the background (independent of the source
tree, so you can keep editing): `.\scripts\stack.ps1 package`, then `.\scripts\stack.ps1 start`
(`status`, `stop`). Logs go to `%LOCALAPPDATA%\ClipForge\logs`.

## Quick start (Docker)

```bash
cp .env.example .env
docker compose -f infra/docker-compose.yml up --build
```

## Tests

```bash
dotnet test backend/ClipForge.sln                                  # unit + integration (real PostgreSQL)
cd processing && .venv/Scripts/python -m pytest -m "not integration"
cd frontend && pnpm lint && pnpm typecheck && pnpm test && pnpm build
cd companion && .venv/Scripts/python -m pytest -m "not integration"
```

End-to-end against a running stack (nothing mocked):

```bash
# test video with real pt-BR speech (Windows TTS voice) and loud moments
processing/.venv/Scripts/python scripts/e2e/make_fixture.py --out "%LOCALAPPDATA%/ClipForge/fixtures/e2e-fixture-ptbr.mp4"
# API flow: register → upload → analyse → clips → edit + re-render → 16:9 clip → cancel → isolation
processing/.venv/Scripts/python scripts/e2e/smoke.py --fixture "%LOCALAPPDATA%/ClipForge/fixtures/e2e-fixture-ptbr.mp4"
# browser flow with Playwright on the installed Edge (screenshots in e2e/screenshots)
cd e2e && pnpm install && pnpm test
```

## Documentation

- `docs/ARCHITECTURE.md` — components, flows, decisions (ADRs), security model
- `docs/DATA_MODEL.md` — entities, relations, indexes, deletion strategy
- `docs/API.md` — REST contract (public + internal)
- `docs/PROCESSING_PIPELINE.md` — job state machine, worker protocol, moment detection, rendering
- `docs/PLANS_AND_BILLING.md` — plans, quotas, subscriptions, payment webhooks
- `docs/LIVE_CAPTURE.md` — real-time capture with the OBS companion
- `docs/SETUP_PROVIDERS.md` — configuring e-mail, S3, Stripe, YouTube/Twitch
- `docs/OPERATIONS.md` — deploys, monitoring, backups, runbooks, LGPD
- `docs/ROADMAP.md` — phase status and what is pending
