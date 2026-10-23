import { expect, test } from "@playwright/test";
import fs from "node:fs";
import path from "node:path";

/**
 * Lives page with real data. Needs an account that already went through a live session
 * (run scripts/e2e/live_smoke.py first, which leaves an ended session with highlights):
 *   E2E_LIVE_EMAIL=live-e2e-…@example.com  (password Senha12345)
 */
const EMAIL = process.env.E2E_LIVE_EMAIL;
const SHOTS = path.join(__dirname, "..", "screenshots");

test("lives page shows the real session, its segments and highlights", async ({ page }) => {
  test.skip(!EMAIL, "set E2E_LIVE_EMAIL to an account with an ended live session");
  const errors: string[] = [];
  page.on("pageerror", (error) => errors.push(`pageerror on ${new URL(page.url()).pathname}: ${error.message}`));
  page.on("response", (response) => {
    if (response.status() >= 400 && !/\/api\/auth\/(me|refresh)$/.test(response.url())) {
      errors.push(`http ${response.status()} ${response.url()}`);
    }
  });

  await page.goto("/login");
  await page.getByLabel("E-mail").fill(EMAIL!);
  await page.locator("#password").fill("Senha12345");
  await page.getByRole("button", { name: "Entrar" }).click();
  await expect(page).toHaveURL(/\/dashboard/, { timeout: 30_000 });

  await page.goto("/live");
  const sessions = page.getByRole("list", { name: "Sessões ao vivo" });
  await expect(sessions).toBeVisible({ timeout: 30_000 });
  await expect(sessions.getByText("Encerrada").first()).toBeVisible();
  fs.mkdirSync(SHOTS, { recursive: true });
  await page.screenshot({ path: path.join(SHOTS, "30-live.png"), fullPage: true });

  await sessions.locator('a[href^="/live/"]').first().click();
  await expect(page).toHaveURL(/\/live\/[0-9a-f-]{36}/);
  const highlights = page.getByRole("region", { name: "Destaques" });
  await expect(highlights).toBeVisible({ timeout: 30_000 });
  await expect(highlights.locator('a[href^="/clips/"]').first()).toBeVisible();
  await page.waitForLoadState("networkidle").catch(() => undefined);
  await page.screenshot({ path: path.join(SHOTS, "31-live-session.png"), fullPage: true });

  expect.soft(errors, errors.join("\n")).toEqual([]);
});
