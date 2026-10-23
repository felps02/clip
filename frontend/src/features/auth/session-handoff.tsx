"use client";

import { Alert } from "@/components/ui/alert";
import { HANDOFF_PATH, isCookieGuardEnabled, type HandoffVerdict } from "@/lib/auth-guard";
import { readSessionStorage, writeSessionStorage } from "@/lib/hooks/use-session-storage";

/**
 * Hand-off from the auth pages (/login, /register) to the app, without redirect loops.
 *
 * The proxy (`src/proxy.ts`) sends app routes without the `cf_access` cookie to `/login`. When the
 * API cookie never reaches the web host (API on another host without `Auth__CookieDomain`), a
 * session restored with `POST /api/auth/refresh` is real for the API but invisible to the proxy:
 * navigating to the app would bounce straight back. So before navigating the auth pages ask the
 * web host itself (`GET /auth/handoff`, same origin) whether the proxy would let them through,
 * and show an explanation instead of navigating when it would not.
 *
 * Second line of defence, for when the probe cannot answer: a short-lived marker is recorded
 * before every hand-off and cleared by the app shell once `/api/auth/me` answers. An auth page
 * that mounts while a recent marker is still there knows the hand-off bounced and stops.
 */
export const SESSION_HANDOFF_KEY = "cf.sessionHandoff";
/** A marker older than this is stale (the session may simply have ended later for another reason). */
export const SESSION_HANDOFF_WINDOW_MS = 30_000;

export function markSessionHandoff(next: string, now: number = Date.now()): void {
  writeSessionStorage(SESSION_HANDOFF_KEY, JSON.stringify({ next, at: now }));
}

export function clearSessionHandoff(): void {
  if (readSessionStorage(SESSION_HANDOFF_KEY) !== null) writeSessionStorage(SESSION_HANDOFF_KEY, null);
}

/** Reads and clears the marker; `true` only when a recent hand-off never reached the app. */
export function consumeSessionHandoff(now: number = Date.now()): boolean {
  const raw = readSessionStorage(SESSION_HANDOFF_KEY);
  if (raw === null) return false;
  writeSessionStorage(SESSION_HANDOFF_KEY, null);
  try {
    const parsed = JSON.parse(raw) as { at?: unknown };
    if (typeof parsed.at !== "number") return false;
    const age = now - parsed.at;
    return age >= 0 && age <= SESSION_HANDOFF_WINDOW_MS;
  } catch {
    return false;
  }
}

export type HandoffCheck = "allowed" | "blocked" | "unknown";

/**
 * Asks the web host whether the proxy guard would accept a navigation to `next` right now.
 * Always "allowed" when the cookie guard is disabled (`NEXT_PUBLIC_COOKIE_GUARD=false`).
 */
export async function checkAppHandoff(next: string): Promise<HandoffCheck> {
  if (!isCookieGuardEnabled()) return "allowed";
  try {
    const response = await fetch(`${HANDOFF_PATH}?${new URLSearchParams({ next }).toString()}`, {
      method: "GET",
      cache: "no-store",
      credentials: "same-origin",
      headers: { Accept: "application/json" },
    });
    if (!response.ok) return "unknown";
    const verdict = (await response.json()) as Partial<HandoffVerdict> | null;
    if (verdict?.allowed === true) return "allowed";
    if (verdict?.allowed === false) return "blocked";
    return "unknown";
  } catch {
    return "unknown";
  }
}

/**
 * When the probe could not answer, the navigation still happens; if the proxy bounces it, the
 * router may keep the auth page mounted (same URL) instead of remounting it. After this long
 * on the same page the hand-off is reported as blocked instead of "redirecting" forever.
 */
export const HANDOFF_WATCHDOG_MS = 10_000;

/**
 * Navigates to `next` unless the proxy would bounce the navigation back to the auth page.
 * Returns "blocked" (and does not navigate) in that case, "cancelled" when `isCancelled` turned
 * true while the probe was running (the page went away). `onStuck` runs if the page is still
 * there `HANDOFF_WATCHDOG_MS` after navigating.
 */
export async function handOffToApp(
  next: string,
  router: { replace: (href: string) => void },
  isCancelled: () => boolean = () => false,
  onStuck?: () => void,
): Promise<"navigating" | "blocked" | "cancelled"> {
  const check = await checkAppHandoff(next);
  if (isCancelled()) return "cancelled";
  if (check === "blocked") return "blocked";
  markSessionHandoff(next);
  router.replace(next);
  if (onStuck) {
    window.setTimeout(() => {
      if (!isCancelled()) onStuck();
    }, HANDOFF_WATCHDOG_MS);
  }
  return "navigating";
}

/** Shown on the auth pages when the session exists for the API but the web host cannot see it. */
export function SessionHandoffAlert({ className }: { className?: string }) {
  return (
    <Alert variant="danger" className={className} title="Não foi possível entrar no aplicativo">
      O servidor confirmou a sua sessão, mas este site não recebe o cookie de acesso e não consegue abrir o aplicativo. Isso
      acontece quando a API e o site estão em endereços diferentes sem um domínio de cookie em comum: quem administra a instalação
      precisa configurar Auth__CookieDomain na API (ou desativar a verificação por cookie com NEXT_PUBLIC_COOKIE_GUARD=false). Entrar
      de novo não resolve até que isso seja corrigido.
    </Alert>
  );
}
