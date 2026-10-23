import type {
  AnalysisSettings,
  CandidateCategory,
  EditingPreferences,
  FramingMode,
  PlanLimits,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";
import { CANDIDATE_CATEGORIES } from "@/lib/labels";
import { normalizeEditingPreferences } from "@/lib/preferences";
import { clamp } from "@/lib/utils";

/**
 * Clip-length range offered by the forms (new project, re-analysis and the default in
 * Settings → Preferências de edição). It sits inside what the API accepts, so a preference
 * saved here is always usable as a project default.
 */
export const CLIP_LENGTH_MIN = 10;
export const CLIP_LENGTH_MAX = 120;
/** The range slider keeps at least this gap between the minimum and the maximum. */
export const CLIP_LENGTH_MIN_GAP = 5;
/** Shortest clip the API accepts; the form only goes this low for recordings shorter than the clip minimum. */
export const API_CLIP_LENGTH_MIN = 5;

export interface ClipLengthBounds {
  min: number;
  max: number;
  /** Minimum distance between the minimum and the maximum. */
  gap: number;
}

export const DEFAULT_CLIP_LENGTH_BOUNDS: ClipLengthBounds = { min: CLIP_LENGTH_MIN, max: CLIP_LENGTH_MAX, gap: CLIP_LENGTH_MIN_GAP };

/** Up to 20 custom keywords of 1–40 characters (API contract). */
export const CUSTOM_KEYWORDS_MAX = 20;
export const CUSTOM_KEYWORD_MAX_LENGTH = 40;
/** `settings.instructions`: what the person asked in their own words. */
export const INSTRUCTIONS_MAX_LENGTH = 500;

/**
 * Plans without `allowCustomSubtitleStyles` only get this subtitle style, at this position: the
 * API forces both on project creation, re-analysis, clips created from a candidate, clip edits
 * and auto-renders, so the forms never offer anything else on those plans.
 */
export const DEFAULT_SUBTITLE_STYLE: SubtitleStyle = "bold";
export const DEFAULT_SUBTITLE_POSITION: SubtitlePosition = "bottom";

export const DEFAULT_ANALYSIS_SETTINGS: AnalysisSettings = {
  minClipSeconds: 15,
  maxClipSeconds: 60,
  targetClipSeconds: 30,
  sensitivity: 50,
  maxCandidates: 20,
  // "Todos os momentos encontrados": every candidate becomes a clip, up to the plan's maxAutoRenderPerJob.
  autoRenderTop: 20,
  aspectRatio: "9:16",
  subtitles: { enabled: true, style: DEFAULT_SUBTITLE_STYLE, position: DEFAULT_SUBTITLE_POSITION },
  language: "auto",
  framing: { mode: "auto" },
  focusCategories: [],
  customKeywords: [],
  instructions: null,
};

/** Unknown limits (session still loading) never lock an option: the API enforces the plan anyway. */
export function isSubtitleStyleAllowed(style: SubtitleStyle, limits?: PlanLimits | null): boolean {
  return style === DEFAULT_SUBTITLE_STYLE || !limits || limits.allowCustomSubtitleStyles !== false;
}

/** Same plan flag as the styles: without `allowCustomSubtitleStyles` subtitles always sit at the bottom. */
export function isSubtitlePositionAllowed(position: SubtitlePosition, limits?: PlanLimits | null): boolean {
  return position === DEFAULT_SUBTITLE_POSITION || !limits || limits.allowCustomSubtitleStyles !== false;
}

export function isFramingAllowed(mode: FramingMode, limits?: PlanLimits | null): boolean {
  return mode !== "webcam-split" || !limits || limits.allowWebcamLayout !== false;
}

function isPositiveNumber(value: number | null | undefined): value is number {
  return typeof value === "number" && Number.isFinite(value) && value > 0;
}

/**
 * Bounds of the clip-length controls. A recording known to be shorter than the clip minimum
 * (`shortSourceSeconds`, see `isShortSource`) gets a range that fits inside it: from the API
 * minimum (5 s) up to its whole length, without the usual gap.
 */
export function clipLengthBounds(shortSourceSeconds?: number | null): ClipLengthBounds {
  if (!isPositiveNumber(shortSourceSeconds)) return DEFAULT_CLIP_LENGTH_BOUNDS;
  const max = Math.max(API_CLIP_LENGTH_MIN, Math.min(CLIP_LENGTH_MAX, Math.floor(shortSourceSeconds)));
  return { min: API_CLIP_LENGTH_MIN, max, gap: 0 };
}

/** Fits min/target/max into the form range, keeping min + gap ≤ max and min ≤ target ≤ max. */
export function fitClipLengths(
  min: number,
  max: number,
  target: number,
  bounds: ClipLengthBounds = DEFAULT_CLIP_LENGTH_BOUNDS,
): { min: number; max: number; target: number } {
  const safeMin = clamp(Math.round(min), bounds.min, bounds.max - bounds.gap);
  const safeMax = clamp(Math.round(max), safeMin + bounds.gap, bounds.max);
  const safeTarget = clamp(Math.round(target), safeMin, safeMax);
  return { min: safeMin, max: safeMax, target: safeTarget };
}

/** The recording (duration measured in the browser) is shorter than the shortest clip asked for. */
export function isShortSource(durationSeconds: number | null | undefined, minClipSeconds: number): durationSeconds is number {
  return isPositiveNumber(durationSeconds) && durationSeconds < minClipSeconds;
}

/**
 * Clip lengths for a recording shorter than the clip minimum: minimum = max(5, whole seconds of the
 * recording), maximum and target clamped to the recording, so the analysis can still return it.
 */
export function adjustForShortSource(settings: AnalysisSettings, durationSeconds: number): AnalysisSettings {
  const whole = Math.max(0, Math.floor(durationSeconds));
  const min = Math.max(API_CLIP_LENGTH_MIN, whole);
  const max = Math.max(min, Math.min(settings.maxClipSeconds, whole));
  return {
    ...settings,
    minClipSeconds: min,
    maxClipSeconds: max,
    targetClipSeconds: clamp(Math.round(settings.targetClipSeconds), min, max),
  };
}

/** Trimmed, 1–40 characters, case-insensitively unique, at most 20 (the API drops the rest anyway). */
export function normalizeKeywords(keywords: readonly unknown[] | null | undefined): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const raw of keywords ?? []) {
    if (typeof raw !== "string") continue;
    const keyword = raw.trim().replace(/\s+/g, " ");
    if (!keyword || keyword.length > CUSTOM_KEYWORD_MAX_LENGTH) continue;
    const key = keyword.toLocaleLowerCase("pt-BR");
    if (seen.has(key)) continue;
    seen.add(key);
    result.push(keyword);
    if (result.length >= CUSTOM_KEYWORDS_MAX) break;
  }
  return result;
}

