"use client";

import { useSyncExternalStore } from "react";

const subscribe = () => () => undefined;

/**
 * `false` during the server render and the hydration pass, `true` afterwards. Text that depends on
 * the current time or the browser (relative times, the page's host) renders only when this is true,
 * so the server HTML and the first client render always match.
 */
export function useHydrated(): boolean {
  return useSyncExternalStore(
    subscribe,
    () => true,
    () => false,
  );
}
