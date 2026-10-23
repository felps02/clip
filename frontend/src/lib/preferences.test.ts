import { describe, expect, it } from "vitest";
import {
  DEFAULT_EDITING_PREFERENCES,
  DEFAULT_NOTIFICATION_PREFERENCES,
  normalizeEditingPreferences,
  normalizeNotificationPreferences,
  validateEditingPreferences,
} from "@/lib/preferences";

describe("profile preference contract", () => {
  it("has the documented defaults", () => {
    expect(DEFAULT_EDITING_PREFERENCES).toEqual({
      aspectRatio: "9:16",
      subtitlesEnabled: true,
      subtitleStyle: "bold",
      subtitlePosition: "bottom",
      minClipSeconds: 15,
      maxClipSeconds: 60,
      targetClipSeconds: 30,
      language: "auto",
    });
    expect(DEFAULT_NOTIFICATION_PREFERENCES).toEqual({
      emailOnJobCompleted: false,
      emailOnJobFailed: true,
      emailOnQuotaWarning: true,
      emailOnBilling: true,
    });
  });

  it("fills missing or unknown fields with defaults and restores min <= target <= max within 5-300", () => {
    expect(normalizeEditingPreferences(null)).toEqual(DEFAULT_EDITING_PREFERENCES);
    expect(normalizeEditingPreferences({ aspectRatio: "16:9", language: "es" })).toMatchObject({ aspectRatio: "16:9", language: "es", subtitleStyle: "bold" });
    expect(
      normalizeEditingPreferences({ aspectRatio: "4:3" as never, subtitleStyle: "neon" as never, language: "fr" as never }),
    ).toMatchObject({ aspectRatio: "9:16", subtitleStyle: "bold", language: "auto" });
    expect(normalizeEditingPreferences({ minClipSeconds: 2, maxClipSeconds: 900, targetClipSeconds: 1000 })).toMatchObject({
      minClipSeconds: 5,
      maxClipSeconds: 300,
      targetClipSeconds: 300,
    });
    expect(normalizeEditingPreferences({ minClipSeconds: 90, maxClipSeconds: 40 })).toMatchObject({
      minClipSeconds: 90,
      maxClipSeconds: 90,
      targetClipSeconds: 90,
    });
  });

  it("validates the same rules as the API", () => {
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES })).toEqual({});
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES, minClipSeconds: 4 })).toHaveProperty("minClipSeconds");
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES, maxClipSeconds: 301 })).toHaveProperty("maxClipSeconds");
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES, minClipSeconds: 40, maxClipSeconds: 30, targetClipSeconds: 35 })).toHaveProperty(
      "maxClipSeconds",
    );
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES, targetClipSeconds: 61 })).toHaveProperty("targetClipSeconds");
    expect(validateEditingPreferences({ ...DEFAULT_EDITING_PREFERENCES, minClipSeconds: 15.5 })).toHaveProperty("minClipSeconds");
  });

  it("normalizes notification preferences field by field", () => {
    expect(normalizeNotificationPreferences(undefined)).toEqual(DEFAULT_NOTIFICATION_PREFERENCES);
    expect(normalizeNotificationPreferences({ emailOnJobCompleted: true })).toEqual({ ...DEFAULT_NOTIFICATION_PREFERENCES, emailOnJobCompleted: true });
  });
});
