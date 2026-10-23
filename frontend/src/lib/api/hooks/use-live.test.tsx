import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, cleanup, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LIVE_POLL_MS, liveSessionsNeedPolling, useCompanionDevices, useLiveSession, useLiveSessions } from "@/lib/api/hooks/use-live";
import type { LiveSession, LiveSessionStatus } from "@/lib/api/types";

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function session(status: LiveSessionStatus, id = "s1"): LiveSession {
  return {
    id,
    projectId: "p1",
    title: "Live",
    status,
    segmentSeconds: 60,
    online: true,
    stopRequested: false,
    stats: { segmentsReceived: 0, segmentsAnalyzed: 0, segmentsFailed: 0, highlights: 0, recordedSeconds: 0 },
  };
}

/** One client per test (a wrapper that built its client while rendering would lose the cache on re-render). */
function createWrapper() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return function Wrapper({ children }: { children: ReactNode }) {
    return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  };
}

const callsTo = (fragment: string) => fetchMock.mock.calls.filter(([input]) => urlOf(input).includes(fragment)).length;

/** Lets pending fetches resolve and moves the fake clock. */
async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

function setVisibility(state: "visible" | "hidden") {
  if (state === "hidden") Object.defineProperty(document, "visibilityState", { configurable: true, get: () => "hidden" });
  else Reflect.deleteProperty(document, "visibilityState");
  window.dispatchEvent(new Event("visibilitychange"));
}

describe("liveSessionsNeedPolling", () => {
  it("is true while any session is waiting, live or ending", () => {
    expect(liveSessionsNeedPolling([session("ended"), session("waiting")])).toBe(true);
    expect(liveSessionsNeedPolling([session("live")])).toBe(true);
    expect(liveSessionsNeedPolling([session("ending")])).toBe(true);
  });

  it("is false when every session ended or failed, or there is none", () => {
    expect(liveSessionsNeedPolling([session("ended"), session("error")])).toBe(false);
    expect(liveSessionsNeedPolling([])).toBe(false);
    expect(liveSessionsNeedPolling(undefined)).toBe(false);
  });
});

describe("live query polling", () => {
  let listStatus: LiveSessionStatus;
  let detailStatus: LiveSessionStatus;

  beforeEach(() => {
    vi.useFakeTimers();
    listStatus = "live";
    detailStatus = "live";
    fetchMock.mockReset();
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/live/sessions")) return Promise.resolve(json({ items: [session(listStatus)] }));
      if (url.endsWith("/api/live/sessions/s1")) {
        return Promise.resolve(json({ session: session(detailStatus), segments: [], highlights: [], candidateCount: 0 }));
      }
      if (url.endsWith("/api/live/companion/devices")) return Promise.resolve(json({ items: [] }));
      return Promise.resolve(json({ title: "not found" }, 404));
    });
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    cleanup();
    setVisibility("visible");
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it("refetches the list every 5 s while a session is active and stops once none is", async () => {
    renderHook(() => useLiveSessions(), { wrapper: createWrapper() });
    await advance(0);
    expect(callsTo("/api/live/sessions")).toBe(1);

    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions")).toBe(2);
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions")).toBe(3);

    listStatus = "ending";
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions")).toBe(4);

    // This answer says "ended": no further refetch.
    listStatus = "ended";
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions")).toBe(5);
    await advance(LIVE_POLL_MS * 6);
    expect(callsTo("/api/live/sessions")).toBe(5);
  });

  it("never polls a list whose sessions all ended or failed", async () => {
    listStatus = "error";
    renderHook(() => useLiveSessions(), { wrapper: createWrapper() });
    await advance(0);
    await advance(LIVE_POLL_MS * 4);
    expect(callsTo("/api/live/sessions")).toBe(1);
  });

  it("does not poll while the tab is hidden and refreshes once it is visible again", async () => {
    renderHook(() => useLiveSessions(), { wrapper: createWrapper() });
    await advance(0);
    expect(callsTo("/api/live/sessions")).toBe(1);

    setVisibility("hidden");
    await advance(LIVE_POLL_MS * 4);
    expect(callsTo("/api/live/sessions")).toBe(1);

    setVisibility("visible");
    await advance(0);
    expect(callsTo("/api/live/sessions")).toBe(2);
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions")).toBe(3);
  });

  it("polls a session detail while it is active and stops on error", async () => {
    renderHook(() => useLiveSession("s1"), { wrapper: createWrapper() });
    await advance(0);
    expect(callsTo("/api/live/sessions/s1")).toBe(1);

    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions/s1")).toBe(2);

    detailStatus = "error";
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/sessions/s1")).toBe(3);
    await advance(LIVE_POLL_MS * 5);
    expect(callsTo("/api/live/sessions/s1")).toBe(3);
  });

  it("polls devices only when asked to (an active session is on screen)", async () => {
    const { rerender } = renderHook(({ poll }: { poll: boolean }) => useCompanionDevices({ poll }), {
      wrapper: createWrapper(),
      initialProps: { poll: false },
    });
    await advance(0);
    await advance(LIVE_POLL_MS * 3);
    expect(callsTo("/api/live/companion/devices")).toBe(1);

    rerender({ poll: true });
    await advance(LIVE_POLL_MS);
    expect(callsTo("/api/live/companion/devices")).toBe(2);
  });
});
