import { safeNextPath } from "@/lib/utils";

/**
 * Server-side auth guard rules, shared by `src/proxy.ts` and the hand-off probe
 * (`src/app/auth/handoff/route.ts`).
 *
 * The guard is optimistic: an app route requested without the API's HttpOnly `cf_access` cookie
 * goes to `/login?next=…`. That only works when the browser sends the API cookie to the web host
 * too — same host, or the API configured with `Auth__CookieDomain` covering both hosts. For
 * deployments where that is not possible, `NEXT_PUBLIC_COOKIE_GUARD=false` turns the proxy
 * redirect off and the client-side guard in `AppShell` (`GET /api/auth/me`, sign-out on a final
 * 401) handles authentication alone.
 *
 * The presence of the cookie proves nothing (it can outlive a revoked session); the API is the
 * only real authority.
 */
export const ACCESS_COOKIE = "cf_access";
export const APP_PREFIXES = ["/dashboard", "/new", "/projects", "/library", "/clips", "/live", "/billing", "/settings"] as const;
/** Same-origin endpoint the auth pages ask before sending someone into the app. */
export const HANDOFF_PATH = "/auth/handoff";

/**
 * `NEXT_PUBLIC_COOKIE_GUARD` (inlined at build time): anything but `false`/`0`/`off`/`no`
 * keeps the cookie guard on, which is the default.
 */
export function isCookieGuardEnabled(raw: string | undefined = process.env.NEXT_PUBLIC_COOKIE_GUARD): boolean {
  if (raw === undefined) return true;
  const value = raw.trim().toLowerCase();
  return !(value === "false" || value === "0" || value === "off" || value === "no");
}

export function isAppRoute(pathname: string): boolean {
  return APP_PREFIXES.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));
}

/** Login URL (with `next`) for an app route requested without the access cookie, else `null`. */
export function resolveAuthRedirect(
  pathname: string,
  search: string,
  hasAccessCookie: boolean,
  guardEnabled: boolean = isCookieGuardEnabled(),
): string | null {
  if (!guardEnabled || !isAppRoute(pathname) || hasAccessCookie) return null;
  const params = new URLSearchParams({ next: `${pathname}${search}` });
  return `/login?${params.toString()}`;
}

export interface HandoffVerdict {
  /** Whether the proxy guard is active in this build. */
  guard: boolean;
  /** `false` when the proxy would send a navigation to `next` straight back to `/login`. */
  allowed: boolean;
}

/** Would a navigation to `next`, carrying the cookies of this request, get through the proxy? */
export function handoffVerdict(
  next: string | null | undefined,
  hasAccessCookie: boolean,
  guardEnabled: boolean = isCookieGuardEnabled(),
): HandoffVerdict {
  if (!guardEnabled) return { guard: false, allowed: true };
  // The same sanitising as the auth pages (`safeNextPath`), so the verdict is about the exact path
  // they would navigate to: an off-site or malformed `next` is judged as the /dashboard fallback.
  const target = new URL(safeNextPath(next), "http://guard.invalid");
  return { guard: true, allowed: resolveAuthRedirect(target.pathname, target.search, hasAccessCookie, true) === null };
}
