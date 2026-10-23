import type { JobSummary } from "@/lib/api/types";
import { isActiveJobStatus } from "@/lib/labels";

function updatedAtMs(job: JobSummary): number {
  const time = job.updatedAt ? Date.parse(job.updatedAt) : Number.NaN;
  return Number.isNaN(time) ? Number.NEGATIVE_INFINITY : time;
}

/** Newest first; jobs without a usable `updatedAt` keep the order the API sent them in. */
function newestFirst(jobs: JobSummary[]): JobSummary[] {
  return [...jobs].sort((a, b) => updatedAtMs(b) - updatedAtMs(a));
}

/**
 * Picks the analysis job the project page tracks. `GET /api/projects/{id}` does not promise
 * any ordering of `jobs`, so array position is never trusted: a running analysis wins,
 * otherwise the most recently updated one, and a running job of another kind is the last
 * resort before falling back to whatever is newest.
 */
export function pickAnalysisJob(jobs: JobSummary[]): JobSummary | undefined {
  const analysis = jobs.filter((job) => job.kind === "analyze_source");
  const activeAnalysis = newestFirst(analysis.filter((job) => isActiveJobStatus(job.status)));
  if (activeAnalysis.length > 0) return activeAnalysis[0];
  if (analysis.length > 0) return newestFirst(analysis)[0];
  const activeAny = newestFirst(jobs.filter((job) => isActiveJobStatus(job.status)));
  if (activeAny.length > 0) return activeAny[0];
  return newestFirst(jobs)[0];
}
