import { describe, expect, it } from "vitest";
import type { JobStatus } from "@/lib/api/types";
import {
  CANDIDATE_CATEGORY_LABELS,
  CLIP_STATUS_LABELS,
  contributorLabel,
  isActiveJobStatus,
  isCancellableJobStatus,
  isTerminalJobStatus,
  JOB_STATUS_LABELS,
  JOB_STATUS_TONES,
  JOB_STATUSES,
  jobStatusLabel,
  PROJECT_STATUS_LABELS,
} from "@/lib/labels";

/** Every status in docs/API.md + docs/DATA_MODEL.md (processing_jobs.status). */
const CONTRACT_JOB_STATUSES: JobStatus[] = [
  "pending",
  "queued",
  "preparing_media",
  "analyzing",
  "detecting_moments",
  "generating_clips",
  "finalizing",
  "completed",
  "failed",
  "cancelled",
];

describe("job status labels", () => {
  it("covers every status of the contract with a non-empty pt-BR label and a tone", () => {
    expect([...JOB_STATUSES].sort()).toEqual([...CONTRACT_JOB_STATUSES].sort());
    for (const status of CONTRACT_JOB_STATUSES) {
      expect(JOB_STATUS_LABELS[status]).toBeTruthy();
      expect(JOB_STATUS_LABELS[status]).not.toBe(status);
      expect(JOB_STATUS_TONES[status]).toBeTruthy();
    }
  });

  it("classifies terminal, active and cancellable statuses", () => {
    expect(isTerminalJobStatus("completed")).toBe(true);
    expect(isTerminalJobStatus("failed")).toBe(true);
    expect(isTerminalJobStatus("cancelled")).toBe(true);
    expect(isTerminalJobStatus("analyzing")).toBe(false);
    expect(isActiveJobStatus("queued")).toBe(true);
    expect(isActiveJobStatus("completed")).toBe(false);
    expect(isActiveJobStatus(undefined)).toBe(false);
    expect(isCancellableJobStatus("generating_clips")).toBe(true);
    expect(isCancellableJobStatus("cancelled")).toBe(false);
  });

  it("degrades gracefully for unknown values", () => {
    expect(jobStatusLabel("weird_status")).toBe("weird_status");
    expect(jobStatusLabel(null)).toBe("Desconhecido");
  });
});

describe("other label maps", () => {
  it("has pt-BR labels for every candidate category of the contract", () => {
    expect(CANDIDATE_CATEGORY_LABELS).toEqual({
      highlight: "destaque",
      reaction: "reação",
      funny: "engraçado",
      hype: "hype",
      tension: "tensão",
      gameplay: "gameplay",
      quote: "frase",
    });
  });

  it("covers project and clip statuses", () => {
    expect(Object.keys(PROJECT_STATUS_LABELS).sort()).toEqual(["cancelled", "created", "failed", "processing", "ready"]);
    expect(Object.keys(CLIP_STATUS_LABELS).sort()).toEqual(["failed", "pending", "ready", "rendering"]);
  });

  it("translates signal contributors", () => {
    expect(contributorLabel("audio.energy")).toBe("energia de áudio");
    expect(contributorLabel("transcript.keyword")).toBe("palavra-chave");
    expect(contributorLabel("visual.some_new_thing")).toBe("visual: some new thing");
  });
});
