# ClipForge — web frontend

Next.js 16 (App Router, Cache Components) + React 19 + TypeScript + Tailwind CSS v4 + TanStack Query.
UI copy is pt-BR; code, identifiers and docs are English. The public brand name is read from
`NEXT_PUBLIC_BRAND_NAME` (`src/config/brand.ts`); "ClipForge" is only the codename fallback.

## Requirements

- Node 24, pnpm 9 (`corepack enable`)
- The API (port 5080 of the host that serves the page by default, e.g. `http://localhost:5080`).
  The app builds and runs without it; pages show honest error/empty states until the API answers.

## Scripts

| Command          | What it does                                                   |
| ---------------- | -------------------------------------------------------------- |
| `pnpm dev`       | dev server on http://localhost:3000                            |
| `pnpm build`     | production build (`output: "standalone"`)                      |
| `pnpm start`     | serve the production build                                     |
| `pnpm lint`      | ESLint (next core-web-vitals + typescript + React Compiler rules) |
| `pnpm typecheck` | `next typegen` (route types) + `tsc --noEmit`                  |
| `pnpm test`      | Vitest + Testing Library (jsdom), single run                   |
| `pnpm test:watch`| Vitest in watch mode                                           |

## Environment

Copy `.env.local.example` to `.env.local` (never committed):

```
# {host} = the host name the page was opened with ("localhost" during SSR). Default when unset.
NEXT_PUBLIC_API_BASE_URL=http://{host}:5080
NEXT_PUBLIC_BRAND_NAME=ClipForge
# Server-side auth guard on the API's cf_access cookie; see "Web and API on different hosts".
NEXT_PUBLIC_COOKIE_GUARD=true
# Optional; empty values hide the corresponding UI instead of showing placeholders.
NEXT_PUBLIC_SUPPORT_EMAIL=
NEXT_PUBLIC_LEGAL_COMPANY_NAME=
NEXT_PUBLIC_LEGAL_CNPJ=
NEXT_PUBLIC_LEGAL_ADDRESS=
NEXT_PUBLIC_LEGAL_FORUM=
NEXT_PUBLIC_LEGAL_DPO_EMAIL=
NEXT_PUBLIC_LEGAL_UPDATED_AT=
NEXT_PUBLIC_COMPANION_DOWNLOAD_URL=
```

`NEXT_PUBLIC_*` values are inlined at build time (also as Docker build args).

- `NEXT_PUBLIC_API_BASE_URL` may contain the token `{host}`, replaced at runtime by
  `window.location.hostname` in the browser and by `localhost` during SSR
  (`getApiBaseUrl()` in `src/lib/api/client.ts`; every request, the plans on the landing page and
  the "Baixar todos (.zip)" link use it). Unset, it is `http://{host}:5080`: the same bundle opened
  at `http://localhost:3000` talks to `http://localhost:5080` and at `http://10.0.0.57:3000` to
  `http://10.0.0.57:5080` (same site, so the auth cookies keep working) — no rebuild per address
  for LAN demos. A fixed URL (`https://api.example.com`) is used as is.

- `/terms` and `/privacy` are published only when every `NEXT_PUBLIC_LEGAL_*` value and the
  support e-mail are set (`resolveLegal()` in `src/config/brand.ts`); otherwise they render a
  short "documento ainda não publicado" notice and the footer omits the company line. Bracket
  placeholders are never shipped.
- The OBS companion is announced as installable only when `NEXT_PUBLIC_COMPANION_DOWNLOAD_URL`
  is set; the Lives page and Settings → Integrações otherwise say it is still in development.

### Web and API on different hosts

The server-side guard (`src/proxy.ts`) redirects app routes to `/login` when the request has no
`cf_access` cookie. That cookie is set by the API, so the browser must send it to the web host
as well:

- Same host (e.g. `localhost:3000` + `localhost:5080`, or the API reverse-proxied under the web
  origin): works out of the box.
- **Different hosts in production** (e.g. `app.example.com` + `api.example.com`): set
  `Auth__CookieDomain` on the API to a domain covering both (`.example.com`), together with
  `Auth__CookieSecure=true` and `Cors__AllowedOrigins__0=https://app.example.com`.