/** Known categories only, each once, in the canonical order. */
export function normalizeFocusCategories(categories: readonly unknown[] | null | undefined): CandidateCategory[] {
  const wanted = new Set((categories ?? []).filter((value): value is string => typeof value === "string"));
  return CANDIDATE_CATEGORIES.filter((category) => wanted.has(category));
}

/**
 * Keeps the text as typed (a trailing space must survive while the person types) and only caps the
 * length; `trimSettingsForSubmit` trims it when the settings are sent.
 */
export function normalizeInstructions(value: unknown): string | null {
  if (typeof value !== "string" || value.length === 0) return null;
  return value.slice(0, INSTRUCTIONS_MAX_LENGTH);
}

/** Settings as sent to the API: instructions trimmed (blank → null). */
export function trimSettingsForSubmit(settings: AnalysisSettings): AnalysisSettings {
  const instructions = settings.instructions?.trim();
  return { ...settings, instructions: instructions ? instructions.slice(0, INSTRUCTIONS_MAX_LENGTH) : null };
}

/** Most clips the plan renders automatically per analysis with `maxCandidates` candidates. */
export function autoRenderCap(maxCandidates: number, limits?: PlanLimits | null): number {
  return Math.min(maxCandidates, limits ? limits.maxAutoRenderPerJob : maxCandidates);
}

