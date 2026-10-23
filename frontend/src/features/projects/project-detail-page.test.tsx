import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { ProjectDetailPage } from "@/features/projects/project-detail-page";
import type { Job, JobSummary, MeResponse, ProjectDetail } from "@/lib/api/types";
import { DEFAULT_EDITING_PREFERENCES, DEFAULT_NOTIFICATION_PREFERENCES } from "@/lib/preferences";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  const push = vi.fn();
  return { replace, push, router: { replace, push, prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  useParams: () => ({ id: "p1" }),
  usePathname: () => "/projects/p1",
  useSearchParams: () => new URLSearchParams(""),
}));

const oldJob: JobSummary = { id: "old", kind: "analyze_source", status: "completed", progress: 100, updatedAt: "2026-10-20T10:00:00Z" };
const newJob: JobSummary = { id: "new", kind: "analyze_source", status: "analyzing", progress: 40, updatedAt: "2026-10-21T10:00:00Z" };

// Oldest-first on purpose: the contract does not promise any ordering of `jobs`.
const detail: ProjectDetail = {
  project: {
    id: "p1",
    title: "Live de sábado",
    sourceKind: "upload",
    sourcePlatform: "upload",
    status: "processing",
    createdAt: "2026-10-20T09:00:00Z",
    updatedAt: "2026-10-21T10:00:00Z",
    durationSeconds: 5400,
  },
  jobs: [oldJob, newJob],
  candidateCount: 0,
  clipCount: 0,
  source: { asset: null, previewable: false },
};

function fullJob(summary: JobSummary): Job {
  return { ...summary, projectId: "p1", attempt: 1, queuedAt: summary.updatedAt, startedAt: summary.updatedAt, events: [] };
}

const me: MeResponse = {
  user: { id: "u1", email: "a@b.c", displayName: "Ana", emailConfirmed: true, createdAt: "2026-01-01T00:00:00Z" },
  profile: {
    displayName: "Ana",
    locale: "pt-BR",
    editingPreferences: { ...DEFAULT_EDITING_PREFERENCES },
    notificationPreferences: { ...DEFAULT_NOTIFICATION_PREFERENCES },
  },
  subscription: { planCode: "free", planName: "Gratuito", status: "active" },
  limits: {
    monthlyProcessingMinutes: 60,
    maxSourceDurationMinutes: 120,
    maxFileSizeMb: 2048,
    maxActiveProjects: 3,
    maxCandidatesPerJob: 15,
    maxAutoRenderPerJob: 3,
    maxRendersPerMonth: 10,
    maxRenderHeight: 720,
    maxConcurrentJobs: 1,
    priority: 0,
    retentionDays: 7,
    storageQuotaGb: 5,
    transcriptionModel: "tiny",
    allowWebcamLayout: false,
    allowCustomSubtitleStyles: false,
  },
};

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <TooltipProvider>{children}</TooltipProvider>
    </QueryClientProvider>
  );
}

const urls = () => fetchMock.mock.calls.map((call) => urlOf(call[0]));

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/auth/me")) return Promise.resolve(json(me));
    if (url.endsWith("/api/projects/p1/jobs/new/cancel")) return Promise.resolve(json({ job: fullJob({ ...newJob, status: "cancelled" }) }, 202));
    if (url.endsWith("/api/projects/p1/jobs/new")) return Promise.resolve(json(fullJob(newJob)));
    if (url.endsWith("/api/projects/p1/jobs/old")) return Promise.resolve(json(fullJob(oldJob)));
    if (url.endsWith("/api/projects/p1")) return Promise.resolve(json(detail));
    if (url.includes("/api/projects/p1/candidates")) return Promise.resolve(json({ items: [], signals: {} }));
    if (url.includes("/api/clips")) return Promise.resolve(json({ title: "Erro", status: 503, code: "unavailable" }, 503));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ProjectDetailPage", () => {
  it("tracks the running analysis even when the API lists the completed job first", async () => {
    render(<ProjectDetailPage />, { wrapper });

    const status = await screen.findByRole("heading", { name: "Análise da gravação" });
    const panel = status.closest("section");
    expect(panel).not.toBeNull();
    expect(within(panel as HTMLElement).getByText("Analisando")).toBeInTheDocument();
    expect(within(panel as HTMLElement).getByRole("button", { name: "Cancelar" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Analisar novamente" })).toBeDisabled();
    await waitFor(() => expect(urls().some((url) => url.endsWith("/api/projects/p1/jobs/new"))).toBe(true));
    expect(urls().some((url) => url.endsWith("/api/projects/p1/jobs/old"))).toBe(false);
  });

  it("asks for confirmation before cancelling the analysis", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<ProjectDetailPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Cancelar" }));

    const dialog = await screen.findByRole("dialog", { name: "Cancelar processamento?" });
    expect(urls().some((url) => url.endsWith("/cancel"))).toBe(false);
    await user.click(within(dialog).getByRole("button", { name: "Cancelar processamento" }));

    await waitFor(() => expect(urls().some((url) => url.endsWith("/api/projects/p1/jobs/new/cancel"))).toBe(true));
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("reports a clips loading failure with a retry instead of claiming there are no clips", async () => {
    render(<ProjectDetailPage />, { wrapper });

    const alert = await screen.findByText("Não foi possível carregar os clipes");
    expect(alert).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeInTheDocument();
    expect(screen.queryByText("Nenhum clipe gerado")).not.toBeInTheDocument();
  });
});