- If no shared cookie domain is possible, build the web with `NEXT_PUBLIC_COOKIE_GUARD=false`:
  the proxy stops redirecting and `AppShell` (the app layout) is the only guard — it holds the
  page until `GET /api/auth/me` first confirms the session and signs out on a final 401 (a later
  refetch that fails for another reason only shows a retryable alert; the page stays mounted).

A misconfiguration cannot loop: before sending someone from `/login` or `/register` into the app
the pages ask the web host (`GET /auth/handoff?next=…`, `src/app/auth/handoff/route.ts`) whether
the proxy would accept that navigation, and show an explanation instead of navigating when it
would not.

## Structure

```
src/
  app/                 routes (App Router)
    (marketing)/       /, /terms, /privacy — static, server components
    (auth)/            /login, /register, /forgot-password, /reset-password, /verify-email
    (app)/             authenticated app: /dashboard, /new, /projects, /projects/[id],
                       /library, /clips/[id], /live, /live/[id], /billing, /settings
  proxy.ts             auth guard: app routes without the cf_access cookie go to /login?next=
                       (off with NEXT_PUBLIC_COOKIE_GUARD=false); guest pages are never bounced
                       on cookie presence (it proves nothing); rules in lib/auth-guard.ts
  app/auth/handoff/    GET /auth/handoff: would the guard accept a navigation to `next`?
  config/brand.ts      brand name, tagline, support e-mail, legal identity, companion download link
  lib/api/             typed client (ProblemDetails → ApiError, one refresh-and-retry on 401),
                       DTOs mirroring docs/API.md, per-feature modules and TanStack Query hooks
  lib/                 format (pt-BR), labels (status/category maps), upload (XHR), media, validation (zod),
                       preferences (profile preference contract), uuid (Idempotency-Key with fallback)
  components/ui        design-system primitives (Button, Input, Select, Slider, Switch, Dialog, …)
  components/layout    AppShell, Sidebar, Topbar, NotificationsMenu, UserMenu, PageHeader
  components/video     VideoPlayer, ClipCard, CandidateCard, JobStatus, TrimSlider
  components/marketing landing sections, plans (with static fallback)
  features/            page-level client components per feature
```

## Conventions

- All API access goes through `src/lib/api/` with `credentials: "include"`; tokens never touch
  JS storage. Signed media URLs are short-lived, so the UI re-fetches resources before playing
  or downloading.
- Sessions: the `cf_access` cookie expires with the 15-minute JWT, so `/login` and `/register`
  call `POST /api/auth/refresh` once on load (`useRestoreSession`) and send the user to `next`
  when the 30-day refresh cookie is still valid and `/auth/handoff` confirms the web host sees
  the access cookie. A request that stays `401` after the client's refresh-and-retry makes
  `AppShell` call `POST /api/auth/logout` (clears both cookies) before redirecting to `/login`,
  so a stale cookie can never cause a login/app redirect loop.
- "Sair" only leaves the app after `POST /api/auth/logout` succeeded (then `/login?signedOut=1`,
  which skips the session probe). If the call fails the person stays on the page, with the
  session and the cache intact and a persistent alert offering "Tentar novamente".
- Profile preferences follow the shared contract in `docs/API.md` (Account): `src/lib/preferences.ts`
  holds the defaults and validation; Settings → Preferências de edição edits all eight fields and
  the new-project form starts from them (`buildDefaultSettings`), capped by the plan
  (subtitle styles other than Destaque and positions other than Embaixo need
  `allowCustomSubtitleStyles`, the webcam layout needs `allowWebcamLayout`; every form disables
  what the plan lacks, since the API would replace it). The interface language is pt-BR only, so
  Perfil shows it as information.
- Destructive or immediately charged actions (delete, cancel a running analysis, switching a paid
  subscription to another plan in place) always go through `ConfirmDialog`. It has no trigger, so
  it returns keyboard focus itself when it closes: to the element that opened it, or to the
  `returnFocus` targets a page passes (`afterConfirm` when the opener goes away with what it acted
  on, `afterCancel`, `fallback`), never to `<body>`.