/**
 * "Clipes prontos automaticamente": `all` ("Todos os momentos encontrados", up to the plan cap),
 * `top` ("Só os melhores", a fixed number) or `none` ("Nenhum: revisar antes").
 */
export type AutoRenderMode = "all" | "top" | "none";

export function autoRenderMode(settings: Pick<AnalysisSettings, "maxCandidates" | "autoRenderTop">, limits?: PlanLimits | null): AutoRenderMode {
  if (settings.autoRenderTop <= 0) return "none";
  return settings.autoRenderTop >= autoRenderCap(settings.maxCandidates, limits) ? "all" : "top";
}

export interface PlanLimitOptions {
  /** Duration of a recording shorter than the clip minimum: the clip-length range then fits inside it. */
  shortSourceSeconds?: number | null;
}

/** Keeps settings inside what the form can show and what the plan allows. */
export function applyPlanLimits(settings: AnalysisSettings, limits?: PlanLimits | null, options: PlanLimitOptions = {}): AnalysisSettings {
  const lengths = fitClipLengths(
    settings.minClipSeconds,
    settings.maxClipSeconds,
    settings.targetClipSeconds,
    clipLengthBounds(options.shortSourceSeconds),
  );
  const maxCandidates = limits ? Math.min(settings.maxCandidates, limits.maxCandidatesPerJob) : settings.maxCandidates;
  const autoRenderTop = Math.max(0, Math.min(settings.autoRenderTop, autoRenderCap(maxCandidates, limits)));
  return {
    ...settings,
    minClipSeconds: lengths.min,
    maxClipSeconds: lengths.max,
    targetClipSeconds: lengths.target,
    maxCandidates,
    autoRenderTop,
    focusCategories: normalizeFocusCategories(settings.focusCategories),
    customKeywords: normalizeKeywords(settings.customKeywords),
    instructions: normalizeInstructions(settings.instructions),
    subtitles: {
      ...settings.subtitles,
      style: isSubtitleStyleAllowed(settings.subtitles.style, limits) ? settings.subtitles.style : DEFAULT_SUBTITLE_STYLE,
      position: isSubtitlePositionAllowed(settings.subtitles.position, limits) ? settings.subtitles.position : DEFAULT_SUBTITLE_POSITION,
    },
    framing: {
      ...settings.framing,
      mode: isFramingAllowed(settings.framing.mode, limits) ? settings.framing.mode : "auto",
    },
  };
}

/** New-project defaults: the profile's editing preferences, capped by the plan limits. */
export function buildDefaultSettings(preferences?: Partial<EditingPreferences> | null, limits?: PlanLimits | null): AnalysisSettings {
  const prefs = normalizeEditingPreferences(preferences);
  return applyPlanLimits(
    {
      ...DEFAULT_ANALYSIS_SETTINGS,
      minClipSeconds: prefs.minClipSeconds,
      maxClipSeconds: prefs.maxClipSeconds,
      targetClipSeconds: prefs.targetClipSeconds,
      aspectRatio: prefs.aspectRatio,
      subtitles: { enabled: prefs.subtitlesEnabled, style: prefs.subtitleStyle, position: prefs.subtitlePosition },
      language: prefs.language,
    },
    limits,
  );
}

/**
 * Defaults for a re-analysis or a clip of an existing project: the project's own settings (the
 * profile preferences only fill a project that has none), capped by the plan limits.
 */
