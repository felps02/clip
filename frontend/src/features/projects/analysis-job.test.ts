import { describe, expect, it } from "vitest";
import { pickAnalysisJob } from "@/features/projects/analysis-job";
import type { JobSummary } from "@/lib/api/types";

function job(overrides: Partial<JobSummary> & { id: string }): JobSummary {
  return { kind: "analyze_source", status: "completed", progress: 100, updatedAt: "2026-10-20T10:00:00Z", ...overrides };
}

describe("pickAnalysisJob", () => {
  it("prefers the running analysis even when the API lists jobs oldest-first", () => {
    const jobs = [
      job({ id: "old", status: "completed", updatedAt: "2026-10-20T10:00:00Z" }),
      job({ id: "new", status: "analyzing", progress: 40, updatedAt: "2026-10-21T10:00:00Z" }),
    ];
    expect(pickAnalysisJob(jobs)?.id).toBe("new");
    expect(pickAnalysisJob([...jobs].reverse())?.id).toBe("new");
  });

  it("picks the most recently updated analysis when none is running, regardless of position", () => {
    const jobs = [
      job({ id: "a", status: "failed", updatedAt: "2026-10-19T10:00:00Z" }),
      job({ id: "b", status: "completed", updatedAt: "2026-10-22T08:30:00Z" }),
      job({ id: "c", status: "cancelled", updatedAt: "2026-10-21T10:00:00Z" }),
    ];
    expect(pickAnalysisJob(jobs)?.id).toBe("b");
  });

  it("ignores render jobs while an analysis exists, but falls back to an active job of any kind", () => {
    expect(
      pickAnalysisJob([
        job({ id: "render", kind: "render_clip", status: "generating_clips", updatedAt: "2026-10-22T10:00:00Z" }),
        job({ id: "analysis", status: "completed", updatedAt: "2026-10-20T10:00:00Z" }),
      ])?.id,
    ).toBe("analysis");
    expect(
      pickAnalysisJob([
        job({ id: "done-render", kind: "render_clip", status: "completed", updatedAt: "2026-10-22T10:00:00Z" }),
        job({ id: "live", kind: "live_segment", status: "queued", updatedAt: "2026-10-21T10:00:00Z" }),
      ])?.id,
    ).toBe("live");
  });

  it("keeps the API order when timestamps are missing and returns undefined for no jobs", () => {
    expect(pickAnalysisJob([job({ id: "first", updatedAt: undefined }), job({ id: "second", updatedAt: undefined })])?.id).toBe("first");
    expect(pickAnalysisJob([])).toBeUndefined();
  });
});
