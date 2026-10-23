import { describe, expect, it } from "vitest";
import type { Notification } from "@/lib/api/types";
import { isLiveHighlightNotification, notificationHeading, notificationHref, notificationLabel } from "@/lib/notifications";

function notification(partial: Partial<Notification>): Notification {
  return { id: "n1", type: "system", title: "Aviso", createdAt: "2026-10-23T20:00:00Z", readAt: null, ...partial };
}

describe("notification labels and links", () => {
  it("labels a clip_ready notification in pt-BR and links to the clip", () => {
    const ready = notification({ type: "clip_ready", title: "Clutch no último round", data: { clipId: "c1", projectId: "p1" } });
    expect(isLiveHighlightNotification(ready)).toBe(false);
    expect(notificationLabel(ready)).toBe("Clipe pronto");
    expect(notificationHref(ready)).toBe("/clips/c1");
    expect(notificationHeading(ready)).toEqual({ label: "Clipe pronto", title: "Clutch no último round", showLabel: true });
  });

  it("recognises the clip_ready notification of a live highlight", () => {
    const highlight = notification({
      type: "clip_ready",
      title: "Novo destaque na live",
      body: "Live de sexta · reação",
      data: { clipId: "c9", projectId: "p1", sessionId: "s1" },
    });
    expect(isLiveHighlightNotification(highlight)).toBe(true);
    expect(notificationLabel(highlight)).toBe("Novo destaque na live");
    expect(notificationHref(highlight)).toBe("/clips/c9");
    // The label is not repeated above an identical title.
    expect(notificationHeading(highlight).showLabel).toBe(false);

    expect(isLiveHighlightNotification(notification({ type: "clip_ready", data: { clipId: "c9", live: true } }))).toBe(true);
    expect(isLiveHighlightNotification(notification({ type: "clip_ready", data: { clipId: "c9", sourceKind: "live" } }))).toBe(true);
  });

  it("falls back to the session, the project or billing when there is no clip", () => {
    expect(notificationHref(notification({ type: "clip_ready", data: { liveSessionId: "s 2" } }))).toBe("/live/s%202");
    expect(notificationHref(notification({ type: "job_completed", data: { jobId: "j1", projectId: "p1" } }))).toBe("/projects/p1");
    expect(notificationHref(notification({ type: "quota_warning", data: { threshold: 80 } }))).toBe("/billing");
    expect(notificationHref(notification({ type: "system", data: null }))).toBeNull();
  });

  it("never shows a raw type or an empty title", () => {
    const untitled = notification({ type: "clip_ready", title: "  ", data: { clipId: "c1" } });
    expect(notificationHeading(untitled)).toEqual({ label: "Clipe pronto", title: "Clipe pronto", showLabel: false });
    expect(notificationLabel(notification({ type: "something_new" as Notification["type"] }))).toBe("Notificação");
  });
});
