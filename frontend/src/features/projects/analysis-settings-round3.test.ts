import { describe, expect, it } from "vitest";
import {
  adjustForShortSource,
  applyPlanLimits,
  applySettingsPatch,
  autoRenderCap,
  autoRenderMode,
  buildDefaultSettings,
  changePaths,
  clipLengthBounds,
  DEFAULT_ANALYSIS_SETTINGS,
  isShortSource,
  normalizeKeywords,
  restoreSettingsPaths,
  settingsForProject,
  SETTINGS_PRESETS,
  trimSettingsForSubmit,
} from "@/features/projects/analysis-settings";
import type { AnalysisSettings, PlanLimits } from "@/lib/api/types";

const freeLimits: PlanLimits = {
  monthlyProcessingMinutes: 60,
  maxSourceDurationMinutes: 120,
  maxFileSizeMb: 2048,
  maxActiveProjects: 3,
  maxCandidatesPerJob: 15,
  maxAutoRenderPerJob: 15,
  maxRendersPerMonth: 10,
  maxRenderHeight: 720,
  maxConcurrentJobs: 1,
  priority: 0,
  retentionDays: 7,
  storageQuotaGb: 5,
  transcriptionModel: "base",
  allowWebcamLayout: false,
  allowCustomSubtitleStyles: false,
  assistantMessagesPerMonth: 100,
};

const preset = (id: string) => {
  const found = SETTINGS_PRESETS.find((item) => item.id === id);
  if (!found) throw new Error(`preset ${id} missing`);
  return found;
};

describe("auto-render default: every moment found becomes a clip", () => {
  it("defaults autoRenderTop to maxCandidates, capped by the plan", () => {
    expect(DEFAULT_ANALYSIS_SETTINGS.autoRenderTop).toBe(DEFAULT_ANALYSIS_SETTINGS.maxCandidates);
    expect(buildDefaultSettings(null, freeLimits)).toMatchObject({ maxCandidates: 15, autoRenderTop: 15 });
    expect(buildDefaultSettings(null, { ...freeLimits, maxAutoRenderPerJob: 3 })).toMatchObject({ maxCandidates: 15, autoRenderTop: 3 });
    expect(buildDefaultSettings(null, null)).toMatchObject({ maxCandidates: 20, autoRenderTop: 20 });
    expect(autoRenderCap(15, { ...freeLimits, maxAutoRenderPerJob: 3 })).toBe(3);
  });

  it("starts with no focus, no keywords and no instructions, also for projects saved before round 3", () => {
    expect(buildDefaultSettings(null, freeLimits)).toMatchObject({ focusCategories: [], customKeywords: [], instructions: null });
    const legacy = { minClipSeconds: 20, maxClipSeconds: 60, targetClipSeconds: 30 } as Partial<AnalysisSettings>;
    expect(settingsForProject(legacy, null, freeLimits)).toMatchObject({ focusCategories: [], customKeywords: [], instructions: null });
    expect(
      settingsForProject({ focusCategories: ["quote", "funny"], customKeywords: ["clutch"], instructions: "só as risadas" }, null, freeLimits),
    ).toMatchObject({ focusCategories: ["funny", "quote"], customKeywords: ["clutch"], instructions: "só as risadas" });
  });
});

describe("short recordings (shorter than the clip minimum)", () => {
  it("detects a recording shorter than the minimum", () => {
    expect(isShortSource(12.7, 15)).toBe(true);
    expect(isShortSource(15, 15)).toBe(false);
    expect(isShortSource(null, 15)).toBe(false);
    expect(isShortSource(0, 15)).toBe(false);
  });

  it("fits the range into the recording: min = max(5, whole seconds), target and max clamped to it", () => {
    const adjusted = adjustForShortSource(DEFAULT_ANALYSIS_SETTINGS, 12.7);
    expect(adjusted).toMatchObject({ minClipSeconds: 12, targetClipSeconds: 12, maxClipSeconds: 12 });
    expect(adjustForShortSource(DEFAULT_ANALYSIS_SETTINGS, 3.2)).toMatchObject({ minClipSeconds: 5, targetClipSeconds: 5, maxClipSeconds: 5 });
    // The plan limits keep the adjustment when they get the recording's duration…
    expect(applyPlanLimits(adjusted, freeLimits, { shortSourceSeconds: 12.7 })).toMatchObject({ minClipSeconds: 12, maxClipSeconds: 12 });
    // …and the usual form range applies again without it.
    expect(applyPlanLimits(adjusted, freeLimits)).toMatchObject({ minClipSeconds: 12, maxClipSeconds: 17 });
  });

  it("offers a 5 s to whole-recording range for the sliders", () => {
    expect(clipLengthBounds(12.7)).toEqual({ min: 5, max: 12, gap: 0 });
    expect(clipLengthBounds(3)).toEqual({ min: 5, max: 5, gap: 0 });
    expect(clipLengthBounds(null)).toEqual({ min: 10, max: 120, gap: 5 });
  });
});