export function settingsForProject(
  projectSettings: Partial<AnalysisSettings> | null | undefined,
  preferences?: Partial<EditingPreferences> | null,
  limits?: PlanLimits | null,
): AnalysisSettings {
  if (!projectSettings) return buildDefaultSettings(preferences, limits);
  const base = DEFAULT_ANALYSIS_SETTINGS;
  return applyPlanLimits(
    {
      ...base,
      ...projectSettings,
      subtitles: { ...base.subtitles, ...projectSettings.subtitles },
      framing: { ...base.framing, ...projectSettings.framing },
      focusCategories: projectSettings.focusCategories ?? base.focusCategories,
      customKeywords: projectSettings.customKeywords ?? base.customKeywords,
      instructions: projectSettings.instructions ?? base.instructions,
    },
    limits,
  );
}

// ----------------------------------------------------------------------------- patches (assistant, presets)

const PATCHABLE_KEYS = new Set<string>([
  "minClipSeconds",
  "maxClipSeconds",
  "targetClipSeconds",
  "sensitivity",
  "maxCandidates",
  "autoRenderTop",
  "aspectRatio",
  "subtitles",
  "language",
  "framing",
  "title",
  "focusCategories",
  "customKeywords",
  "instructions",
]);

const CLIP_LENGTH_FIELDS = ["minClipSeconds", "targetClipSeconds", "maxClipSeconds"];
/** How many moments and how many of them become clips: a new maximum can move the auto-render count. */
const RENDER_COUNT_FIELDS = ["maxCandidates", "autoRenderTop"];

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return !!value && typeof value === "object" && !Array.isArray(value);
}

/**
 * Applies a (server-validated) settings patch: known fields only, nested `subtitles`/`framing`
 * merged field by field. A new `maxCandidates` without an `autoRenderTop` behaves like the form's
 * own field: "Todos os momentos encontrados" follows the new maximum (a fixed number of clips is
 * only capped by it). The plan limits are applied by the caller (`applyPlanLimits`).
 */
export function applySettingsPatch(
  settings: AnalysisSettings,
  patch: Partial<AnalysisSettings> | null | undefined,
  limits?: PlanLimits | null,
): AnalysisSettings {
  if (!isPlainObject(patch)) return settings;
  const next: Record<string, unknown> = { ...settings };
  for (const [key, value] of Object.entries(patch as Record<string, unknown>)) {
    if (value === undefined || !PATCHABLE_KEYS.has(key)) continue;
    const current = next[key];
    next[key] = isPlainObject(value) && isPlainObject(current) ? { ...current, ...value } : value;
  }
  const { maxCandidates, autoRenderTop } = patch as Record<string, unknown>;
  if (
    typeof maxCandidates === "number" &&
    Number.isFinite(maxCandidates) &&
    autoRenderTop === undefined &&
    autoRenderMode(settings, limits) === "all"
  ) {
    next.autoRenderTop = autoRenderCap(maxCandidates, limits);
  }
  return next as unknown as AnalysisSettings;
}

/** Reads `field` (`"minClipSeconds"`, `"subtitles.position"`) from the settings. */
export function getSettingsValue(settings: AnalysisSettings, field: string): unknown {
  let current: unknown = settings;
  for (const part of field.split(".")) {
    if (!isPlainObject(current)) return undefined;
    current = current[part];
  }
  return current;
}

/** Immutable write of `field` (one or two levels deep). */
export function setSettingsValue(settings: AnalysisSettings, field: string, value: unknown): AnalysisSettings {
  const [head, ...rest] = field.split(".");
  const target = settings as unknown as Record<string, unknown>;
  if (rest.length === 0) return { ...target, [head]: value } as unknown as AnalysisSettings;
  const current = target[head];
  const nested = isPlainObject(current) ? current : {};
  return { ...target, [head]: { ...nested, [rest.join(".")]: value } } as unknown as AnalysisSettings;
}

