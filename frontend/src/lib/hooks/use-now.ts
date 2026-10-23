"use client";

import { useSyncExternalStore } from "react";

/**
 * How often each clock moves: "minute" for wording such as "há 5 minutos" (a 15 s step does not show
 * at that precision), "second" for counters such as "há 35 segundos".
 */
export const CLOCK_TICK_MS = { minute: 15_000, second: 1_000 } as const;
export type ClockPrecision = keyof typeof CLOCK_TICK_MS;

/** A clock read after a pause (nothing was reading it) is refreshed when older than this. */
const STALE_AFTER_PAUSE_MS = 1_000;

interface Clock {
  subscribe: (onTick: () => void) => () => void;
  getSnapshot: () => number;
}

/**
 * One timer per precision, shared by every component that reads it and running only while one of
 * them is mounted. A tick re-renders only those components, so "há 5 minutos" keeps up with the time
 * even when the data on screen (and the query that holds it) stays the same.
 */
function createClock(tickMs: number): Clock {
  const listeners = new Set<() => void>();
  let timer: ReturnType<typeof setInterval> | undefined;
  // Set by the first read in the browser (the server render never reads the clock).
  let now = 0;

  const tick = () => {
    now = Date.now();
    for (const listener of listeners) listener();
  };
  // While stopped, the clock keeps the time of its last tick.
  const catchUp = () => {
    if (Math.abs(Date.now() - now) >= STALE_AFTER_PAUSE_MS) now = Date.now();
  };
  // Browsers throttle the timers of a background tab: back on screen, the time is current at once.
  const onVisibilityChange = () => {
    if (document.visibilityState === "visible") tick();
  };

  return {
    subscribe(onTick) {
      listeners.add(onTick);
      if (timer === undefined) {
        // A hydrating component subscribes before it reads the clock for the first time.
        catchUp();
        timer = setInterval(tick, tickMs);
        document.addEventListener("visibilitychange", onVisibilityChange);
      }
      return () => {
        listeners.delete(onTick);
        if (listeners.size === 0 && timer !== undefined) {
          clearInterval(timer);
          timer = undefined;
          document.removeEventListener("visibilitychange", onVisibilityChange);
        }
      };
    },
    getSnapshot() {
      // While stopped, the first reader gets the current time instead of the last tick's. Between
      // two ticks the value never changes, as useSyncExternalStore requires.
      if (timer === undefined) catchUp();
      return now;
    },
  };
}

const clocks: Record<ClockPrecision, Clock> = {
  minute: createClock(CLOCK_TICK_MS.minute),
  second: createClock(CLOCK_TICK_MS.second),
};

const getServerSnapshot = (): number | null => null;

/**
 * The current time (ms since the epoch), moving every 15 s (`"minute"`) or every second (`"second"`)
 * while the component is mounted. `null` during the server render and the hydration pass: text that
 * depends on the current time renders only afterwards, so the server HTML and the first client render
 * always match.
 */
export function useNow(precision: ClockPrecision = "minute"): number | null {
  const clock = clocks[precision];
  return useSyncExternalStore<number | null>(clock.subscribe, clock.getSnapshot, getServerSnapshot);
}
