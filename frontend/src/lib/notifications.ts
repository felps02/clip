import type { Notification, NotificationType } from "@/lib/api/types";
import { LIVE_HIGHLIGHT_NOTIFICATION_LABEL, NOTIFICATION_TYPE_LABELS } from "@/lib/labels";

function stringField(data: Record<string, unknown>, key: string): string | null {
  const value = data[key];
  return typeof value === "string" && value.trim() ? value : null;
}

/** Live session a notification refers to (`data.sessionId`, or `data.liveSessionId`). */
export function notificationLiveSessionId(notification: Notification): string | null {
  const data = notification.data ?? {};
  return stringField(data, "sessionId") ?? stringField(data, "liveSessionId");
}

/** The `clip_ready` notification created for a highlight of a live session. */
export function isLiveHighlightNotification(notification: Notification): boolean {
  if (notification.type !== "clip_ready") return false;
  const data = notification.data ?? {};
  return notificationLiveSessionId(notification) !== null || data.live === true || data.sourceKind === "live";
}

/** pt-BR label of the notification's kind; never the raw `type`. */
export function notificationLabel(notification: Notification): string {
  if (isLiveHighlightNotification(notification)) return LIVE_HIGHLIGHT_NOTIFICATION_LABEL;
  return NOTIFICATION_TYPE_LABELS[notification.type as NotificationType] ?? "Notificação";
}

/** Where the notification leads: the clip first, then the live session, the project or billing. */
export function notificationHref(notification: Notification): string | null {
  const data = notification.data ?? {};
  const clipId = stringField(data, "clipId");
  if (clipId) return `/clips/${encodeURIComponent(clipId)}`;
  const sessionId = notificationLiveSessionId(notification);
  if (sessionId) return `/live/${encodeURIComponent(sessionId)}`;
  const projectId = stringField(data, "projectId");
  if (projectId) return `/projects/${encodeURIComponent(projectId)}`;
  if (notification.type === "billing" || notification.type === "quota_warning") return "/billing";
  return null;
}

function normalized(value: string): string {
  return value.trim().toLocaleLowerCase("pt-BR");
}

/** Title to show (the label when the API sent none) and whether the label adds anything to it. */
export function notificationHeading(notification: Notification): { label: string; title: string; showLabel: boolean } {
  const label = notificationLabel(notification);
  const title = notification.title?.trim() || label;
  return { label, title, showLabel: !normalized(title).startsWith(normalized(label)) };
}
