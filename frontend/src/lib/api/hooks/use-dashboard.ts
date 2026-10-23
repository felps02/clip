"use client";

import { useQuery } from "@tanstack/react-query";
import { getDashboardSummary } from "@/lib/api/dashboard";
import { queryKeys } from "@/lib/api/query-keys";

export function useDashboardSummary() {
  return useQuery({
    queryKey: queryKeys.dashboard.summary,
    queryFn: getDashboardSummary,
    refetchInterval: (state) => ((state.state.data?.activeJobs.length ?? 0) > 0 ? 2000 : 30_000),
  });
}
