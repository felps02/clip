/**
 * Formatting helpers. All human-readable output is pt-BR.
 */
export const LOCALE = "pt-BR";

/**
 * Time zone of every absolute date and time. Explicit on both sides so the server render and the
 * browser produce the same text (no hydration mismatch); the app switches it to the profile's
 * time zone once the session is known (`setDisplayTimeZone`).
 */
export const DEFAULT_TIME_ZONE = "America/Sao_Paulo";
let displayTimeZone = DEFAULT_TIME_ZONE;

export function isValidTimeZone(value: string | null | undefined): value is string {
  if (!value) return false;
  try {
    new Intl.DateTimeFormat(LOCALE, { timeZone: value });
    return true;
  } catch {
    return false;
  }
}

export function getDisplayTimeZone(): string {
  return displayTimeZone;
}

/** Profile time zone (IANA name); anything invalid or empty restores the default. */
export function setDisplayTimeZone(value: string | null | undefined): void {
  displayTimeZone = isValidTimeZone(value) ? value : DEFAULT_TIME_ZONE;
}

const pad2 = (n: number) => String(n).padStart(2, "0");

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}

/** `mm:ss` (or `h:mm:ss` from one hour on). Null/NaN renders as `--:--`. */
export function formatDuration(seconds: number | null | undefined): string {
  if (!isFiniteNumber(seconds)) return "--:--";
  const total = Math.max(0, Math.round(seconds));
  const h = Math.floor(total / 3600);
  const m = Math.floor((total % 3600) / 60);
  const s = total % 60;
  return h > 0 ? `${h}:${pad2(m)}:${pad2(s)}` : `${pad2(m)}:${pad2(s)}`;
}

/** `mm:ss.s` with tenths of a second (editor precision). */
export function formatTimecode(seconds: number | null | undefined): string {
  if (!isFiniteNumber(seconds)) return "--:--.-";
  const clamped = Math.max(0, seconds);
  const tenths = Math.round(clamped * 10);
  const totalSeconds = Math.floor(tenths / 10);
  const fraction = tenths % 10;
  const h = Math.floor(totalSeconds / 3600);
  const m = Math.floor((totalSeconds % 3600) / 60);
  const s = totalSeconds % 60;
  const base = h > 0 ? `${h}:${pad2(m)}:${pad2(s)}` : `${pad2(m)}:${pad2(s)}`;
  return `${base}.${fraction}`;
}

/**
 * Parse `ss`, `mm:ss`, `mm:ss.s`, `h:mm:ss.s` (comma or dot decimals) into seconds.
 * Returns null for invalid input.
 */
export function parseTimecode(input: string): number | null {
  const trimmed = input.trim().replace(",", ".");
  if (!trimmed) return null;
  if (!/^\d+(:\d{1,2}){0,2}(\.\d+)?$/.test(trimmed)) return null;
  const parts = trimmed.split(":");
  let seconds = 0;
  for (const part of parts) {
    const value = Number(part);
    if (!Number.isFinite(value)) return null;
    seconds = seconds * 60 + value;
  }
  return seconds;
}

const BYTE_UNITS = ["B", "KB", "MB", "GB", "TB"];

export function formatBytes(bytes: number | null | undefined, decimals = 1): string {
  if (!isFiniteNumber(bytes) || bytes < 0) return "—";
  if (bytes === 0) return "0 B";
  const exponent = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), BYTE_UNITS.length - 1);
  const value = bytes / 1024 ** exponent;
  const digits = exponent === 0 ? 0 : decimals;
  return `${formatNumber(value, { maximumFractionDigits: digits, minimumFractionDigits: 0 })} ${BYTE_UNITS[exponent]}`;
}

export function formatSpeed(bytesPerSecond: number | null | undefined): string {
  if (!isFiniteNumber(bytesPerSecond) || bytesPerSecond <= 0) return "—";
  return `${formatBytes(bytesPerSecond)}/s`;
}

export function formatNumber(value: number, options?: Intl.NumberFormatOptions): string {
  return new Intl.NumberFormat(LOCALE, options).format(value);
}

export function formatPercent(fraction: number, maximumFractionDigits = 0): string {
  if (!isFiniteNumber(fraction)) return "—";
  return new Intl.NumberFormat(LOCALE, { style: "percent", maximumFractionDigits }).format(fraction);
}

/** Seconds with at most one decimal: `12,7 s`, `15 s`. */
export function formatSeconds(seconds: number | null | undefined): string {
  if (!isFiniteNumber(seconds)) return "—";
  return `${formatNumber(Math.max(0, seconds), { maximumFractionDigits: 1 })} s`;
}

