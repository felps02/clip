import { api } from "@/lib/api/client";
import type {
  CompanionDevice,
  CompanionTokenResponse,
  LiveSession,
  LiveSessionDetail,
  LiveSessionsQuery,
  Paged,
  StopLiveSessionResponse,
} from "@/lib/api/types";

/**
 * User endpoints of live capture (docs: LIVE_CAPTURE.md §2), all cookie-authenticated.
 * Environments without live capture answer 404/501: callers show that as "not available here".
 */
const base = "/api/live";

/** Default page size of the API's paged lists (docs/API.md → Conventions). */
export const LIVE_SESSIONS_PAGE_SIZE = 20;

type ListBody<T> = Partial<Paged<T>> | T[] | null | undefined;

function itemsOf<T>(data: ListBody<T>): T[] {
  if (Array.isArray(data)) return data;
  return Array.isArray(data?.items) ? data.items : [];
}

/**
 * `GET /api/live/sessions?page&pageSize&status` → paged `{ items: LiveSession[] }`, newest first.
 * Only the parameters that differ from the API defaults are sent.
 */
export async function listLiveSessions(query: LiveSessionsQuery = {}): Promise<Paged<LiveSession>> {
  const data = await api.get<ListBody<LiveSession>>(`${base}/sessions`, {
    query: {
      page: query.page && query.page > 1 ? query.page : undefined,
      pageSize: query.pageSize,
      status: query.status || undefined,
    },
  });
  const items = itemsOf(data);
  const paged = Array.isArray(data) ? undefined : data;
  return {
    items,
    page: paged?.page ?? query.page ?? 1,
    pageSize: paged?.pageSize ?? query.pageSize ?? LIVE_SESSIONS_PAGE_SIZE,
    total: paged?.total ?? items.length,
  };
}

/** `GET /api/live/sessions/{id}`: the session, its latest 50 segments and the clips of its project. */
export async function getLiveSession(sessionId: string): Promise<LiveSessionDetail> {
  const data = await api.get<LiveSessionDetail>(`${base}/sessions/${encodeURIComponent(sessionId)}`);
  return {
    ...data,
    segments: Array.isArray(data.segments) ? data.segments : [],
    highlights: Array.isArray(data.highlights) ? data.highlights : [],
    candidateCount: typeof data.candidateCount === "number" ? data.candidateCount : 0,
  };
}

/**
 * `POST /api/live/sessions/{id}/stop` → `202 { session }` with `stopRequested = true`. The companion
 * ends the session on its next heartbeat; nothing is ended by the web itself.
 */
export async function stopLiveSession(sessionId: string): Promise<LiveSession> {
  const data = await api.post<StopLiveSessionResponse>(`${base}/sessions/${encodeURIComponent(sessionId)}/stop`);
  return data.session;
}

/** `GET /api/live/companion/devices` → `{ items }` (revoked devices last). */
export async function listCompanionDevices(): Promise<CompanionDevice[]> {
  const data = await api.get<ListBody<CompanionDevice>>(`${base}/companion/devices`);
  return itemsOf(data);
}

/** `POST /api/live/companion/tokens { name }` (1–60 characters) → `{ device, token }`. The token is shown once. */
export function createCompanionToken(name: string): Promise<CompanionTokenResponse> {
  return api.post<CompanionTokenResponse>(`${base}/companion/tokens`, { name });
}

/** `DELETE /api/live/companion/devices/{id}` → `204`; the device's open sessions go to `error`. */
export function revokeCompanionDevice(deviceId: string): Promise<void> {
  return api.delete<void>(`${base}/companion/devices/${encodeURIComponent(deviceId)}`);
}
