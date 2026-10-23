import { describe, expect, it } from "vitest";
import {
  applyPlanLimits,
  buildDefaultSettings,
  DEFAULT_ANALYSIS_SETTINGS,
  isSubtitlePositionAllowed,
  settingsForProject,
} from "@/features/projects/analysis-settings";
import type { PlanLimits } from "@/lib/api/types";

const freeLimits: PlanLimits = {
  monthlyProcessingMinutes: 60,
  maxSourceDurationMinutes: 120,
  maxFileSizeMb: 2048,
  maxActiveProjects: 3,
  maxCandidatesPerJob: 15,
  maxAutoRenderPerJob: 3,
  maxRendersPerMonth: 10,
  maxRenderHeight: 720,
  maxConcurrentJobs: 1,
  priority: 0,
  retentionDays: 7,
  storageQuotaGb: 5,
  transcriptionModel: "base",
  allowWebcamLayout: false,
  allowCustomSubtitleStyles: false,
};

describe("new-project defaults", () => {
  it("read every editing preference, including the language spoken in the recordings", () => {
    const settings = buildDefaultSettings({
      aspectRatio: "1:1",
      subtitlesEnabled: false,
      subtitleStyle: "boxed",
      subtitlePosition: "top",
      minClipSeconds: 20,
      maxClipSeconds: 90,
      targetClipSeconds: 45,
      language: "en",
    });
    expect(settings).toMatchObject({
      aspectRatio: "1:1",
      subtitles: { enabled: false, style: "boxed", position: "top" },
      minClipSeconds: 20,
      maxClipSeconds: 90,
      targetClipSeconds: 45,
      language: "en",
    });
  });

  it("fall back to the plan's default subtitle style and framing when the plan does not include them", () => {
    const settings = buildDefaultSettings({ subtitleStyle: "boxed" }, freeLimits);
    expect(settings.subtitles.style).toBe("bold");
    expect(settings.maxCandidates).toBe(15);
    expect(settings.autoRenderTop).toBe(3);
    expect(settingsForProject({ framing: { mode: "webcam-split" } }, null, freeLimits).framing.mode).toBe("auto");
  });

  it("put subtitles at the bottom when the plan has no custom subtitle styles, as the API renders them", () => {
    expect(buildDefaultSettings({ subtitleStyle: "clean", subtitlePosition: "top" }, freeLimits).subtitles).toEqual({
      enabled: true,
      style: "bold",
      position: "bottom",
    });
    expect(settingsForProject({ subtitles: { enabled: false, style: "boxed", position: "center" } }, null, freeLimits).subtitles).toEqual({
      enabled: false,
      style: "bold",
      position: "bottom",
    });
    const edited = { ...DEFAULT_ANALYSIS_SETTINGS, subtitles: { enabled: true, style: "bold" as const, position: "top" as const } };
    expect(applyPlanLimits(edited, freeLimits).subtitles.position).toBe("bottom");
  });

  it("keep the chosen subtitle position when the plan allows it (or while the plan is still unknown)", () => {
    const paid = { ...freeLimits, allowCustomSubtitleStyles: true };
    expect(buildDefaultSettings({ subtitleStyle: "clean", subtitlePosition: "top" }, paid).subtitles).toMatchObject({ style: "clean", position: "top" });
    expect(buildDefaultSettings({ subtitlePosition: "center" }).subtitles.position).toBe("center");
    expect(isSubtitlePositionAllowed("bottom", freeLimits)).toBe(true);
    expect(isSubtitlePositionAllowed("top", freeLimits)).toBe(false);
    expect(isSubtitlePositionAllowed("center", freeLimits)).toBe(false);
    expect(isSubtitlePositionAllowed("center", paid)).toBe(true);
    expect(isSubtitlePositionAllowed("top", null)).toBe(true);
  });

  it("keep clip lengths inside the form range", () => {
    expect(buildDefaultSettings({ minClipSeconds: 5, maxClipSeconds: 300, targetClipSeconds: 200 })).toMatchObject({
      minClipSeconds: 10,
      maxClipSeconds: 120,
      targetClipSeconds: 120,
    });
  });

  it("use the project's own settings for re-analysis, not the profile preferences", () => {
    const settings = settingsForProject({ language: "es", aspectRatio: "16:9", minClipSeconds: 30, maxClipSeconds: 60, targetClipSeconds: 40 }, { language: "pt", aspectRatio: "9:16" });
    expect(settings).toMatchObject({ language: "es", aspectRatio: "16:9", minClipSeconds: 30, targetClipSeconds: 40 });
    expect(settingsForProject(null, { language: "pt" }).language).toBe("pt");
  });
});