describe("focus, keywords and instructions", () => {
  it("normalises custom keywords: trimmed, 1–40 characters, unique ignoring case, at most 20", () => {
    expect(normalizeKeywords(["  clutch ", "Clutch", "", "que   isso", "x".repeat(41), 3])).toEqual(["clutch", "que isso"]);
    expect(normalizeKeywords(Array.from({ length: 25 }, (_, index) => `palavra ${index}`))).toHaveLength(20);
  });

  it("keeps known focus categories once, in canonical order, and caps instructions at 500 characters", () => {
    const settings = applyPlanLimits(
      { ...DEFAULT_ANALYSIS_SETTINGS, focusCategories: ["quote", "funny", "quote", "nope" as never], instructions: "a".repeat(600) },
      freeLimits,
    );
    expect(settings.focusCategories).toEqual(["funny", "quote"]);
    expect(settings.instructions).toHaveLength(500);
    // Typing keeps trailing spaces; they are trimmed only when the settings are sent.
    expect(applyPlanLimits({ ...DEFAULT_ANALYSIS_SETTINGS, instructions: "mais risadas " }, freeLimits).instructions).toBe("mais risadas ");
    expect(trimSettingsForSubmit({ ...DEFAULT_ANALYSIS_SETTINGS, instructions: "  mais risadas  " }).instructions).toBe("mais risadas");
    expect(trimSettingsForSubmit({ ...DEFAULT_ANALYSIS_SETTINGS, instructions: "   " }).instructions).toBeNull();
  });
});

describe("assistant patches and undo", () => {
  it("applies known fields only and merges nested subtitles field by field", () => {
    const patched = applySettingsPatch(DEFAULT_ANALYSIS_SETTINGS, {
      focusCategories: ["funny"],
      subtitles: { position: "center" } as AnalysisSettings["subtitles"],
      unknownField: 1,
    } as Partial<AnalysisSettings>);
    expect(patched.focusCategories).toEqual(["funny"]);
    expect(patched.subtitles).toEqual({ enabled: true, style: "bold", position: "center" });
    expect(patched).not.toHaveProperty("unknownField");
    expect(applySettingsPatch(DEFAULT_ANALYSIS_SETTINGS, null)).toBe(DEFAULT_ANALYSIS_SETTINGS);
  });

  it("restores only what one change touched (clip lengths always together)", () => {
    const before: AnalysisSettings = { ...DEFAULT_ANALYSIS_SETTINGS, focusCategories: ["hype"] };
    const patch: Partial<AnalysisSettings> = { focusCategories: ["funny"], minClipSeconds: 10, targetClipSeconds: 15, maxClipSeconds: 25 };
    const after = applySettingsPatch(before, patch);

    expect(changePaths("minClipSeconds", patch)).toEqual(["minClipSeconds", "targetClipSeconds", "maxClipSeconds"]);
    expect(changePaths("subtitles.position", patch)).toEqual(["subtitles.position"]);
    // A field name the web does not know falls back to the patch keys no other change claims.
    expect(changePaths("foco", patch, ["minClipSeconds"])).toEqual(["focusCategories"]);

    const undoneLengths = restoreSettingsPaths(after, before, changePaths("targetClipSeconds", patch));
    expect(undoneLengths).toMatchObject({ minClipSeconds: 15, targetClipSeconds: 30, maxClipSeconds: 60, focusCategories: ["funny"] });
    const undoneFocus = restoreSettingsPaths(after, before, changePaths("focusCategories", patch));
    expect(undoneFocus).toMatchObject({ focusCategories: ["hype"], minClipSeconds: 10, maxClipSeconds: 25 });
  });
});

