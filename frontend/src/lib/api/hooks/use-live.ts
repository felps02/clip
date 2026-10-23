"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  createCompanionToken,
  getLiveSession,
  listCompanionDevices,
  listLiveSessions,
  revokeCompanionDevice,
  stopLiveSession,
} from "@/lib/api/live";
import { queryKeys } from "@/lib/api/query-keys";
import type { LiveSession, LiveSessionDetail, LiveSessionsQuery } from "@/lib/api/types";
import { isActiveLiveSessionStatus } from "@/lib/labels";

/** Live pages refresh every 5 s while a session is `waiting`, `live` or `ending` (LIVE_CAPTURE.md §4). */
export const LIVE_POLL_MS = 5_000;

/**
 * Shared by every live query:
 * - the interval never fires while the tab is hidden (TanStack Query skips interval refetches of a
 *   hidden document unless `refetchIntervalInBackground` is true), so a forgotten tab costs nothing;
 * - coming back to the tab refreshes stale data once (the app default is off), which is how a
 *   session the companion just created shows up when nothing was being polled.
 */
const LIVE_QUERY_BEHAVIOUR = {
  refetchIntervalInBackground: false,
  refetchOnWindowFocus: true,
} as const;

/** True while any session can still change; `ended` and `error` are final. */
export function liveSessionsNeedPolling(sessions: ReadonlyArray<Pick<LiveSession, "status">> | null | undefined): boolean {
  return (sessions ?? []).some((session) => isActiveLiveSessionStatus(session.status));
}

export function useLiveSessions(query: LiveSessionsQuery = {}, options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.live.sessions(query),
    queryFn: () => listLiveSessions(query),
    enabled: options.enabled ?? true,
    // Changing pages keeps the current page on screen until the next one arrives.
    placeholderData: (previous) => previous,
    refetchInterval: (state) => (liveSessionsNeedPolling(state.state.data?.items) ? LIVE_POLL_MS : false),
    ...LIVE_QUERY_BEHAVIOUR,
  });
}

export function useLiveSession(sessionId: string | undefined) {
  return useQuery({
    queryKey: queryKeys.live.session(sessionId ?? ""),
    queryFn: () => getLiveSession(sessionId as string),
    enabled: !!sessionId,
    refetchInterval: (state) => (isActiveLiveSessionStatus(state.state.data?.session.status) ? LIVE_POLL_MS : false),
    ...LIVE_QUERY_BEHAVIOUR,
  });
}

/**
 * Devices have no lifecycle of their own; `poll` (true while a session is active) keeps their
 * online state and last activity current next to the sessions.
 */
export function useCompanionDevices(options: { enabled?: boolean; poll?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.live.devices,
    queryFn: listCompanionDevices,
    enabled: options.enabled ?? true,
    refetchInterval: options.poll ? LIVE_POLL_MS : false,
    ...LIVE_QUERY_BEHAVIOUR,
  });
}

/**
 * The response carries the one-time device token. `gcTime: 0` drops the finished mutation (and the
 * token in its data) from the mutation cache as soon as no component observes it, instead of keeping
 * it for the default 5 minutes; the caller copies the token to its own state and calls `reset()`.
 */
export function useCreateCompanionToken() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (name: string) => createCompanionToken(name),
    gcTime: 0,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.live.devices });
    },
  });
}

export function useRevokeCompanionDevice() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (deviceId: string) => revokeCompanionDevice(deviceId),
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.live.devices });
      // The device's open sessions go to `error` ("dispositivo revogado").
      void queryClient.invalidateQueries({ queryKey: queryKeys.live.sessionsAll });
    },
  });
}

export function useStopLiveSession(sessionId: string) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => stopLiveSession(sessionId),
    onSuccess: (session) => {
      if (session) {
        queryClient.setQueryData<LiveSessionDetail>(queryKeys.live.session(sessionId), (previous) =>
          previous ? { ...previous, session: { ...previous.session, ...session } } : previous,
        );
      }
      void queryClient.invalidateQueries({ queryKey: queryKeys.live.sessionsAll });
    },
  });
}
