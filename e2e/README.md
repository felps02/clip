# Browser end-to-end tests

Playwright tests that drive the **real stack**: they register a new user, upload a real
video, wait for the worker to analyse it, open the generated clips and download one.
Nothing is mocked, so the API, the worker and the web app must be running.

```powershell
# start the stack (three terminals) — see the root README
.\scripts\dev-api.ps1
.\scripts\dev-worker.ps1
.\scripts\dev-web.ps1

# generate the test video once (pt-BR Windows TTS speech + loud bursts, 70 s; --lang en for English)
processing\.venv\Scripts\python.exe scripts\e2e\make_fixture.py --out "$env:LOCALAPPDATA\ClipForge\fixtures\e2e-fixture-ptbr.mp4"

cd e2e
pnpm install          # no browser download: the tests use the installed Microsoft Edge
pnpm test             # or: $env:E2E_BROWSER="chrome"; pnpm test
```

| Variable       | Default                                               |
|----------------|-------------------------------------------------------|
| `E2E_BASE_URL` | `http://localhost:3000` (must match the API CORS origin) |
| `E2E_FIXTURE`  | `%LOCALAPPDATA%\ClipForge\fixtures\e2e-fixture-ptbr.mp4` |
| `E2E_BROWSER`  | `msedge` (`chrome` or `chromium` also work)           |

Screenshots of every page are written to `e2e/screenshots/` for visual review.
The API-level equivalent (no browser) is `scripts/e2e/smoke.py`.
