# Setting up external providers

Everything is configured through ASP.NET configuration: `appsettings.json` defaults, overridden by
`appsettings.{Environment}.json`, environment variables (double underscore = section, e.g.
`Storage__S3__Bucket`) and `.env` (loaded by `scripts/load-env.ps1`). `.env.example` lists every variable.
Secrets never go into `appsettings*.json` that is committed; use environment variables or
`dotnet user-secrets` (the API project has `UserSecretsId=clipforge-api`).

Without any provider configured the API runs fully for development: files on local disk, e-mails in the
log, billing endpoints answering `409 payments_not_configured`.

## Required secrets (every environment)

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Default` | PostgreSQL connection string (`Host=…;Port=5432;Database=clipforge;Username=…;Password=…`) |
| `Auth__JwtSigningKey` | HMAC key for access tokens, at least 32 random characters (`openssl rand -base64 48`) |
| `Storage__SigningKey` | HMAC key for signed download/upload URLs in Local mode, at least 32 characters |
| `Internal__ApiKey` | Shared secret between the API and the processing worker (header `X-Internal-Api-Key`) |

`appsettings.Development.json` ships dev-only values for the three keys so `dotnet run` works out of the
box; production must override all of them.

## E-mail

| Variable | Values |
|---|---|
| `Email__Provider` | `Log` (default: messages are written to the log, nothing is sent) or `Smtp` |
| `Email__From`, `Email__FromName` | Sender address / display name |
| `Email__Smtp__Host`, `Email__Smtp__Port` | SMTP server (587 for STARTTLS, 465 for implicit TLS with `UseStartTls=false`) |
| `Email__Smtp__Username`, `Email__Smtp__Password` | Credentials (leave empty for an unauthenticated relay) |
| `Email__Smtp__UseStartTls` | `true` (STARTTLS) or `false` (auto-detect / implicit TLS) |

Delivery uses MailKit. E-mails sent: e-mail verification (`/verify-email?userId=&token=`) and password reset
(`/reset-password?email=&token=`), sent while the request runs; and the notification e-mails the user
opted into in Configurações → Notificações (analysis completed, analysis/render failed, 80 %/100 % of the
processing minutes, billing — docs/API.md → Notifications), which go through an in-memory background queue
(`EmailOutbox`, 1000 messages): a slow or unreachable SMTP server is logged (`Notification e-mail … could not
be sent`) and never fails the job report or webhook that produced the e-mail; messages still queued when the
API stops are lost (the in-app notification always exists). All links point to `Frontend__BaseUrl`. Set
`Auth__RequireConfirmedEmail=true` only after SMTP works, otherwise nobody can log in.

Example (Mailtrap / any SMTP sandbox):

```
Email__Provider=Smtp
Email__From=no-reply@yourdomain.com
Email__Smtp__Host=sandbox.smtp.mailtrap.io
Email__Smtp__Port=587
Email__Smtp__Username=xxxx
Email__Smtp__Password=yyyy
Email__Smtp__UseStartTls=true
```

## Auth cookies across subdomains

The API sets two HttpOnly cookies, `cf_access` (path `/`) and `cf_refresh` (path `/api/auth`). By default
they are host-only: they belong to the API host. When the web app runs on a sibling subdomain
(`app.example.com` next to `api.example.com`), its server-side route guard must see `cf_access`, so give the
cookies a shared domain:

```
Auth__CookieDomain=example.com     # empty (default) = host-only cookies of the API host
Auth__CookieSecure=true            # HTTPS in production
```

The value must be the API host or a parent of it (browsers reject other domains) and should cover the web
host. Both cookies are then written and cleared with that `Domain`; host-only copies issued before the setting
was enabled are expired in the same responses. Not needed when the web proxies `/api/*` on its own host;
without it (and without such a proxy) build the web with `NEXT_PUBLIC_COOKIE_GUARD=false` so only its
client-side guard applies.

## Object storage

### Local (default)

```
Storage__Provider=Local
Storage__Local__RootPath=%LOCALAPPDATA%\ClipForge\storage   # %ENV% placeholders are expanded
Storage__SigningKey=<32+ random characters>
```

Files live under `RootPath/users/{userId}/…`. Browsers and workers transfer bytes through the API:
`PUT /api/storage/upload?key&exp&max&sig` and `GET /api/storage/download?key&exp&sig&name`
(HMAC-SHA256, see `docs/API.md`). `Api__PublicBaseUrl` is the host browsers reach, `Api__WorkerBaseUrl`
the host the worker reaches (e.g. `http://api:8080` inside Docker). Request bodies for the upload endpoint
are limited to the size declared when the upload was created, and the PUT only lands while its target still
accepts it (a pending upload, a running job): a completed or deleted upload cannot be replaced or re-created
with its still-valid URL.

### S3 / MinIO / Cloudflare R2

```
Storage__Provider=S3
Storage__S3__Bucket=clipforge
Storage__S3__ServiceUrl=http://localhost:9000     # empty for AWS (then Region is used)
Storage__S3__Region=us-east-1
Storage__S3__AccessKey=minioadmin
Storage__S3__SecretKey=minioadmin
Storage__S3__ForcePathStyle=true                  # required for MinIO
```

The API only needs `GetObject`, `PutObject`, `DeleteObject`, `ListBucket` and `HeadObject` on the bucket.
Presigned PUT/GET URLs are returned to browsers and workers, so the `ServiceUrl` must be reachable by both
(for MinIO in Docker use a hostname the browser resolves, or keep the Local provider which proxies through
the API). Create the bucket before starting (`mc mb local/clipforge`); the compose file has a `minio-init`
helper (`docker compose --profile s3 up`). The S3 provider is compiled and unit-level exercised but has no
integration test in this repository (no MinIO on the CI runner). Presigned PUT URLs cannot be revoked:
for `Media__UploadUrlMinutes` (120) a client could re-send a PUT after `/complete` or after deleting the
upload. A deleted asset's key is therefore deleted once more by the retention cleanup after its upload URL
expired; keep `Media__UploadUrlMinutes` as short as your users' uploads allow.

## Stripe (test mode)

1. Create one recurring monthly Product/Price per paid plan in the Stripe dashboard (test mode) and copy the
   price ids:
   ```
   Plans__creator__ProviderPriceId=price_123
   Plans__creator__PriceMonthlyCents=2990
   Plans__pro__ProviderPriceId=price_456
   Plans__pro__PriceMonthlyCents=7990
   ```
   Plans without a price id/price are listed as "sob consulta" and cannot be bought (`409 conflict`).
2. Configure the provider:
   ```
   Payments__Provider=Stripe
   Payments__Stripe__SecretKey=sk_test_…
   Payments__Stripe__WebhookSecret=whsec_…
   ```
3. Forward webhooks while developing:
   ```
   stripe listen --forward-to localhost:5080/api/webhooks/stripe
   ```
   Subscribe (in the dashboard, for production) to `checkout.session.completed`,
   `customer.subscription.created`, `customer.subscription.updated`, `customer.subscription.deleted`,
   `invoice.paid` and `invoice.payment_failed`.
4. Flow: `POST /api/billing/checkout { planCode }` → Checkout Session (mode `subscription`,
   `client_reference_id` = user id, metadata `userId`/`planCode`) → the user pays with a test card
   (`4242 4242 4242 4242`) → Stripe calls the webhook → the subscription row is updated and a
   `subscription_events` row is written. The success redirect never changes the plan; the web polls
   `GET /api/billing/subscription`. `POST /api/billing/portal` opens the Billing Portal (enable it in the
   dashboard first) and `POST /api/billing/cancel` sets `cancel_at_period_end`.

Webhook processing is idempotent: every event is stored in `payment_events` with a unique
`(provider, provider_event_id)`; duplicates of handled events answer `200` and are not applied again. A
processing failure is stored with `status=failed` and answered with `500` (problem `code: internal_error`) so
Stripe redelivers; the redelivery is reprocessed. A bad `Stripe-Signature` answers `400 invalid_signature`, a
missing `Payments__Stripe__WebhookSecret` `409 payments_not_configured`.

Plans are granted only for a confirmed payment: a checkout whose `payment_status` is `unpaid` (declined,
abandoned, boleto/PIX still clearing) grants nothing, and a subscription is bound to the user only while
`active`/`trialing` — the `incomplete` subscription Checkout creates before the first invoice is paid never
grants the plan (`docs/PLANS_AND_BILLING.md`).

## Settings assistant: local LLM (default) or Gemini free tier

The assistant chat on `/new` ("Assistente") turns requests such as "quero clipes mais engraçados, de uns
30 segundos, pro tiktok" into settings changes (ADR-010). It never needs a paid API.

| Variable | Default | Purpose |
|---|---|---|
| `Assistant__Provider` | `local` | `local` (llama.cpp), `gemini` (Google AI Studio key) or `none` (assistant off: status unavailable, chat `503`) |
| `Assistant__Local__BaseUrl` | `http://127.0.0.1:8090` | OpenAI-compatible `llama-server` (`http://llm:8090` inside Docker) |
| `Assistant__Local__Model` | empty | Label shown by `GET /api/assistant/status`; empty = the model file the server reports |
| `Assistant__Local__WarmupOnStartup` | `true` | One request with the system prompt at startup so the server caches it |
| `Assistant__Gemini__ApiKey` | empty | Secret: environment / user-secrets only |
| `Assistant__Gemini__Model` | `gemini-2.5-flash` | Verify the id in Google AI Studio (free tier: Flash models) |
| `Assistant__Gemini__ThinkingBudget` | `0` | `generationConfig.thinkingConfig.thinkingBudget` (0 = no thinking, fastest); empty = not sent |
| `Assistant__TimeoutSeconds` | `60` | Upper bound of one model call (then `503 assistant_unavailable`) |
| `Assistant__StatusCacheSeconds` | `10` | How long the provider probe of `/api/assistant/status` is reused |
| `RateLimiting__AssistantPerMinute` | `20` | Chat messages per user and minute |

### Local model (the product's own AI)

llama.cpp `llama-server` (CPU build) with Qwen3-1.7B Q4_K_M (Apache-2.0, ~1.1 GB, multilingual
including Portuguese). On Windows `scripts/dev-setup.ps1` downloads the server into
`%LOCALAPPDATA%\ClipForge\tools\llama` and the model into `%LOCALAPPDATA%\ClipForge\models\llm`
(skip with `-SkipLlm`); then:

```
.\scripts\dev-llm.ps1                      # http://127.0.0.1:8090/v1, 2 slots x 4096 tokens, all cores
.\scripts\dev-llm.ps1 -Parallel 1 -Threads 4
```

`scripts/stack.ps1` starts it too when the files exist. In Docker the `llm` service (official
`ghcr.io/ggml-org/llama.cpp:server` image) serves the model from the `models` volume, filled once by the
`llm-model` helper; the API reaches it at `http://llm:8090`.

The API sends `POST /v1/chat/completions` with `response_format: { type: "json_schema", json_schema:
{ name, strict: true, schema } }` (the server turns the schema into a grammar),
`chat_template_kwargs: { enable_thinking: false }`, `cache_prompt: true`, `temperature: 0.2`,
`max_tokens: 300`. Every parallel slot keeps its own prompt cache; the system prompt never changes, so
after the warm-up only the short per-request part is processed. Measured on a 4-core desktop CPU (Ryzen 3
3200G, other jobs running): about 20 s for a call with a cold cache, 5–9 s with the cache warm. Size
`-c` so that each slot has at least 4096 tokens (`ContextPerSlot`). The worker can use the same server
(`LLM_PROVIDER=local`, `LLM_BASE_URL`) for its optional re-ranking and clip titles.

### Gemini (optional, free tier)

1. Create a key in Google AI Studio (free tier) and set `Assistant__Provider=gemini`,
   `Assistant__Gemini__ApiKey=…` (and `GEMINI_API_KEY` for the worker if it should use Gemini too).
2. Check that `Assistant__Gemini__Model` (default `gemini-2.5-flash`) is listed for your key in AI Studio;
   the free tier offers Flash models with low per-project rate limits (a `429` from Google answers
   `503 assistant_unavailable` "limite gratuito do Gemini").
3. The API calls `models/{model}:generateContent` with `responseMimeType: application/json` and a
   `responseSchema`; the key goes in the `x-goog-api-key` header, never in a URL or a log line.

**Data use (LGPD):** on the free tier Google may use prompts and responses to improve its products,
including human review. The assistant sends the user's chat messages and the current analysis settings
(no video, no transcript, no e-mail). Choose Gemini only with that in mind and disclose it in the
privacy page; the local model keeps everything on your server.

## Media providers (optional)

`Providers__YouTube__ApiKey`, `Providers__Twitch__ClientId`/`ClientSecret` are reserved for reading public
metadata through official APIs. In the MVP no platform supports direct processing: `POST /api/sources/validate`
identifies Twitch/YouTube/Kick links and answers `supported: false` with the alternatives (upload or the OBS
companion). The `IMediaProvider` abstraction (`Features/Sources`) is where an authorised integration would
plug in.

## Reverse proxy (production)

In production `web` and `api` sit behind one HTTPS reverse proxy (`infra/README.md`), so every
connection reaches the API from the proxy's address. Per-IP rate limiting, the login lockout and the IP
stored with refresh tokens must see the real client, which the proxy sends in `X-Forwarded-For` /
`X-Forwarded-Proto`. Those headers are honoured only from trusted proxies:

```
ReverseProxy__Enabled=true
ReverseProxy__KnownProxies__0=10.0.0.5        # proxy address(es), besides loopback
ReverseProxy__KnownNetworks__0=10.0.0.0/8      # or CIDR ranges
ReverseProxy__ForwardLimit=1                   # hops to resolve (one per proxy in the chain)
# ReverseProxy__TrustAllProxies=true           # only when the API is reachable exclusively through the proxy (PaaS)
```

Without `ReverseProxy__Enabled` the connection address is used as-is, which is right for local development
and for Docker Compose publishing the API directly. Enabling it with an incomplete proxy list leaves users
behind the unlisted proxy sharing one rate-limit bucket; enabling `TrustAllProxies` while the API is also
reachable directly lets clients spoof their address.

## Other knobs

| Section | Keys |
|---|---|
| `Jobs` | `LeaseSeconds` (180), `MaxAttempts` (3), `ReaperIntervalSeconds` (30), `SourceUrlHours` (12, validity of the signed source URL given to workers) |
| `Retention` | `TempUploadHours` (24), `CleanupIntervalMinutes` (15), `AccountPurgeDelayHours` (24) |
| `RateLimiting` | `AuthPerMinute` (10 per IP on the credential endpoints: register, login, forgot/reset password, verify e-mail, resend verification, change password, `DELETE /api/account`), `RefreshPerMinute` (60 per IP on `POST /api/auth/refresh`; `/me` and `/logout` are not limited), `UploadsPerMinute` (30 per user), `TransfersPerMinute` (600 per IP on the signed `PUT /api/storage/upload`), `AssistantPerMinute` (20 per user on `POST /api/assistant/chat`) |
| `Assistant` | `Provider`, `Local__BaseUrl`, `Local__Model`, `Local__WarmupOnStartup`, `Gemini__ApiKey`, `Gemini__Model`, `Gemini__ThinkingBudget`, `TimeoutSeconds`, `StatusCacheSeconds` (see "Settings assistant") |
| `Auth` | `CookieDomain` (see "Auth cookies across subdomains"), `CookieSecure`, `AccessTokenMinutes` (15), `RefreshTokenDays` (30), `RequireConfirmedEmail`, `LockoutMaxFailedAttempts` (5 wrong passwords on login, change-password or account deletion), `LockoutMinutes` (5) |
| `ReverseProxy` | `Enabled`, `KnownProxies__0…`, `KnownNetworks__0…`, `ForwardLimit`, `TrustAllProxies` (see above) |
| `Media` | `UploadUrlMinutes` (120), `DownloadUrlMinutes` (15), `PreviewUrlMinutes` (30), `ThumbnailUrlMinutes` (60) |
| `Cors` | `AllowedOrigins__0…` (credentials are allowed) |
| `Database` | `MigrateOnStartup` (true in Development; production applies `dotnet ef database update` in the pipeline) |
| `Plans` | Plan limits/prices, e.g. `Plans__pro__Limits__MonthlyProcessingMinutes=2400` (upserted on startup) |
| `Branding` | `Name`, `SupportEmail` (used in e-mails and notifications) |