describe("assistant patches of the maximum of moments (auto-render count follows it)", () => {
  const creatorLimits: PlanLimits = { ...freeLimits, maxCandidatesPerJob: 30, maxAutoRenderPerJob: 30 };
  /** The plan renders fewer clips than it finds moments: "all" means "up to 10" there. */
  const cappedLimits: PlanLimits = { ...freeLimits, maxCandidatesPerJob: 30, maxAutoRenderPerJob: 10 };
  const start = applyPlanLimits({ ...DEFAULT_ANALYSIS_SETTINGS }, creatorLimits);
  /** What the assistant panel does with a reply: patch with the plan in mind, then the plan caps. */
  const reply = (settings: AnalysisSettings, patch: Partial<AnalysisSettings>, limits: PlanLimits | null = creatorLimits) =>
    applyPlanLimits(applySettingsPatch(settings, patch, limits), limits);

  it("keeps “Todos os momentos encontrados” when a reply raises the maximum", () => {
    expect(start).toMatchObject({ maxCandidates: 20, autoRenderTop: 20 });
    expect(autoRenderMode(start, creatorLimits)).toBe("all");

    const raised = reply(start, { maxCandidates: 30 });
    expect(raised).toMatchObject({ maxCandidates: 30, autoRenderTop: 30 });
    expect(autoRenderMode(raised, creatorLimits)).toBe("all");
    // Above the plan: both capped, still "all".
    expect(reply(start, { maxCandidates: 45 })).toMatchObject({ maxCandidates: 30, autoRenderTop: 30 });
  });

  it("keeps “Todos” when a reply lowers the maximum, and Desfazer restores both values", () => {
    const patch: Partial<AnalysisSettings> = { maxCandidates: 5 };
    const lowered = reply(start, patch);
    expect(lowered).toMatchObject({ maxCandidates: 5, autoRenderTop: 5 });

    const paths = changePaths("maxCandidates", patch);
    expect(paths).toEqual(["maxCandidates", "autoRenderTop"]);
    const undone = applyPlanLimits(restoreSettingsPaths(lowered, start, paths), creatorLimits);
    expect(undone).toMatchObject({ maxCandidates: 20, autoRenderTop: 20 });
    expect(autoRenderMode(undone, creatorLimits)).toBe("all");

    // Undoing a raise goes back too.
    const raised = reply(start, { maxCandidates: 30 });
    expect(applyPlanLimits(restoreSettingsPaths(raised, start, changePaths("maxCandidates", { maxCandidates: 30 })), creatorLimits)).toMatchObject({
      maxCandidates: 20,
      autoRenderTop: 20,
    });
  });

  it("tells “all” apart with the plan's auto-render cap", () => {
    const capped = applyPlanLimits({ ...DEFAULT_ANALYSIS_SETTINGS }, cappedLimits);
    expect(capped).toMatchObject({ maxCandidates: 20, autoRenderTop: 10 });
    expect(autoRenderMode(capped, cappedLimits)).toBe("all");
    expect(reply(capped, { maxCandidates: 30 }, cappedLimits)).toMatchObject({ maxCandidates: 30, autoRenderTop: 10 });
    expect(reply(capped, { maxCandidates: 6 }, cappedLimits)).toMatchObject({ maxCandidates: 6, autoRenderTop: 6 });
  });

  it("leaves a fixed number of clips (only capped) and “none” alone, and honours an explicit count", () => {
    const top = { ...start, autoRenderTop: 5 };
    expect(autoRenderMode(top, creatorLimits)).toBe("top");
    expect(reply(top, { maxCandidates: 30 })).toMatchObject({ maxCandidates: 30, autoRenderTop: 5 });
    expect(reply(top, { maxCandidates: 3 })).toMatchObject({ maxCandidates: 3, autoRenderTop: 3 });

    const none = { ...start, autoRenderTop: 0 };
    expect(autoRenderMode(none, creatorLimits)).toBe("none");
    expect(reply(none, { maxCandidates: 30 })).toMatchObject({ maxCandidates: 30, autoRenderTop: 0 });

    expect(reply(start, { maxCandidates: 30, autoRenderTop: 8 })).toMatchObject({ maxCandidates: 30, autoRenderTop: 8 });
    expect(changePaths("autoRenderTop", { autoRenderTop: 8 })).toEqual(["maxCandidates", "autoRenderTop"]);
  });

  it("restores the pair when a reply's change has a field name the web does not know", () => {
    const patch: Partial<AnalysisSettings> = { maxCandidates: 5, focusCategories: ["funny"] };
    expect(changePaths("quantidade", patch, ["focusCategories"])).toEqual(["maxCandidates", "autoRenderTop"]);
    expect(changePaths("quantidade", { focusCategories: ["funny"] }, [])).toEqual(["focusCategories"]);
  });
});

describe("shortcuts (atalhos, no AI)", () => {
  it("toggle focus categories", () => {
    const funny = preset("funny");
    const on = funny.toggle(DEFAULT_ANALYSIS_SETTINGS);
    expect(on.focusCategories).toEqual(["funny"]);
    expect(funny.isActive(on)).toBe(true);
    expect(funny.toggle(on).focusCategories).toEqual([]);
    expect(preset("reaction-hype").toggle(on).focusCategories).toEqual(["reaction", "funny", "hype"]);
    expect(preset("quote").toggle(DEFAULT_ANALYSIS_SETTINGS).focusCategories).toEqual(["quote"]);
    expect(preset("gameplay").toggle(DEFAULT_ANALYSIS_SETTINGS).focusCategories).toEqual(["gameplay"]);
  });

  it("set the clip-length ranges of their labels", () => {
    expect(preset("short").label).toBe("Clipes curtos (15–30 s)");
    expect(preset("short").toggle(DEFAULT_ANALYSIS_SETTINGS)).toMatchObject({ minClipSeconds: 15, targetClipSeconds: 20, maxClipSeconds: 30 });
    expect(preset("long").label).toBe("Clipes longos (45–90 s)");
    expect(preset("long").toggle(DEFAULT_ANALYSIS_SETTINGS)).toMatchObject({ minClipSeconds: 45, targetClipSeconds: 60, maxClipSeconds: 90 });
    expect(SETTINGS_PRESETS.map((item) => item.label)).toEqual([
      "Mais engraçados",
      "Mais reações e hype",
      "Frases marcantes",
      "Jogadas",
      "Clipes curtos (15–30 s)",
      "Clipes longos (45–90 s)",
    ]);
  });
});
