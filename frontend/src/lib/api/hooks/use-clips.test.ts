import { describe, expect, it } from "vitest";
import { clipListNeedsPolling, PENDING_POLL_WINDOW_MS } from "@/lib/api/hooks/use-clips";
import type { ClipSummary } from "@/lib/api/types";

const now = Date.parse("2026-10-23T12:00:00Z");

function clip(status: ClipSummary["status"], updatedAt: string): ClipSummary {
  return {
    id: `c-${status}-${updatedAt}`,
    projectId: "p1",
    title: "Clipe",
    status,
    aspectRatio: "9:16",
    durationSeconds: 30,
    startSeconds: 0,
    endSeconds: 30,
    createdAt: updatedAt,
    updatedAt,
  };
}

describe("clipListNeedsPolling", () => {
  it("polls while a clip is rendering", () => {
    expect(clipListNeedsPolling([clip("rendering", "2026-10-01T00:00:00Z")], now)).toBe(true);
  });

  it("polls for a recently queued clip but stops for one stuck in pending", () => {
    const recent = new Date(now - 60_000).toISOString();
    const stuck = new Date(now - PENDING_POLL_WINDOW_MS - 1).toISOString();
    expect(clipListNeedsPolling([clip("pending", recent)], now)).toBe(true);
    expect(clipListNeedsPolling([clip("pending", stuck)], now)).toBe(false);
    expect(clipListNeedsPolling([clip("ready", recent), clip("failed", recent)], now)).toBe(false);
  });
});
