# ClipForge — conventions for working in this repository

ClipForge is the internal codename of a SaaS that turns recorded streams into short,
auto-edited clips. The public brand name is NOT decided: never hard-code a brand name in UI
copy or e-mails — read it from `frontend/src/config/brand.ts` (web) or the `Branding`
options section (API).

## Layout

| Path          | What lives there                                                       |
|---------------|------------------------------------------------------------------------|
| `frontend/`   | Next.js 16 (App Router) + TypeScript + Tailwind v4 + TanStack Query     |
| `backend/`    | .NET 10 solution: `ClipForge.Domain`, `ClipForge.Infrastructure`, `ClipForge.Api`, tests |
| `processing/` | Python 3.14 FastAPI service + job worker (FFmpeg, faster-whisper, OpenCV) |
| `infra/`      | docker-compose, Dockerfiles, deployment notes                           |
| `scripts/`    | PowerShell dev scripts (Windows without Docker) and helpers             |
| `docs/`       | Architecture, data model, API contract, pipeline protocol, ADRs         |

Read `docs/ARCHITECTURE.md` first. `docs/API.md` and `docs/PROCESSING_PIPELINE.md` are the
contracts between the three codebases: change the doc in the same change as the code.

## Ground rules

- **No fake results.** Candidates, scores, transcripts and clips must come from real
  processing. Never seed demo clips, never stub the pipeline with fixed output. If a signal is
  unavailable (e.g. transcription model missing) record `available: false` in the metadata and
  degrade honestly.
- **Relevance score is an internal estimate (0–100)**, never presented as a probability of
  going viral.
- **Every query is scoped by the authenticated user** (`UserId` from claims). Ownership is
  checked in the API, never trusted from the client. Tests must cover cross-user isolation.
- **Never shell out with user input.** FFmpeg/ffprobe are invoked with argument lists; file
  paths are generated server-side (GUIDs); user text goes through ASS subtitle files, not
  `drawtext` expressions.
- **Quotas are enforced server-side** (plan limits, file size, duration, concurrency, renders).
- **Secrets only via environment / user-secrets.** `.env.example` documents every variable.
  Never commit `.env`, keys or tokens.
- **Idempotency:** project creation honours `Idempotency-Key`; job claim uses
  `FOR UPDATE SKIP LOCKED`; payment webhooks are deduplicated by provider event id.
- Interface language of the product is **pt-BR**; code, identifiers, commits and docs are in
  English. UI copy lives in the components (no i18n framework in MVP).

## Backend (.NET 10)

- Minimal APIs grouped per feature in `src/ClipForge.Api/Features/<Feature>/`
  (`*Endpoints.cs`, `*Service.cs`, `Dtos.cs`, `Validators.cs`).
- Domain entities in `ClipForge.Domain` (no EF attributes; configuration via
  `IEntityTypeConfiguration` in Infrastructure). Enums stored as strings.
- EF Core + Npgsql. Migrations live in `src/ClipForge.Infrastructure/Migrations`; create with
  `dotnet ef migrations add <Name> --project src/ClipForge.Infrastructure --startup-project src/ClipForge.Api`.
- Errors: throw `AppException(code, status, message)` → ProblemDetails via the global handler.
  Validation with FluentValidation through an endpoint filter.
- Logging: Serilog, structured, no secrets or tokens in logs.
- Tests: xUnit + Shouldly + NSubstitute. Integration tests run against a real PostgreSQL
  database (`clipforge_test`) through `WebApplicationFactory`; DB reset with Respawn.
- Build/test: `dotnet build backend/ClipForge.sln` · `dotnet test backend/ClipForge.sln`.

## Processing (Python 3.14)

- Virtual env in `processing/.venv`. Install: `python -m pip install -r requirements.txt`.
- Entry point: `uvicorn app.main:app --port 8008` (serves `/health` and runs the worker loop
  when `WORKER_ENABLED=true`).
- All FFmpeg calls go through `app/media/ffmpeg.py` (`run_ffmpeg(args: list[str])`).
- Type hints everywhere, `ruff` clean, `pytest` for tests (`pytest -m "not integration"` for
  the fast suite; integration tests need the API running).

## Frontend (Next.js 16)

- `pnpm` is the package manager. `pnpm dev` / `pnpm build` / `pnpm lint` / `pnpm test`.
- API access only through `src/lib/api/` (typed client + TanStack Query hooks). Cookies carry
  auth (`credentials: "include"`); never store tokens in `localStorage`.
- Design tokens in `src/app/globals.css` (`@theme`). Dark theme is the default. Reusable
  primitives in `src/components/ui/`.
- Progress of jobs is polled every 2 s with `refetchInterval` while a job is active.

## Local development (Windows, no Docker)

`scripts/dev-setup.ps1` downloads FFmpeg and a portable PostgreSQL into
`%LOCALAPPDATA%\ClipForge\tools`, initialises the database cluster and creates the
`clipforge` / `clipforge_test` databases. Then `scripts/dev-db.ps1 start`,
`scripts/dev-api.ps1`, `scripts/dev-worker.ps1`, `scripts/dev-web.ps1`.
With Docker: `docker compose -f infra/docker-compose.yml up`.

Ports: web 3000 · API 5080 · processing 8008 · PostgreSQL 5432 · MinIO 9000/9001.
