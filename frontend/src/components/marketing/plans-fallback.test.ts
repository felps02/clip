// @vitest-environment node
import { existsSync, readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import { FALLBACK_PLANS } from "@/components/marketing/plans-fallback";

/** The API's default plan configuration, the source the reference values mirror. */
const SEED_PATH = fileURLToPath(new URL("../../../../backend/src/ClipForge.Api/appsettings.json", import.meta.url));

interface SeedPlan {
  Name: string;
  PriceMonthlyCents?: number | null;
  Description: string;
  Limits: Record<string, unknown>;
  Features: string[];
}

function camel(key: string): string {
  return key.charAt(0).toLowerCase() + key.slice(1);
}

describe("FALLBACK_PLANS", () => {
  it("carries only the free plan's price (0), never a paid price", () => {
    for (const plan of FALLBACK_PLANS) expect(plan.priceMonthlyCents ?? null).toBe(plan.code === "free" ? 0 : null);
  });

  // Skipped only when the frontend is checked out without the backend (e.g. a web-only Docker context).
  it.skipIf(!existsSync(SEED_PATH))("mirrors the API plan seed (names, descriptions, limits, features)", () => {
    const settings = JSON.parse(readFileSync(SEED_PATH, "utf8").replace(/^﻿/, "")) as { Plans: Record<string, SeedPlan> };
    expect(FALLBACK_PLANS.map((plan) => plan.code)).toEqual(Object.keys(settings.Plans));
    for (const plan of FALLBACK_PLANS) {
      const seed = settings.Plans[plan.code];
      expect(plan.name).toBe(seed.Name);
      expect(plan.description).toBe(seed.Description);
      expect(plan.features).toEqual(seed.Features);
      // Free in the seed (price 0) ⇔ free in the reference values; paid prices are never mirrored.
      expect(plan.priceMonthlyCents === 0).toBe(seed.PriceMonthlyCents === 0);
      const seedLimits = Object.fromEntries(Object.entries(seed.Limits).map(([key, value]) => [camel(key), value]));
      expect(plan.limits).toEqual(seedLimits);
    }
  });
});