/**
 * Settings paths an assistant change touches, for "Desfazer". The three clip lengths always go
 * together (restoring only one could break min ≤ target ≤ max), and so do `maxCandidates` and
 * `autoRenderTop` (a new maximum moves the auto-render count, see `applySettingsPatch`); a field
 * the web does not know falls back to the patch keys that no other change of the reply claims.
 */
export function changePaths(field: string, patch: Partial<AnalysisSettings> | null | undefined, otherFields: string[] = []): string[] {
  const head = field.split(".")[0];
  if (CLIP_LENGTH_FIELDS.includes(head)) return [...CLIP_LENGTH_FIELDS];
  if (RENDER_COUNT_FIELDS.includes(head)) return [...RENDER_COUNT_FIELDS];
  if (PATCHABLE_KEYS.has(head)) return [field];
  const claimed = new Set(otherFields.map((other) => other.split(".")[0]));
  const keys = Object.keys(isPlainObject(patch) ? patch : {}).filter(
    (key) => PATCHABLE_KEYS.has(key) && !claimed.has(key) && !CLIP_LENGTH_FIELDS.includes(key),
  );
  return keys.some((key) => RENDER_COUNT_FIELDS.includes(key))
    ? [...keys.filter((key) => !RENDER_COUNT_FIELDS.includes(key)), ...RENDER_COUNT_FIELDS]
    : keys;
}

/** Restores `paths` from `before` on top of the current settings. */
export function restoreSettingsPaths(current: AnalysisSettings, before: AnalysisSettings, paths: string[]): AnalysisSettings {
  return paths.reduce((settings, path) => setSettingsValue(settings, path, getSettingsValue(before, path)), current);
}

/** Quick shortcuts on /new and in "Analisar novamente": deterministic setting changes, not AI. */
export interface SettingsPreset {
  id: string;
  label: string;
  /** Pressed state of the toggle. */
  isActive: (settings: AnalysisSettings) => boolean;
  /** Next settings when the shortcut is clicked (focus shortcuts toggle their categories). */
  toggle: (settings: AnalysisSettings) => AnalysisSettings;
}

function focusPreset(id: string, label: string, categories: CandidateCategory[]): SettingsPreset {
  const isActive = (settings: AnalysisSettings) => categories.every((category) => settings.focusCategories.includes(category));
  return {
    id,
    label,
    isActive,
    toggle: (settings) => ({
      ...settings,
      focusCategories: isActive(settings)
        ? settings.focusCategories.filter((category) => !categories.includes(category))
        : normalizeFocusCategories([...settings.focusCategories, ...categories]),
    }),
  };
}

function lengthPreset(id: string, label: string, min: number, target: number, max: number): SettingsPreset {
  return {
    id,
    label,
    isActive: (settings) => settings.minClipSeconds === min && settings.maxClipSeconds === max,
    toggle: (settings) => ({ ...settings, minClipSeconds: min, targetClipSeconds: target, maxClipSeconds: max }),
  };
}

export const SETTINGS_PRESETS: SettingsPreset[] = [
  focusPreset("funny", "Mais engraçados", ["funny"]),
  focusPreset("reaction-hype", "Mais reações e hype", ["reaction", "hype"]),
  focusPreset("quote", "Frases marcantes", ["quote"]),
  focusPreset("gameplay", "Jogadas", ["gameplay"]),
  lengthPreset("short", "Clipes curtos (15–30 s)", 15, 20, 30),
  lengthPreset("long", "Clipes longos (45–90 s)", 45, 60, 90),
];

export function sensitivityHint(value: number): string {
  if (value <= 20) return "Muito seletiva: só os picos mais fortes da gravação viram candidatos.";
  if (value <= 45) return "Seletiva: menos candidatos, com mais confiança.";
  if (value <= 65) return "Equilibrada: bom ponto de partida para a maioria das lives.";
  if (value <= 85) return "Ampla: mais candidatos, inclusive momentos moderados.";
  return "Muito ampla: muitos candidatos; espere mais revisão manual.";
}
