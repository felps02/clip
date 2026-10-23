"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  getUnreadCount,
  listNotifications,
  markAllNotificationsRead,
  markNotificationRead,
} from "@/lib/api/notifications";
import { queryKeys } from "@/lib/api/query-keys";

export function useUnreadCount(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.notifications.unreadCount,
    queryFn: getUnreadCount,
    enabled: options.enabled ?? true,
    refetchInterval: 30_000,
    staleTime: 10_000,
  });
}

export function useNotifications(options: { enabled?: boolean; pageSize?: number } = {}) {
  const query = { page: 1, pageSize: options.pageSize ?? 10 };
  return useQuery({
    queryKey: queryKeys.notifications.list(query),
    queryFn: () => listNotifications(query),
    enabled: options.enabled ?? true,
    staleTime: 10_000,
  });
}

export function useMarkNotificationRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: markNotificationRead,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.all });
    },
  });
}

export function useMarkAllNotificationsRead() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: markAllNotificationsRead,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.notifications.all });
    },
  });
}
