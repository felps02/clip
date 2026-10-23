# Infrastructure

## Local (Docker)

```bash
cp .env.example .env            # edit the change-me secrets
docker compose -f infra/docker-compose.yml up --build
# optional S3 provider for tests:  docker compose -f infra/docker-compose.yml --profile s3 up
```

Services: `postgres` (5432), `api` (5080 → container 8080), `worker` (8008), `web` (3000),
`minio` (9000/9001, profile `s3`). Storage defaults to the Local provider on the `storage`
volume; the worker and the browser transfer files through the API's signed URLs.

## Production checklist (phase 6)

- Put `web` and `api` behind one HTTPS reverse proxy (Caddy/Nginx/Traefik). Serve the API on
  `api.<domain>` and the app on `app.<domain>` (same site → cookies work), or proxy
  `/api/*` to the API on the app domain. Set `Auth__CookieSecure=true`,
  `Frontend__BaseUrl`, `Api__PublicBaseUrl`, `Cors__AllowedOrigins__0`.
- With the API and the app on different hosts set `Auth__CookieDomain=<domain>` (e.g.
  `example.com` for `api.example.com` + `app.example.com`): the auth cookies then carry that
  domain and the web's server-side route guard sees `cf_access`. Leave it empty when `/api/*` is
  proxied on the app host; otherwise build the web with `NEXT_PUBLIC_COOKIE_GUARD=false`
  (`docs/SETUP_PROVIDERS.md`, "Auth cookies across subdomains").
- Use an S3-compatible bucket (`Storage__Provider=S3`) with a public endpoint reachable by
  browsers and workers (presigned URLs embed the host). Lifecycle rules: expire
  `users/*/jobs/*/analysis` after 90 days; the API's retention service deletes the rest.
- Managed PostgreSQL with daily backups; run `dotnet ef database update` in the deploy
  pipeline instead of `Database__MigrateOnStartup`.
- Workers scale horizontally (`WORKER_ID` unique per instance). CPU-only instances with
  4–8 vCPU handle ~2–3× realtime analysis with the `base` Whisper model; GPU instances
  (`WHISPER_DEVICE=cuda`) are much faster for `small`/`medium`.
- Secrets from the platform's secret store (never in images). Rotate `Internal__ApiKey`,
  `Auth__JwtSigningKey` and `Storage__SigningKey` independently.
- Observability: ship container logs (JSON) to the log platform; `/health` on API and
  worker for liveness; job events table for per-job audit.
- Cost controls live in plan limits (`Plans` configuration) and `MAX_PARALLEL_JOBS` per
  worker; raise `Jobs__MaxAttempts` carefully (each retry re-processes the file).
