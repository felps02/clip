import "@testing-library/jest-dom/vitest";
import { cleanup } from "@testing-library/react";
import { afterEach } from "vitest";

// jsdom lacks a few layout APIs that Radix primitives call on open (popper sizing, focus management,
// slider thumbs). Assigned directly, not with vi.stubGlobal, so a test's vi.unstubAllGlobals() keeps it.
if (typeof globalThis.ResizeObserver === "undefined") {
  class ResizeObserverStub {
    observe() {}
    unobserve() {}
    disconnect() {}
  }
  globalThis.ResizeObserver = ResizeObserverStub as unknown as typeof ResizeObserver;
}
if (typeof Element !== "undefined") {
  Element.prototype.scrollIntoView ??= () => {};
  Element.prototype.hasPointerCapture ??= () => false;
  Element.prototype.setPointerCapture ??= () => {};
  Element.prototype.releasePointerCapture ??= () => {};
}

afterEach(() => {
  cleanup();
  if (typeof window !== "undefined") window.sessionStorage.clear();
});
