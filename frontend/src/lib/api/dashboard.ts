import { api } from "@/lib/api/client";
import type { DashboardSummary, JobSummary, PageQuery, Paged } from "@/lib/api/types";

export function getDashboardSummary(): Promise<DashboardSummary> {
  return api.get<DashboardSummary>("/api/dashboard/summary");
}

export function listJobs(query: PageQuery & { status?: "active" | "all" } = {}): Promise<Paged<JobSummary>> {
  return api.get<Paged<JobSummary>>("/api/jobs", {
    query: { status: query.status ?? "active", page: query.page ?? 1, pageSize: query.pageSize ?? 20 },
  });
}
