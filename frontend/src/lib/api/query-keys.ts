import type { CandidatesQuery, ClipsQuery, LiveSessionsQuery, PageQuery, ProjectsQuery } from "@/lib/api/types";

/** Centralised query keys so invalidation stays consistent across features. */
export const queryKeys = {
  auth: {
    me: ["auth", "me"] as const,
  },
  account: {
    profile: ["account", "profile"] as const,
    usage: ["account", "usage"] as const,
  },
  projects: {
    all: ["projects"] as const,
    list: (query: ProjectsQuery) => ["projects", "list", query] as const,
    options: ["projects", "options"] as const,
    detail: (id: string) => ["projects", "detail", id] as const,
    job: (projectId: string, jobId: string) => ["projects", "detail", projectId, "jobs", jobId] as const,
    candidates: (projectId: string, query: CandidatesQuery) =>
      ["projects", "detail", projectId, "candidates", query] as const,
  },
  clips: {
    all: ["clips"] as const,
    list: (query: ClipsQuery) => ["clips", "list", query] as const,
    detail: (id: string) => ["clips", "detail", id] as const,
  },
  dashboard: {
    summary: ["dashboard", "summary"] as const,
  },
  notifications: {
    all: ["notifications"] as const,
    list: (query: PageQuery & { unreadOnly?: boolean }) => ["notifications", "list", query] as const,
    unreadCount: ["notifications", "unread-count"] as const,
  },
  billing: {
    plans: ["plans"] as const,
    subscription: ["billing", "subscription"] as const,
    payments: (query: PageQuery) => ["billing", "payments", query] as const,
  },
  live: {
    all: ["live"] as const,
    /** Prefix of every session list and detail (invalidated after a stop or a revocation). */
    sessionsAll: ["live", "sessions"] as const,
    sessionLists: ["live", "sessions", "list"] as const,
    sessions: (query: LiveSessionsQuery) => ["live", "sessions", "list", query] as const,
    session: (id: string) => ["live", "sessions", "detail", id] as const,
    devices: ["live", "devices"] as const,
  },
  assistant: {
    status: ["assistant", "status"] as const,
  },
};
