"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { deleteClip, getClip, getClipDownload, listClips, renderClip, updateClip } from "@/lib/api/clips";
import { queryKeys } from "@/lib/api/query-keys";
import type { ClipDetail, ClipSummary, ClipsQuery, UpdateClipRequest } from "@/lib/api/types";
import { isActiveJobStatus } from "@/lib/labels";

const ACTIVE_POLL_MS = 2000;
const LIST_POLL_MS = 5000;
/**
 * A clip waiting for its render ("pending") normally starts rendering within seconds. One that
 * stayed pending longer than this (render refused, job lost) no longer keeps lists polling;
 * the page still refreshes on focus and after any clip mutation.
 */
export const PENDING_POLL_WINDOW_MS = 10 * 60_000;

export function clipListNeedsPolling(items: ClipSummary[], now: number = Date.now()): boolean {
  return items.some((clip) => {
    if (clip.status === "rendering") return true;
    if (clip.status !== "pending") return false;
    const updatedAt = Date.parse(clip.updatedAt);
    return Number.isFinite(updatedAt) && now - updatedAt < PENDING_POLL_WINDOW_MS;
  });
}

export function clipHasActiveWork(clip: ClipDetail | undefined): boolean {
  if (!clip) return false;
  if (clip.activeJob && isActiveJobStatus(clip.activeJob.status)) return true;
  return clip.status === "rendering" || clip.currentRender?.status === "rendering";
}

export function useClips(query: ClipsQuery, options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.clips.list(query),
    queryFn: () => listClips(query),
    enabled: options.enabled ?? true,
    placeholderData: (previous) => previous,
    refetchInterval: (state) => (clipListNeedsPolling(state.state.data?.items ?? []) ? LIST_POLL_MS : false),
  });
}

export function useClip(clipId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.clips.detail(clipId ?? ""),
    queryFn: () => getClip(clipId as string),
    enabled: !!clipId,
    refetchInterval: (state) => (clipHasActiveWork(state.state.data) ? ACTIVE_POLL_MS : false),
  });
}

export function useUpdateClip(clipId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: UpdateClipRequest) => updateClip(clipId, body),
    onSuccess: (result) => {
      queryClient.setQueryData(queryKeys.clips.detail(clipId), (previous: ClipDetail | undefined) =>
        previous ? { ...previous, ...result.clip } : result.clip,
      );
      void queryClient.invalidateQueries({ queryKey: [...queryKeys.clips.all, "list"] });
    },
  });
}

export function useRenderClip(clipId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => renderClip(clipId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.detail(clipId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.account.usage });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

export function useDeleteClip() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (clipId: string) => deleteClip(clipId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    },
  });
}

/** Fetches a fresh signed download URL (they expire in minutes). */
export function useClipDownload() {
  return useMutation({
    mutationFn: ({ clipId, renderId }: { clipId: string; renderId?: string }) => getClipDownload(clipId, renderId),
  });
}
