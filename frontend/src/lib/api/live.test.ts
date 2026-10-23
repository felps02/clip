import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError } from "@/lib/api/client";
import {
  createCompanionToken,
  getLiveSession,
  listCompanionDevices,
  listLiveSessions,
  revokeCompanionDevice,
  stopLiveSession,
} from "@/lib/api/live";
import type { CompanionDevice, LiveSession } from "@/lib/api/types";

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function lastCall(): { url: string; init: RequestInit } {
  const call = fetchMock.mock.calls.at(-1);
  if (!call) throw new Error("fetch was not called");
  return { url: urlOf(call[0]), init: call[1] ?? {} };
}

const session: LiveSession = {
  id: "s1",
  projectId: "p1",
  title: "Live de sexta",
  status: "live",
  deviceId: "d1",
  deviceName: "PC do estúdio",
  segmentSeconds: 60,
  startedAt: "2026-10-23T20:00:00Z",
  endedAt: null,
  lastSegmentAt: "2026-10-23T20:05:00Z",
  lastHeartbeatAt: "2026-10-23T20:05:10Z",
  online: true,
  stopRequested: false,
  endReason: null,
  companionState: { obsConnected: true, streaming: true, recording: true, uploading: false, queueLength: 0, lastError: null },
  stats: { segmentsReceived: 5, segmentsAnalyzed: 4, segmentsFailed: 0, highlights: 2, recordedSeconds: 300 },
};

const device: CompanionDevice = {
  id: "d1",
  name: "PC do estúdio",
  tokenPrefix: "a1b2c3d4",
  createdAt: "2026-10-20T10:00:00Z",
  lastSeenAt: "2026-10-23T20:05:10Z",
  revokedAt: null,
  online: true,
};

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("live API module", () => {
  it("lists sessions with cookies, sending only the parameters that differ from the API defaults", async () => {
    fetchMock.mockResolvedValueOnce(json({ items: [session], page: 1, pageSize: 20, total: 1 }));

    await expect(listLiveSessions()).resolves.toEqual({ items: [session], page: 1, pageSize: 20, total: 1 });
    expect(lastCall().url).toBe("http://api.test/api/live/sessions");
    expect(lastCall().init.method).toBe("GET");
    expect(lastCall().init.credentials).toBe("include");

    fetchMock.mockResolvedValueOnce(json({ items: [] }));
    await expect(listLiveSessions({ page: 2, pageSize: 10, status: "ending" })).resolves.toEqual({
      items: [],
      page: 2,
      pageSize: 10,
      total: 0,
    });
    expect(lastCall().url).toBe("http://api.test/api/live/sessions?page=2&pageSize=10&status=ending");
  });

  it("reads a session detail and never hands undefined lists to the page", async () => {
    fetchMock.mockResolvedValueOnce(json({ session, settings: null }));

    const detail = await getLiveSession("s 1/x");

    expect(lastCall().url).toBe("http://api.test/api/live/sessions/s%201%2Fx");
    expect(detail.session).toEqual(session);
    expect(detail.segments).toEqual([]);
    expect(detail.highlights).toEqual([]);
    expect(detail.candidateCount).toBe(0);
  });

  it("asks the companion to stop with POST /stop and returns the updated session", async () => {
    fetchMock.mockResolvedValueOnce(json({ session: { ...session, stopRequested: true } }, 202));

    const result = await stopLiveSession("s1");

    expect(lastCall().url).toBe("http://api.test/api/live/sessions/s1/stop");
    expect(lastCall().init.method).toBe("POST");
    expect(result.stopRequested).toBe(true);
  });

  it("creates a device token with the name and returns the one-time token", async () => {
    fetchMock.mockResolvedValueOnce(json({ device, token: "cfd_secret" }, 201));

    const result = await createCompanionToken("PC do estúdio");

    expect(lastCall().url).toBe("http://api.test/api/live/companion/tokens");
    expect(lastCall().init.method).toBe("POST");
    expect(JSON.parse(String(lastCall().init.body))).toEqual({ name: "PC do estúdio" });
    expect(result).toEqual({ device, token: "cfd_secret" });
  });

  it("lists devices from { items } and revokes one with DELETE", async () => {
    fetchMock.mockResolvedValueOnce(json({ items: [device] }));
    await expect(listCompanionDevices()).resolves.toEqual([device]);
    expect(lastCall().url).toBe("http://api.test/api/live/companion/devices");

    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(revokeCompanionDevice("d1")).resolves.toBeUndefined();
    expect(lastCall().url).toBe("http://api.test/api/live/companion/devices/d1");
    expect(lastCall().init.method).toBe("DELETE");
  });

  it("rejects with the HTTP status when the environment has no live endpoints", async () => {
    fetchMock.mockResolvedValueOnce(json({ title: "Not Found", status: 404 }, 404));
    await expect(listLiveSessions()).rejects.toMatchObject({ status: 404 });

    fetchMock.mockResolvedValueOnce(new Response("", { status: 501 }));
    const error = await getLiveSession("s1").catch((caught: unknown) => caught);
    expect(error).toBeInstanceOf(ApiError);
    expect((error as ApiError).status).toBe(501);
  });
});
