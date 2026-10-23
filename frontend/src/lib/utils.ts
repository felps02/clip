import { clsx, type ClassValue } from "clsx";
import { twMerge } from "tailwind-merge";

/** Merge Tailwind classes without conflicts. */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

export function roundTo(value: number, decimals = 1): number {
  const factor = 10 ** decimals;
  return Math.round(value * factor) / factor;
}

export function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Build a login URL that returns the user to `next` after signing in. */
export function loginUrlFor(next: string | null | undefined): string {
  if (!next || !next.startsWith("/") || next.startsWith("//")) return "/login";
  return `/login?next=${encodeURIComponent(next)}`;
}

/** Placeholder origin used only to parse `next`; a value that resolves anywhere else is rejected. */
const NEXT_PATH_BASE = "http://next.invalid";

/** Backslashes (browsers read them as `/`), ASCII control characters and any whitespace. */
function hasUnsafeUrlCharacter(value: string): boolean {
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index);
    if (code <= 0x1f || code === 0x7f || code === 0x5c /* backslash */) return true;
  }
  return /\s/.test(value);
}

/**
 * Only allow same-origin relative paths as post-login destinations (also what the hand-off probe
 * checks, see `handoffVerdict`).
 *
 * URL parsers treat `\` like `/` and drop tabs and newlines, so `/\evil.example` or
 * `/<tab>/evil.example` would leave the site: anything with a backslash, a control character or
 * whitespace is refused outright. The rest is resolved against a placeholder origin and only its
 * path, query and fragment are kept; that result must still be a single-slash path (dot segments
 * can collapse into `//host`, e.g. `/..//evil.example`) and must not be the login page itself.
 */
export function safeNextPath(next: string | null | undefined, fallback = "/dashboard"): string {
  if (!next || !next.startsWith("/") || next.startsWith("//") || hasUnsafeUrlCharacter(next)) return fallback;
  let url: URL;
  try {
    url = new URL(next, NEXT_PATH_BASE);
  } catch {
    return fallback;
  }
  if (url.origin !== NEXT_PATH_BASE) return fallback;
  const path = `${url.pathname}${url.search}${url.hash}`;
  if (!path.startsWith("/") || path.startsWith("//") || url.pathname.startsWith("/login")) return fallback;
  return path;
}