/** Minutes from seconds, e.g. `12 min` or `1 h 05 min`. */
export function formatMinutes(seconds: number | null | undefined): string {
  if (!isFiniteNumber(seconds)) return "—";
  const totalMinutes = Math.max(0, Math.round(seconds / 60));
  const h = Math.floor(totalMinutes / 60);
  const m = totalMinutes % 60;
  if (h > 0) return `${h} h ${pad2(m)} min`;
  return `${m} min`;
}

export function formatCurrency(cents: number | null | undefined, currency = "BRL"): string {
  if (!isFiniteNumber(cents)) return "—";
  return new Intl.NumberFormat(LOCALE, { style: "currency", currency }).format(cents / 100);
}

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === "") return null;
  const date = value instanceof Date ? value : new Date(value);
  return Number.isNaN(date.getTime()) ? null : date;
}

export function formatDate(value: string | number | Date | null | undefined): string {
  const date = toDate(value);
  if (!date) return "—";
  return new Intl.DateTimeFormat(LOCALE, { day: "2-digit", month: "2-digit", year: "numeric", timeZone: displayTimeZone }).format(date);
}

export function formatDateTime(value: string | number | Date | null | undefined): string {
  const date = toDate(value);
  if (!date) return "—";
  return new Intl.DateTimeFormat(LOCALE, {
    day: "2-digit",
    month: "2-digit",
    year: "numeric",
    hour: "2-digit",
    minute: "2-digit",
    timeZone: displayTimeZone,
  }).format(date);
}

export function formatTime(value: string | number | Date | null | undefined): string {
  const date = toDate(value);
  if (!date) return "—";
  return new Intl.DateTimeFormat(LOCALE, { hour: "2-digit", minute: "2-digit", second: "2-digit", timeZone: displayTimeZone }).format(date);
}

const RELATIVE_STEPS: Array<[Intl.RelativeTimeFormatUnit, number]> = [
  ["year", 60 * 60 * 24 * 365],
  ["month", 60 * 60 * 24 * 30],
  ["week", 60 * 60 * 24 * 7],
  ["day", 60 * 60 * 24],
  ["hour", 60 * 60],
  ["minute", 60],
];

/** "há 5 minutos", "em 2 dias", "agora". */
export function formatRelativeTime(
  value: string | number | Date | null | undefined,
  now: number = Date.now(),
): string {
  const date = toDate(value);
  if (!date) return "—";
  const diffSeconds = Math.round((date.getTime() - now) / 1000);
  if (Math.abs(diffSeconds) < 45) return "agora";
  const rtf = new Intl.RelativeTimeFormat(LOCALE, { numeric: "auto" });
  for (const [unit, size] of RELATIVE_STEPS) {
    if (Math.abs(diffSeconds) >= size) {
      return rtf.format(Math.round(diffSeconds / size), unit);
    }
  }
  return rtf.format(diffSeconds, "second");
}

/**
 * Time since a past moment, for statements such as "último contato há 35 segundos": counted in
 * seconds under a minute and never "agora" (next to "Sem sinal do companion", "agora" would contradict
 * the state), then the wording of `formatRelativeTime`. A moment that is not in the past by the
 * browser's clock (clock skew with the server) counts as one second.
 */
export function formatElapsed(value: string | number | Date | null | undefined, now: number = Date.now()): string {
  const date = toDate(value);
  if (!date) return "—";
  const seconds = Math.floor((now - date.getTime()) / 1000);
  if (seconds < 60) {
    return new Intl.RelativeTimeFormat(LOCALE, { numeric: "always" }).format(-Math.max(1, seconds), "second");
  }
  return formatRelativeTime(date, now);
}

/** Seconds → "~2 min", "~45 s". */
export function formatEta(seconds: number | null | undefined): string {
  if (!isFiniteNumber(seconds) || seconds < 0) return "—";
  if (seconds < 60) return `~${Math.max(1, Math.round(seconds))} s`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `~${minutes} min`;
  return `~${Math.round(minutes / 60)} h`;
}

export function pluralize(count: number, singular: string, plural: string): string {
  return `${formatNumber(count)} ${count === 1 ? singular : plural}`;
}

export function truncate(text: string, max = 140): string {
  if (text.length <= max) return text;
  return `${text.slice(0, max - 1).trimEnd()}…`;
}

/**
 * Calendar date for display. A date-only `YYYY-MM-DD` value is rendered as that day (never
 * shifted by the local timezone, unlike `new Date("YYYY-MM-DD")`); anything else goes through `formatDate`.
 */
export function formatCalendarDate(value: string | null | undefined): string {
  if (!value) return "—";
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value.trim());
  if (match) return `${match[3]}/${match[2]}/${match[1]}`;
  return formatDate(value);
}