- Post-login destinations (`next`) go through `safeNextPath` (`src/lib/utils.ts`): same-origin
  paths only, no backslashes, control characters or whitespace, dot segments resolved.
- Job progress is polled every 2 s (`refetchInterval`) only while a job is active.
- Live capture (`/live`, `/live/[id]`, contract in `docs/LIVE_CAPTURE.md` §2/§4): sessions, the
  open session and the device list are polled every 5 s only while a session is `waiting`, `live`
  or `ending`, never in a hidden tab (`refetchIntervalInBackground: false`); returning to the tab
  refreshes once, and "Atualizar" refreshes on demand. When the API answers 404/501 the page says
  "Captura ao vivo ainda não disponível neste ambiente". The device token is shown once, selected,
  with a copy button (Clipboard API → `execCommand("copy")` → "copie manualmente"). Only the token
  form's state holds it until it is hidden: the mutation uses `gcTime: 0` and is reset on success,
  so the query client does not keep it.
- Relative times (`RelativeTime`, `ElapsedTime`) render only after hydration and then follow a
  shared clock (`useNow`: one timer per precision, running only while something reads it), so
  "há 5 minutos" keeps moving when polls return the same data. `ElapsedTime` counts seconds under a
  minute and never says "agora" ("Sem sinal do companion · último contato há 35 segundos").
- `cacheComponents` is enabled (the create-next-app default for this Next.js version): every
  client tree that reads request data (cookies, search params, params) or the clock sits behind
  a `<Suspense>` boundary in the route-group layouts; marketing pages are static shells.
- Design tokens live in `src/app/globals.css` (`@theme`): graphite surfaces, one accent colour,
  semantic colours, radii 8/12/16 px. Dark theme only in the MVP.
- No fake data: candidates, scores ("relevância estimada", an internal 0–100 estimate), clips
  and live states always come from the API; unavailable signals are shown as unavailable.

## Tests

`pnpm test` runs unit tests for the API client (problem details, refresh-once on 401, no refresh
for credential endpoints, no raw non-JSON bodies), formatters, the XHR upload helper (mocked
`XMLHttpRequest`), the UUID fallback (no `crypto.randomUUID`), the job status label map and live
region, the plans section (skeleton while loading, labelled reference values on failure) and its
sync with the API plan seed, `ClipCard`, `CandidateCard` (score accessibility), the auth proxy and
the cookie-guard flag, `AppShell` (sign-out on a final 401, "Sair" failure keeps the session,
client-side guard), the login session probe and hand-off, the profile preference contract and the
Settings forms, plan gating in the analysis form, the Lives page states, the library project
filter (no polling), the notifications menu (keyboard), `ChoiceGroup`/`UsageRow`/`FormField`
accessibility, `ConfirmDialog` focus return, the relative-time clock (fake timers), the
analysis-job picker, the legal identity resolver and legal pages, the companion
availability notice, and the projects, project detail and billing pages (mocked `fetch` +
`next/navigation`). Live capture has its own tests: the typed API module, the 5 s polling rules
(fake timers, hidden tab, final states), the Lives page (sessions, companion chips, counters,
devices, revoke and stop confirmations and where focus goes after them, one-time token copy
fallbacks, the token kept out of the query cache, relative times that keep moving while polls
return the same data, server render vs hydration), the session detail page, the setup guide and
the notification labels and links.

## Docker

```
docker build -t clipforge-web \
  --build-arg NEXT_PUBLIC_API_BASE_URL=https://api.example.com \
  --build-arg NEXT_PUBLIC_BRAND_NAME="Minha Marca" \
  --build-arg NEXT_PUBLIC_SUPPORT_EMAIL=suporte@minhamarca.com.br \
  --build-arg NEXT_PUBLIC_LEGAL_COMPANY_NAME="Minha Marca Ltda." .
docker run -p 3000:3000 clipforge-web
```

All optional `NEXT_PUBLIC_LEGAL_*` / `NEXT_PUBLIC_COMPANION_DOWNLOAD_URL` / `NEXT_PUBLIC_COOKIE_GUARD`
arguments are accepted the same way (see `.env.local.example`).

The image is multi-stage (pnpm install → `next build` standalone → `node:24-alpine` runtime as a
non-root user).
