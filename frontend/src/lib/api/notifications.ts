import { api } from "@/lib/api/client";
import type { Notification, PageQuery, Paged, UnreadCountResponse } from "@/lib/api/types";

const base = "/api/notifications";

export function listNotifications(query: PageQuery & { unreadOnly?: boolean } = {}): Promise<Paged<Notification>> {
  return api.get<Paged<Notification>>(base, {
    query: {
      unreadOnly: query.unreadOnly ? true : undefined,
      page: query.page ?? 1,
      pageSize: query.pageSize ?? 10,
    },
  });
}

export function markNotificationRead(notificationId: string): Promise<void> {
  return api.post<void>(`${base}/${encodeURIComponent(notificationId)}/read`);
}

export function markAllNotificationsRead(): Promise<void> {
  return api.post<void>(`${base}/read-all`);
}

/** The contract only names the endpoint; accept the common shapes for the count. */
export async function getUnreadCount(): Promise<number> {
  const data = await api.get<UnreadCountResponse | number>(`${base}/unread-count`);
  if (typeof data === "number") return data;
  return data?.count ?? data?.unreadCount ?? data?.unread ?? 0;
}
