import { expect, test, type Download, type Page } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";

const FIXTURE =
  process.env.E2E_FIXTURE ?? path.join(process.env.LOCALAPPDATA ?? "", "ClipForge", "fixtures", "e2e-fixture-ptbr.mp4");
const SHOTS = path.join(__dirname, "..", "screenshots");

/**
 * HTTP errors the product is expected to produce during this flow. Anything else that
 * answers 4xx/5xx, any uncaught page error and any console error that is not a plain
 * "failed to load resource" line fails the run.
 */
const EXPECTED_HTTP_ERRORS: Array<{ status: number; url: RegExp; why: string }> = [
  { status: 401, url: /\/api\/auth\/(me|refresh)$/, why: "session probe while signed out" },
  { status: 404, url: /\/api\/live\/sessions/, why: "live capture is not available in this environment (phase 5)" },
  { status: 501, url: /\/api\/live\/sessions/, why: "live capture is not available in this environment (phase 5)" },
];

function watchErrors(page: Page): string[] {
  const errors: string[] = [];
  const where = () => new URL(page.url()).pathname;
  page.on("pageerror", (error) => errors.push(`pageerror on ${where()}: ${error.message}`));
  page.on("console", (message) => {
    if (message.type() === "error" && !message.text().startsWith("Failed to load resource")) {
      errors.push(`console on ${where()}: ${message.text()}`);
    }
  });
  page.on("response", (response) => {
    const status = response.status();
    if (status < 400) return;
    const url = response.url();
    if (EXPECTED_HTTP_ERRORS.some((e) => e.status === status && e.url.test(url))) return;
    errors.push(`http ${status} ${response.request().method()} ${url}`);
  });
  return errors;
}

async function shot(page: Page, name: string): Promise<void> {
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, `${name}.png`), fullPage: true });
}

async function expectNoHorizontalOverflow(page: Page, label: string): Promise<void> {
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect.soft(overflow, `${label}: page wider than the viewport by ${overflow}px`).toBeLessThanOrEqual(1);
}

/** Captures a download started either in the page itself or in a popup it opens. */
function captureDownload(page: Page, timeoutMs = 60_000): Promise<Download> {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error("no download started")), timeoutMs);
    const done = (download: Download) => {
      clearTimeout(timer);
      resolve(download);
    };
    page.once("download", done);
    page.context().once("page", (popup) => popup.once("download", done));
  });
}

test("landing, legal pages and auth guard", async ({ page }) => {
  const errors = watchErrors(page);
  await page.goto("/");
  await expect(page.getByRole("heading", { level: 1 })).toContainText("Transforme horas de live");
  await shot(page, "01-landing-desktop");

  await page.setViewportSize({ width: 360, height: 780 });
  await page.goto("/");
  await expectNoHorizontalOverflow(page, "landing 360px");
  await shot(page, "02-landing-360");
  await page.setViewportSize({ width: 1440, height: 900 });

  for (const route of ["/terms", "/privacy"]) {
    await page.goto(route);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  }

  await page.goto("/dashboard");
  await expect(page).toHaveURL(/\/login\?next=%2Fdashboard/);
  expect.soft(errors, errors.join("\n")).toEqual([]);
});

