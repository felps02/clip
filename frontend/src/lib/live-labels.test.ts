import { describe, expect, it } from "vitest";
import type { LiveSegmentStatus, LiveSessionStatus } from "@/lib/api/types";
import {
  isActiveLiveSessionStatus,
  LIVE_SEGMENT_STATUS_LABELS,
  LIVE_SEGMENT_STATUS_TONES,
  LIVE_SEGMENT_STATUSES,
  LIVE_SESSION_STATUS_LABELS,
  LIVE_SESSION_STATUS_TONES,
  LIVE_SESSION_STATUSES,
  liveSegmentStatusLabel,
  liveSessionStatusLabel,
} from "@/lib/labels";

/** LIVE_CAPTURE.md §2. */
const CONTRACT_SESSION_STATUSES: LiveSessionStatus[] = ["waiting", "live", "ending", "ended", "error"];
const CONTRACT_SEGMENT_STATUSES: LiveSegmentStatus[] = ["received", "queued", "analyzing", "analyzed", "failed"];

describe("live labels", () => {
  it("names every session state of the contract in pt-BR", () => {
    expect([...LIVE_SESSION_STATUSES]).toEqual(CONTRACT_SESSION_STATUSES);
    expect(CONTRACT_SESSION_STATUSES.map((status) => LIVE_SESSION_STATUS_LABELS[status])).toEqual([
      "Aguardando",
      "Ao vivo",
      "Encerrando",
      "Encerrada",
      "Erro",
    ]);
    for (const status of CONTRACT_SESSION_STATUSES) expect(LIVE_SESSION_STATUS_TONES[status]).toBeTruthy();
  });

  it("treats waiting, live and ending as active; ended and error as final", () => {
    expect(CONTRACT_SESSION_STATUSES.filter((status) => isActiveLiveSessionStatus(status))).toEqual(["waiting", "live", "ending"]);
    expect(isActiveLiveSessionStatus(undefined)).toBe(false);
  });

  it("names every segment state of the contract in pt-BR", () => {
    expect([...LIVE_SEGMENT_STATUSES]).toEqual(CONTRACT_SEGMENT_STATUSES);
    for (const status of CONTRACT_SEGMENT_STATUSES) {
      expect(LIVE_SEGMENT_STATUS_LABELS[status]).toBeTruthy();
      expect(LIVE_SEGMENT_STATUS_LABELS[status]).not.toBe(status);
      expect(LIVE_SEGMENT_STATUS_TONES[status]).toBeTruthy();
    }
  });

  it("degrades gracefully for values the contract does not list", () => {
    expect(liveSessionStatusLabel("paused")).toBe("paused");
    expect(liveSessionStatusLabel(null)).toBe("Desconhecido");
    expect(liveSegmentStatusLabel("skipped")).toBe("skipped");
  });
});
