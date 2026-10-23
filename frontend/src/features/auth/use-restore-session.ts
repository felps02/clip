"use client";

import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { consumeSessionHandoff, handOffToApp } from "@/features/auth/session-handoff";
import { refreshSession } from "@/lib/api/client";

/**
 * - `checking`: probing the API for a session;
 * - `restored`: a session exists and the hand-off to `next` is under way;
 * - `guest`: no session (or the probe failed/timed out): show the form;
 * - `blocked`: the session exists for the API but the web host cannot see its cookie, so the
 *   proxy would send the hand-off straight back here; show the explanation, never redirect.
 */
export type RestoreSessionState = "checking" | "restored" | "guest" | "blocked";

/** Upper bound for the probe so a hanging API never keeps the form locked. */
export const RESTORE_SESSION_TIMEOUT_MS = 8_000;

export interface RestoreSessionOptions {
  /** Skip the probe entirely, e.g. right after an explicit logout cleared the cookies. */
  skip?: boolean;
}

/**
 * Guest pages are not redirected by the proxy (the presence of the `cf_access` cookie proves
 * nothing: it expires with the 15-minute JWT and may outlive a revoked session), so they ask the
 * API once whether a session can be restored. `POST /api/auth/refresh` succeeds while the 30-day
 * refresh cookie is valid, even when the access cookie is already gone; the user is then sent to
 * `next` instead of typing credentials again — but only if the web host would accept that
 * navigation (`handOffToApp`), never on the strength of client state alone.
 */
export function useRestoreSession(next: string, options: RestoreSessionOptions = {}): RestoreSessionState {
  const router = useRouter();
  const skip = options.skip ?? false;
  const [state, setState] = useState<RestoreSessionState>(skip ? "guest" : "checking");
  // Decided once per mount: the marker is consumed on read, so a re-run of the effect (React
  // strict mode) must not start the probe it was meant to prevent.
  const bounced = useRef<boolean | null>(null);

  useEffect(() => {
    if (skip) return;
    bounced.current ??= consumeSessionHandoff();
    if (bounced.current) {
      setState("blocked");
      return;
    }
    let cancelled = false;
    let timer: number | undefined;
    const timeout = new Promise<boolean>((resolve) => {
      timer = window.setTimeout(() => resolve(false), RESTORE_SESSION_TIMEOUT_MS);
    });
    void Promise.race([refreshSession(), timeout]).then(async (restored) => {
      window.clearTimeout(timer);
      if (cancelled) return;
      if (!restored) {
        setState("guest");
        return;
      }
      setState("restored");
      const outcome = await handOffToApp(
        next,
        router,
        () => cancelled,
        () => setState("blocked"),
      );
      if (outcome === "blocked") setState("blocked");
    });
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [next, router, skip]);

  return state;
}