test("register, upload, analyse, review, edit and download", async ({ page }) => {
  test.skip(!fs.existsSync(FIXTURE), `fixture not found: ${FIXTURE}`);
  const errors = watchErrors(page);
  const email = `web-e2e-${Date.now()}@example.com`;

  // --- register -------------------------------------------------------------------------
  await page.goto("/register");
  await page.getByLabel("Nome de exibição").fill("Teste Navegador");
  await page.getByLabel("E-mail").fill(email);
  await page.locator("#password").fill("Senha12345");
  await page.getByRole("checkbox").first().check();
  await shot(page, "03-register");
  await page.getByRole("button", { name: "Criar conta" }).click();
  await expect(page).toHaveURL(/\/dashboard/, { timeout: 30_000 });
  await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
  await shot(page, "04-dashboard-empty");

  // --- new project: upload + assistant ----------------------------------------------------
  await page.goto("/new");
  await page.locator('input[type="file"]').setInputFiles(FIXTURE);
  const start = page.getByRole("button", { name: "Enviar e analisar" });
  await expect(start).toBeEnabled({ timeout: 30_000 });

  // The assistant is the product's own AI (local model). When this environment has it, a plain
  // request must change the settings and the reply must list what really changed; otherwise the
  // page must say it is unavailable (never a fake reply).
  const assistantInput = page.getByLabel("Mensagem para o assistente");
  await expect(assistantInput.or(page.getByText("Assistente indisponível neste ambiente")).first()).toBeVisible({ timeout: 30_000 });
  if ((await assistantInput.count()) > 0 && (await assistantInput.isEnabled())) {
    await assistantInput.fill("quero clipes mais engraçados, de uns 30 segundos, pro tiktok");
    await page.getByRole("button", { name: "Enviar", exact: true }).click();
    const changes = page.getByRole("list", { name: "Alterações aplicadas" }).last();
    await expect(changes).toBeVisible({ timeout: 120_000 });
    const applied = (await changes.innerText()).toLowerCase();
    console.log(`assistant changes: ${applied.replace(/\s+/g, " ")}`);
    expect.soft(applied, "assistant applied the funny focus").toMatch(/engraç/);
    expect.soft(applied, "assistant applied a 30 s target").toMatch(/30/);
  } else {
    console.log("assistant unavailable in this environment");
  }
  await shot(page, "05-new-project-file-selected");
  await start.click();
  await expect(page).toHaveURL(/\/projects\/[0-9a-f-]{36}/, { timeout: 120_000 });
  const projectPath = new URL(page.url()).pathname;
  await shot(page, "06-project-processing");

  // --- wait for the real analysis: the page leads with the finished clips ----------------------
  await expect(page.getByText("Concluído", { exact: true }).first()).toBeVisible({ timeout: 300_000 });
  await expect(page.getByRole("heading", { name: /Clipes prontos/ })).toBeVisible({ timeout: 60_000 });
  const clipLinks = page.locator('a[href^="/clips/"]');
  await expect(clipLinks.first()).toBeVisible({ timeout: 30_000 });
  const momentsToggle = page.getByRole("button", { name: /Momentos encontrados/ });
  if ((await momentsToggle.count()) > 0 && (await momentsToggle.first().getAttribute("aria-expanded")) === "false") {
    await momentsToggle.first().click();
  }
  await expect(page.getByText(/relevância estimada/i).first()).toBeVisible({ timeout: 15_000 });
  await shot(page, "07-project-completed");

  // --- download every clip at once (.zip) ---------------------------------------------------
  const zipPromise = captureDownload(page);
  await page.getByRole("link", { name: /Baixar todos/ }).or(page.getByRole("button", { name: /Baixar todos/ })).first().click();
  const zip = await zipPromise;
  expect(zip.suggestedFilename()).toMatch(/\.zip$/);
  const zipFile = await zip.path();
  expect(fs.readFileSync(zipFile).subarray(0, 2).toString("latin1"), "zip signature").toBe("PK");
  expect(fs.statSync(zipFile).size, "zip size").toBeGreaterThan(10_000);

  // --- clip editor + a single download ----------------------------------------------------------
  await clipLinks.first().click();
  await expect(page).toHaveURL(/\/clips\/[0-9a-f-]{36}/);
  const clipPath = new URL(page.url()).pathname;
  await expect(page.locator("video").first()).toBeVisible({ timeout: 30_000 });
  await shot(page, "08-clip-editor");
  let downloads = 0;
  page.on("download", () => downloads++);
  page.context().on("page", (popup) => popup.on("download", () => downloads++));
  const downloadPromise = captureDownload(page);
  await page.getByRole("button", { name: /^Baixar/ }).first().click();
  const download = await downloadPromise;
  const file = await download.path();
  expect(download.suggestedFilename()).toMatch(/\.mp4$/);
  expect(fs.statSync(file).size, "downloaded clip size").toBeGreaterThan(10_000);
  await page.waitForTimeout(3_000);
  expect(downloads, "one click on Baixar starts exactly one download").toBe(1);

  // --- library, dashboard and the remaining app pages -----------------------------------
  await page.goto("/library");
  await expect(page.locator('a[href^="/clips/"]').first()).toBeVisible({ timeout: 30_000 });
  await page.waitForLoadState("networkidle").catch(() => undefined);
  // Every thumbnail must really load (signed URL valid, served by the API).
  await expect
    .poll(
      () =>
        page.evaluate(() => {
          const images = Array.from(document.querySelectorAll("main img"));
          return images.length > 0 && images.every((img) => (img as HTMLImageElement).complete && (img as HTMLImageElement).naturalWidth > 0);
        }),
      { timeout: 30_000, message: "library thumbnails loaded" },
    )
    .toBe(true);
  await shot(page, "09-library");

  await page.goto("/dashboard");
  await expect(page.locator('a[href^="/clips/"]').first()).toBeVisible({ timeout: 30_000 });
  await shot(page, "10-dashboard-after");

  for (const [route, name] of [
    ["/projects", "11-projects"],
    ["/billing", "12-billing"],
    ["/settings", "13-settings"],
    ["/live", "14-live"],
  ] as const) {
    await page.goto(route);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await page.waitForLoadState("networkidle").catch(() => undefined);
    await shot(page, name);
  }

  // --- small screens ------------------------------------------------------------------------
  await page.setViewportSize({ width: 360, height: 780 });
  for (const [route, name] of [
    ["/dashboard", "15-dashboard-360"],
    ["/library", "16-library-360"],
    ["/billing", "17-billing-360"],
    ["/new", "18-new-360"],
    [projectPath, "19-project-360"],
    [clipPath, "20-clip-360"],
    ["/settings", "21-settings-360"],
    ["/live", "22-live-360"],
  ] as const) {
    await page.goto(route);
    await expect(page.getByRole("heading", { level: 1 })).toBeVisible();
    await page.waitForLoadState("networkidle").catch(() => undefined);
    await expectNoHorizontalOverflow(page, `${route} at 360px`);
    await shot(page, name);
  }

  expect.soft(errors, errors.join("\n")).toEqual([]);
});
