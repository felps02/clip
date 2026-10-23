"use client";

import { useCallback, useSyncExternalStore } from "react";

const CHANGE_EVENT = "cf:session-storage";

function subscribe(callback: () => void) {
  window.addEventListener("storage", callback);
  window.addEventListener(CHANGE_EVENT, callback);
  return () => {
    window.removeEventListener("storage", callback);
    window.removeEventListener(CHANGE_EVENT, callback);
  };
}

export function readSessionStorage(key: string): string | null {
  try {
    return window.sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeSessionStorage(key: string, value: string | null): void {
  try {
    if (value === null) window.sessionStorage.removeItem(key);
    else window.sessionStorage.setItem(key, value);
  } catch {
    // storage may be unavailable (private mode); the UI degrades to the neutral path
  }
  window.dispatchEvent(new Event(CHANGE_EVENT));
}

/** Hydration-safe read of a sessionStorage value (null on the server and during hydration). */
export function useSessionStorageValue(key: string): string | null {
  const getSnapshot = useCallback(() => readSessionStorage(key), [key]);
  return useSyncExternalStore(subscribe, getSnapshot, () => null);
}
