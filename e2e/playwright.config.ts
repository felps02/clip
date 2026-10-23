import { defineConfig, devices } from "@playwright/test";

/**
 * Browser E2E against a running stack. Nothing is mocked: the tests register real users,
 * upload a real video and wait for the worker to produce clips.
 *
 *   E2E_BASE_URL      web app (default http://localhost:3000 — must match the API CORS origin)
 *   E2E_API_URL       API (default http://localhost:5080)
 *   E2E_FIXTURE       video to upload (default %LOCALAPPDATA%/ClipForge/fixtures/e2e-fixture.mp4)
 *   E2E_BROWSER       msedge | chrome | chromium (default msedge: uses the installed Edge, no download)
 */
const channel = process.env.E2E_BROWSER ?? "msedge";

export default defineConfig({
  testDir: "./tests",
  timeout: 6 * 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  outputDir: "./test-results",
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://localhost:3000",
    channel: channel === "chromium" ? undefined : channel,
    locale: "pt-BR",
    timezoneId: "America/Sao_Paulo",
    screenshot: "only-on-failure",
    trace: "retain-on-failure",
    video: "off",
    actionTimeout: 20_000,
    navigationTimeout: 45_000,
  },
  projects: [
    { name: "desktop", use: { ...devices["Desktop Edge"], viewport: { width: 1440, height: 900 } } },
  ],
});
