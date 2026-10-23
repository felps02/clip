# ClipForge backend (.NET 10)

ASP.NET Core 10 Minimal API that owns the database, authentication, quotas, the job queue, storage,
e-mail, billing and the internal API used by the processing worker. Contract: `docs/API.md`,
`docs/PROCESSING_PIPELINE.md`, `docs/DATA_MODEL.md`.

```
backend/
  ClipForge.sln
  Directory.Build.props / Directory.Packages.props   # net10.0, nullable, central package versions
  .config/dotnet-tools.json                           # dotnet-ef (local tool)
  src/ClipForge.Domain          entities, enums, value objects, AppException, JobStateMachine, quota rules
  src/ClipForge.Infrastructure  AppDbContext + migrations, IObjectStorage (Local/S3), signed URLs, e-mail, payments
  src/ClipForge.Api             Program.cs, Features/<Feature>/{*Endpoints,*Service,Dtos,Validators}.cs, hosted services
  tests/ClipForge.Api.Tests     xUnit: unit tests + integration tests (WebApplicationFactory + PostgreSQL + Respawn)
```

## Prerequisites

- .NET SDK 10.0.x
- PostgreSQL 18 reachable at `localhost:5432` with user `clipforge`/`clipforge` and the databases
  `clipforge` (development) and `clipforge_test` (integration tests). On Windows without Docker,
  `scripts/dev-setup.ps1` + `scripts/dev-db.ps1 start` provide a portable server; with Docker,
  `docker compose -f infra/docker-compose.yml up postgres`.

## Run

```powershell
cd backend
dotnet restore
dotnet run --project src/ClipForge.Api        # http://localhost:5080
```

- `ASPNETCORE_ENVIRONMENT=Development` (default from `launchSettings.json`) loads
  `appsettings.Development.json`: local connection string, `Database:MigrateOnStartup=true` (migrations are
  applied on start), dev-only signing keys, `Internal:ApiKey=change-me-internal-key`, Local storage under
  `%LOCALAPPDATA%\ClipForge\storage`, e-mails to the log, payments disabled.
- Any setting can be overridden with environment variables (`Section__Key`) or `.env`
  (`scripts/dev-api.ps1` loads it). See `docs/SETUP_PROVIDERS.md` for SMTP, S3/MinIO and Stripe.
- Endpoints: `GET /health`, OpenAPI `GET /openapi/v1.json`, Scalar UI `GET /scalar`.

Quick check:

```bash
curl -s http://localhost:5080/health
curl -s -c jar -b jar -X POST http://localhost:5080/api/auth/register -H "Content-Type: application/json" \
  -d '{"email":"me@example.com","password":"Password123","displayName":"Me","acceptedTerms":true}'
curl -s -b jar http://localhost:5080/api/auth/me
curl -s http://localhost:5080/api/plans
```

Worker calls use the headers `X-Internal-Api-Key: <Internal:ApiKey>` and `X-Worker-Id: <id>` (the id used to
claim; `/heartbeat`, `/probe`, `/status`, `/render-result`, `/complete` and `/fail` also accept it as the `workerId`
body field). Every `/internal/jobs/*` call requires it (`400` without it) and is refused with `409` when the job is
leased to another worker or is not running.

## Build and test

```bash
dotnet build backend/ClipForge.sln
dotnet test backend/ClipForge.sln
```

Integration tests boot the API in-process (`WebApplicationFactory`, environment `Testing`) against
`ConnectionStrings:Test` (default `Host=localhost;Port=5432;Database=clipforge_test;Username=clipforge;Password=clipforge`,
override with the `ConnectionStrings__Test` environment variable). Migrations are applied once per run; the
data is reset between tests with Respawn (plans are kept) through `TruncateCascadePostgresAdapter`, a single
`TRUNCATE … CASCADE` that needs no superuser despite the `clips` ↔ `clip_renders` foreign-key cycle. Storage
uses a temporary folder, e-mails are captured by `LogEmailSender` (notification e-mails go through the
background `EmailOutbox`; tests wait for it to drain before asserting), payments use a fake provider for
webhook tests, and the background services run with long intervals (their logic is invoked directly by the
tests).

## Migrations

```bash
cd backend
dotnet tool restore
dotnet ef migrations add <Name> --project src/ClipForge.Infrastructure --startup-project src/ClipForge.Api
dotnet ef database update      --project src/ClipForge.Infrastructure --startup-project src/ClipForge.Api
```

Each migration is summarised in `docs/migrations/CHANGELOG.md`.

## Notes

- Errors are RFC 9457 `application/problem+json` with a machine-readable `code` (`validation_failed`,
  `unauthorized`, `not_found`, `conflict`, `quota_exceeded`, `plan_limit`, `invalid_media`,
  `source_not_supported`, `payments_not_configured`, `rate_limited`, `job_not_cancellable`, …).
- Rate limiting: credential endpoints (`register`, `login`, `forgot-password`, `reset-password`,
  `verify-email`, `resend-verification`, `change-password`, `DELETE /api/account`) 10 req/min per IP;
  `POST /api/auth/refresh` its own 60 req/min per IP (`/me` and `/logout` are not limited); upload endpoints
  30 req/min per user; the signed `PUT /api/storage/upload` 600 req/min per IP (`RateLimiting` section). Behind
  a reverse proxy configure the `ReverseProxy` section so the client IP comes from `X-Forwarded-For` of trusted
  proxies (`docs/SETUP_PROVIDERS.md`).
- Auth cookies: `Auth:CookieDomain` (optional) gives `cf_access`/`cf_refresh` a shared `Domain` when the web
  runs on a sibling subdomain. Refresh tokens rotate atomically (one token, one rotation); a reused token
  revokes all sessions of the user.
- Notifications: in-app always; e-mail copies follow `notification_preferences` and are delivered by the
  `EmailDispatcher` background service after the request committed (`NotificationService.FlushAsync`).
- Quota checks (active projects, concurrent jobs, renders, storage) run under a per-user advisory lock together
  with the insert they guard (`QuotaService.WithUserLockAsync`), so concurrent requests cannot exceed the caps.
- The API never runs ffmpeg/ffprobe: uploads are validated by size and magic bytes, duration and quota
  limits are enforced when the worker reports `/internal/jobs/{id}/probe`.
- Background services: `PlanSeeder` (upserts the `Plans` section on startup), `JobReaper` (expired leases
  → queued/failed), `RetentionCleanup` (expired pending uploads, a second deletion of uploads deleted while
  their signed URL was valid, purge of soft-deleted projects/clips after the plan retention window),
  `AccountPurge` (accounts with a deletion request), `EmailDispatcher` (notification e-mails).
