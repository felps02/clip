"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  cancelJob,
  createClipFromCandidate,
  createJob,
  createProject,
  deleteProject,
  getJob,
  getProject,
  listCandidates,
  listProjectOptions,
  listProjects,
  updateProject,
} from "@/lib/api/projects";
import { queryKeys } from "@/lib/api/query-keys";
import type {
  AnalysisSettings,
  CandidatesQuery,
  CreateClipRequest,
  CreateProjectRequest,
  ProjectsQuery,
} from "@/lib/api/types";
import { isActiveJobStatus } from "@/lib/labels";

export const ACTIVE_JOB_POLL_MS = 2000;

export function useProjects(query: ProjectsQuery, options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.projects.list(query),
    queryFn: () => listProjects(query),
    enabled: options.enabled ?? true,
    placeholderData: (previous) => previous,
    refetchInterval: (state) => {
      const items = state.state.data?.items ?? [];
      return items.some((item) => isActiveJobStatus(item.latestJob?.status)) ? ACTIVE_JOB_POLL_MS : false;
    },
  });
}

/**
 * Project names for filter dropdowns. No polling (unlike `useProjects`, which refreshes every
 * 2 s while a job runs): titles rarely change and project mutations invalidate `projects`.
 */
export function useProjectOptions() {
  return useQuery({
    queryKey: queryKeys.projects.options,
    queryFn: listProjectOptions,
    staleTime: 60_000,
  });
}

export function useProject(projectId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.projects.detail(projectId ?? ""),
    queryFn: () => getProject(projectId as string),
    enabled: !!projectId,
    refetchInterval: (state) => {
      const jobs = state.state.data?.jobs ?? [];
      return jobs.some((job) => isActiveJobStatus(job.status)) ? ACTIVE_JOB_POLL_MS : false;
    },
  });
}

export function useJob(projectId: string | undefined, jobId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.projects.job(projectId ?? "", jobId ?? ""),
    queryFn: () => getJob(projectId as string, jobId as string),
    enabled: !!projectId && !!jobId,
    refetchInterval: (state) => (isActiveJobStatus(state.state.data?.status) ? ACTIVE_JOB_POLL_MS : false),
  });
}

export function useCandidates(projectId: string | undefined, query: CandidatesQuery, options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.projects.candidates(projectId ?? "", query),
    queryFn: () => listCandidates(projectId as string, query),
    enabled: (options.enabled ?? true) && !!projectId,
    placeholderData: (previous) => previous,
  });
}

export function useCreateProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ body, idempotencyKey }: { body: CreateProjectRequest; idempotencyKey: string }) =>
      createProject(body, idempotencyKey),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

export function useUpdateProject(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: { title: string }) => updateProject(projectId, body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
    },
  });
}

export function useDeleteProject() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (projectId: string) => deleteProject(projectId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

export function useCreateJob(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (settings?: Partial<AnalysisSettings>) => createJob(projectId, settings),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

export function useCancelJob() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ projectId, jobId }: { projectId: string; jobId: string }) => cancelJob(projectId, jobId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

export function useCreateClipFromCandidate(projectId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: ({ candidateId, body }: { candidateId: string; body: CreateClipRequest }) =>
      createClipFromCandidate(projectId, candidateId, body),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.detail(projectId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}
