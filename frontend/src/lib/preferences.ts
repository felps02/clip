import type {
  AspectRatio,
  AudioLanguage,
  EditingPreferences,
  NotificationPreferences,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";
import { clamp } from "@/lib/utils";

/**
 * Profile preference contract shared with the API (docs/API.md → Account, docs/DATA_MODEL.md →
 * user_profiles). The API validates the same rules and answers 400 `validation_failed`.
 */
export const PREFERENCE_CLIP_SECONDS_MIN = 5;
export const PREFERENCE_CLIP_SECONDS_MAX = 300;

/** The interface only exists in pt-BR; every profile update sends this locale. */
export const INTERFACE_LOCALE = "pt-BR";

export const ASPECT_RATIO_VALUES: readonly AspectRatio[] = ["9:16", "16:9", "1:1"];
export const SUBTITLE_STYLE_VALUES: readonly SubtitleStyle[] = ["bold", "clean", "boxed"];
export const SUBTITLE_POSITION_VALUES: readonly SubtitlePosition[] = ["bottom", "center", "top"];
export const AUDIO_LANGUAGE_VALUES: readonly AudioLanguage[] = ["auto", "pt", "en", "es"];

export const DEFAULT_EDITING_PREFERENCES: Readonly<EditingPreferences> = Object.freeze({
  aspectRatio: "9:16",
  subtitlesEnabled: true,
  subtitleStyle: "bold",
  subtitlePosition: "bottom",
  minClipSeconds: 15,
  maxClipSeconds: 60,
  targetClipSeconds: 30,
  language: "auto",
});

export const DEFAULT_NOTIFICATION_PREFERENCES: Readonly<NotificationPreferences> = Object.freeze({
  emailOnJobCompleted: false,
  emailOnJobFailed: true,
  emailOnQuotaWarning: true,
  emailOnBilling: true,
});

export const NOTIFICATION_PREFERENCE_KEYS = Object.keys(DEFAULT_NOTIFICATION_PREFERENCES) as Array<keyof NotificationPreferences>;

function oneOf<T extends string>(value: unknown, allowed: readonly T[], fallback: T): T {
  return typeof value === "string" && (allowed as readonly string[]).includes(value) ? (value as T) : fallback;
}

function integer(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? Math.round(value) : null;
}

/**
 * Fills fields missing from a stored document (older rows, other clients) with the contract
 * defaults and restores the invariants min ≤ target ≤ max inside 5–300 s.
 */
export function normalizeEditingPreferences(raw?: Partial<EditingPreferences> | null): EditingPreferences {
  const defaults = DEFAULT_EDITING_PREFERENCES;
  const min = clamp(integer(raw?.minClipSeconds) ?? defaults.minClipSeconds, PREFERENCE_CLIP_SECONDS_MIN, PREFERENCE_CLIP_SECONDS_MAX);
  const max = clamp(integer(raw?.maxClipSeconds) ?? Math.max(defaults.maxClipSeconds, min), min, PREFERENCE_CLIP_SECONDS_MAX);
  const target = clamp(integer(raw?.targetClipSeconds) ?? defaults.targetClipSeconds, min, max);
  return {
    aspectRatio: oneOf(raw?.aspectRatio, ASPECT_RATIO_VALUES, defaults.aspectRatio),
    subtitlesEnabled: typeof raw?.subtitlesEnabled === "boolean" ? raw.subtitlesEnabled : defaults.subtitlesEnabled,
    subtitleStyle: oneOf(raw?.subtitleStyle, SUBTITLE_STYLE_VALUES, defaults.subtitleStyle),
    subtitlePosition: oneOf(raw?.subtitlePosition, SUBTITLE_POSITION_VALUES, defaults.subtitlePosition),
    minClipSeconds: min,
    maxClipSeconds: max,
    targetClipSeconds: target,
    language: oneOf(raw?.language, AUDIO_LANGUAGE_VALUES, defaults.language),
  };
}

export function normalizeNotificationPreferences(raw?: Partial<NotificationPreferences> | null): NotificationPreferences {
  const result = { ...DEFAULT_NOTIFICATION_PREFERENCES };
  for (const key of NOTIFICATION_PREFERENCE_KEYS) {
    const value = raw?.[key];
    if (typeof value === "boolean") result[key] = value;
  }
  return result;
}

export type EditingPreferenceErrors = Partial<Record<keyof EditingPreferences, string>>;

function isInteger(value: number): boolean {
  return Number.isInteger(value);
}

/** Client-side mirror of the API rules; an empty object means the document is valid. */
export function validateEditingPreferences(prefs: EditingPreferences): EditingPreferenceErrors {
  const errors: EditingPreferenceErrors = {};
  if (!ASPECT_RATIO_VALUES.includes(prefs.aspectRatio)) errors.aspectRatio = "Escolha um formato válido.";
  if (!SUBTITLE_STYLE_VALUES.includes(prefs.subtitleStyle)) errors.subtitleStyle = "Escolha um estilo de legenda válido.";
  if (!SUBTITLE_POSITION_VALUES.includes(prefs.subtitlePosition)) errors.subtitlePosition = "Escolha uma posição válida.";
  if (!AUDIO_LANGUAGE_VALUES.includes(prefs.language)) errors.language = "Escolha um idioma válido.";
  const { minClipSeconds: min, maxClipSeconds: max, targetClipSeconds: target } = prefs;
  if (!isInteger(min) || min < PREFERENCE_CLIP_SECONDS_MIN || min > PREFERENCE_CLIP_SECONDS_MAX) {
    errors.minClipSeconds = `A duração mínima deve ser um número inteiro entre ${PREFERENCE_CLIP_SECONDS_MIN} e ${PREFERENCE_CLIP_SECONDS_MAX} segundos.`;
  }
  if (!isInteger(max) || max < min || max > PREFERENCE_CLIP_SECONDS_MAX) {
    errors.maxClipSeconds = `A duração máxima deve ser um número inteiro entre a mínima e ${PREFERENCE_CLIP_SECONDS_MAX} segundos.`;
  }
  if (!isInteger(target) || target < min || target > max) {
    errors.targetClipSeconds = "A duração alvo deve ficar entre a duração mínima e a máxima.";
  }
  return errors;
}
