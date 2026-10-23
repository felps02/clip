import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { toast } from "sonner";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { ProjectDetailPage } from "@/features/projects/project-detail-page";
import { DEFAULT_ANALYSIS_SETTINGS } from "@/features/projects/analysis-settings";
import { ARCHIVE_EMPTY_TEXT } from "@/features/projects/project-results";
import type { Candidate, ClipDetail, ClipSummary, Job, MeResponse, ProjectDetail } from "@/lib/api/types";
import { DEFAULT_EDITING_PREFERENCES, DEFAULT_NOTIFICATION_PREFERENCES } from "@/lib/preferences";

vi.mock("next/navigation", () => ({
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }),
  useParams: () => ({ id: "p1" }),
  usePathname: () => "/projects/p1",
  useSearchParams: () => new URLSearchParams(""),
}));

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
    maxAutoRenderPerJob: 15,
    maxRendersPerMonth: 10,
    maxRenderHeight: 720,
    maxConcurrentJobs: 1,
    priority: 0,
    retentionDays: 7,
    storageQuotaGb: 5,
    transcriptionModel: "base",
    allowWebcamLayout: false,
    allowCustomSubtitleStyles: false,
  },
};

function detailWith(overrides: Partial<ProjectDetail> = {}, durationSeconds = 5400): ProjectDetail {
  return {
    project: {
      id: "p1",
      title: "Live de sábado",
      sourceKind: "upload",
      sourcePlatform: "upload",
      status: "ready",
      settings: { ...DEFAULT_ANALYSIS_SETTINGS },
      createdAt: "2026-10-20T09:00:00Z",
      updatedAt: "2026-10-21T10:00:00Z",
      durationSeconds,
    },
    jobs: [{ id: "j1", kind: "analyze_source", status: "completed", progress: 100, updatedAt: "2026-10-21T10:00:00Z" }],
    candidateCount: 2,
    clipCount: 3,
    source: {
      asset: {
        id: "a1",
        kind: "source_upload",
        originalFileName: "live.mp4",
        contentType: "video/mp4",
        sizeBytes: 1000,
        status: "verified",
        durationSeconds,
        createdAt: "2026-10-20T09:00:00Z",
      },
      previewable: false,
    },
    ...overrides,
  };
}

const job: Job = {
  id: "j1",
  projectId: "p1",
  kind: "analyze_source",
  status: "completed",
  progress: 100,
  attempt: 1,
  events: [],
  result: { candidatesFound: 2, clipsRendered: 2, signals: { transcript: { available: true }, audio: { available: true }, visual: { available: true } } },
};

function clip(id: string, title: string, overrides: Partial<ClipSummary> = {}): ClipSummary {
  return {
    id,
    projectId: "p1",
    projectTitle: "Live de sábado",
    title,
    category: "funny",
    score: 80,
    status: "ready",
    aspectRatio: "9:16",
    durationSeconds: 25,
    startSeconds: 100,
    endSeconds: 125,
    thumbnailUrl: null,
    createdAt: "2026-10-21T10:00:00Z",
    updatedAt: "2026-10-21T10:00:00Z",
    currentRender: { id: `r-${id}`, version: 1, status: "ready", hasSubtitles: true },
    ...overrides,
  };
}

const clips: ClipSummary[] = [
  clip("c1", "Clip A"),
  clip("c2", "Clip B", { currentRender: { id: "r-c2", version: 1, status: "ready", hasSubtitles: false } }),
  clip("c3", "Clip C", { status: "rendering", currentRender: { id: "r-c3", version: 1, status: "rendering" } }),
];

function candidate(id: string, clipId: string | null): Candidate {
  return {
    id,
    rank: id === "k1" ? 1 : 2,
    startSeconds: 100,
    endSeconds: 125,
    durationSeconds: 25,
    category: "funny",
    score: 80,
    reasoning: "Risadas e pico de áudio.",
    transcript: null,
    signals: null,
    status: clipId ? "rendered" : "detected",
    clipId,
    previewable: false,
  };
}

const clipDetail: ClipDetail = {
  ...clips[0],
  subtitles: { enabled: true, style: "bold", position: "bottom" },
  framing: { mode: "auto" },
  currentRender: { id: "r-c1", version: 1, status: "ready", hasSubtitles: true, streamUrl: "http://api.test/media/c1.mp4", downloadUrl: null },
  renders: [],
  source: { previewable: false, durationSeconds: 5400 },
};

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function route(state: { detail: ProjectDetail; clips: ClipSummary[]; candidates: Candidate[]; signals?: Record<string, unknown> }) {
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/auth/me")) return Promise.resolve(json(me));
    if (url.endsWith("/api/projects/p1/jobs/j1")) return Promise.resolve(json(job));
    if (url.endsWith("/api/projects/p1")) return Promise.resolve(json(state.detail));
    if (url.includes("/api/projects/p1/candidates")) {
      return Promise.resolve(json({ items: state.candidates, signals: state.signals ?? job.result?.signals }));
    }
    if (url.includes("/api/clips/c1/download")) {
      return Promise.resolve(json({ url: "http://api.test/media/c1.mp4?download=1", expiresAt: "2026-10-23T12:00:00Z", fileName: "a.mp4" }));
    }
    if (url.endsWith("/api/clips/c1")) return Promise.resolve(json(clipDetail));
    if (url.includes("/api/clips?")) {
      return Promise.resolve(json({ items: state.clips, page: 1, pageSize: 100, total: state.clips.length }));
    }
    if (url.endsWith("/api/assistant/status")) return Promise.resolve(json({ available: false, provider: "none" }));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return (
    <QueryClientProvider client={client}>
      <TooltipProvider>{children}</TooltipProvider>
    </QueryClientProvider>
  );
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

/** Downloads start through a temporary link (`openDownload`): records each one instead of navigating. */
function captureDownloads(log: string[] = []): string[] {
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
    log.push(`download ${this.href}`);
  });
  return log;
}

/** Wraps the route() fetch mock: logs every request and lets a test answer some of them. */
function interceptFetch(override: (url: string, init?: RequestInit) => Promise<Response> | undefined, log: string[] = []): string[] {
  const base = fetchMock.getMockImplementation();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    log.push(`${init?.method ?? "GET"} ${url}`);
    return override(url, init) ?? (base ? base(input, init) : Promise.resolve(json({}, 404)));
  });
  return log;
}

const ARCHIVE_URL = "http://api.test/api/projects/p1/clips/archive";

describe("Project results screen", () => {
  it("leads with the finished clips and the .zip of all of them; details and moments start collapsed", async () => {
    route({ detail: detailWith(), clips, candidates: [candidate("k1", "c1"), candidate("k2", null)] });
    const user = userEvent.setup();
    render(<ProjectDetailPage />, { wrapper });

    const heading = await screen.findByRole("heading", { name: /Clipes prontos/ });
    expect(heading).toHaveTextContent("Clipes prontos (2)");
    expect(screen.getByRole("link", { name: "Baixar todos (.zip)" })).toHaveAttribute("href", "http://api.test/api/projects/p1/clips/archive");
    expect(await screen.findByRole("article", { name: "Clip A" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Editar Clip A" })).toHaveAttribute("href", "/clips/c1");
    // A clip still rendering is listed but not playable yet.
    const rendering = screen.getByRole("article", { name: "Clip C" });
    expect(within(rendering).getByText("Renderizando")).toBeInTheDocument();
    expect(within(rendering).queryByRole("button", { name: /Reproduzir/ })).not.toBeInTheDocument();
    expect(within(rendering).getByRole("button", { name: "Baixar Clip C" })).toBeDisabled();

    const details = screen.getByRole("button", { name: /Detalhes da análise/ });
    expect(details).toHaveAttribute("aria-expanded", "false");
    // The analysis status stays visible while the details are collapsed.
    expect(screen.getByText("Concluído", { exact: true })).toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: "Análise da gravação" })).not.toBeInTheDocument();
    const moments = screen.getByRole("button", { name: /Momentos encontrados/ });
    expect(moments).toHaveAttribute("aria-expanded", "false");
    expect(screen.queryByText("Risadas e pico de áudio.")).not.toBeInTheDocument();

    await user.click(details);
    expect(await screen.findByRole("heading", { name: "Análise da gravação" })).toBeInTheDocument();
    await user.click(moments);
    expect(moments).toHaveAttribute("aria-expanded", "true");
    expect(await screen.findAllByText("Risadas e pico de áudio.")).toHaveLength(2);
    expect(screen.getByRole("button", { name: "Gerar clipe" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Abrir clipe" })).toHaveAttribute("href", "/clips/c1");
  });

  it("plays a clip inline and downloads it through a fresh signed URL", async () => {
    route({ detail: detailWith(), clips, candidates: [] });
    // With noopener window.open always returns null: it must not be used (it doubled every download).
    const open = vi.fn(() => null);
    vi.stubGlobal("open", open);
    const downloads = captureDownloads();
    const user = userEvent.setup();
    const { container } = render(<ProjectDetailPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Reproduzir Clip A" }));
    await waitFor(() => expect(container.querySelector("video")).not.toBeNull());
    expect(container.querySelector("video")).toHaveAttribute("src", "http://api.test/media/c1.mp4");

    await user.click(screen.getByRole("button", { name: "Baixar Clip A" }));
    await waitFor(() => expect(downloads).toEqual(["download http://api.test/media/c1.mp4?download=1"]));
    expect(open).not.toHaveBeenCalled();
  });

  it("“Baixar todos” checks with the API first, then downloads the .zip once", async () => {
    route({ detail: detailWith(), clips, candidates: [] });
    const user = userEvent.setup();
    render(<ProjectDetailPage />, { wrapper });
    const link = await screen.findByRole("link", { name: "Baixar todos (.zip)" });
    expect(link).toHaveAttribute("href", ARCHIVE_URL);

    const log = captureDownloads(interceptFetch(() => undefined));
    await user.click(link);

    await waitFor(() => expect(log).toContain(`download ${ARCHIVE_URL}`));
    expect(log.filter((entry) => entry.startsWith("download"))).toHaveLength(1);
    const listCall = log.findIndex((entry) => entry.startsWith("GET http://api.test/api/clips?"));
    expect(listCall).toBeGreaterThanOrEqual(0);
    expect(listCall).toBeLessThan(log.indexOf(`download ${ARCHIVE_URL}`));
  });

  it("“Baixar todos” refreshes an expired session before the download instead of showing the API's 401", async () => {
    route({ detail: detailWith(), clips, candidates: [] });
    const user = userEvent.setup();
    render(<ProjectDetailPage />, { wrapper });
    const link = await screen.findByRole("link", { name: "Baixar todos (.zip)" });

    // The 15-minute access token expired while the person watched the clips.
    let expired = true;
    const log = captureDownloads(
      interceptFetch((url) => {
        if (url.endsWith("/api/auth/refresh")) {
          expired = false;
          return Promise.resolve(new Response(null, { status: 204 }));
        }
        if (expired && url.includes("/api/clips?")) return Promise.resolve(json({ title: "Unauthorized", status: 401 }, 401));
        return undefined;
      }),
    );
    await user.click(link);

    await waitFor(() => expect(log).toContain(`download ${ARCHIVE_URL}`));
    const refresh = log.indexOf("POST http://api.test/api/auth/refresh");
    expect(refresh).toBeGreaterThanOrEqual(0);
    expect(refresh).toBeLessThan(log.indexOf(`download ${ARCHIVE_URL}`));
    expect(log.filter((entry) => entry.startsWith("download"))).toHaveLength(1);
  });

  it("“Baixar todos” stays in the app with a message when the session is gone or no clip is ready any more", async () => {
    route({ detail: detailWith(), clips, candidates: [] });
    const error = vi.spyOn(toast, "error");
    const user = userEvent.setup();
    render(<ProjectDetailPage />, { wrapper });
    const link = await screen.findByRole("link", { name: "Baixar todos (.zip)" });

    let answer: "expired" | "deleted" = "expired";
    const log = captureDownloads(
      interceptFetch((url) => {
        if (url.endsWith("/api/auth/refresh")) return Promise.resolve(json({ title: "Unauthorized", status: 401 }, 401));
        if (!url.includes("/api/clips?")) return undefined;
        if (answer === "expired") return Promise.resolve(json({ title: "Unauthorized", status: 401 }, 401));
        // Clips deleted in another tab: the .zip would answer 404.
        return Promise.resolve(json({ items: [], page: 1, pageSize: 100, total: 0 }));
      }),
    );

    await user.click(link);
    await waitFor(() => expect(error).toHaveBeenCalledWith("Sua sessão expirou. Entre novamente."));
    expect(log.some((entry) => entry.startsWith("download"))).toBe(false);

    answer = "deleted";
    await user.click(screen.getByRole("link", { name: "Baixar todos (.zip)" }));
    await waitFor(() => expect(error).toHaveBeenCalledWith(ARCHIVE_EMPTY_TEXT));
    expect(log.some((entry) => entry.startsWith("download"))).toBe(false);
  });

  it("says when a clip came out without subtitles because no speech was found", async () => {
    route({ detail: detailWith(), clips, candidates: [] });
    render(<ProjectDetailPage />, { wrapper });

    const silent = await screen.findByRole("article", { name: "Clip B" });
    expect(within(silent).getByText("Nenhuma fala detectada — legendas não geradas")).toBeInTheDocument();
    expect(within(screen.getByRole("article", { name: "Clip A" })).queryByText(/Nenhuma fala detectada/)).not.toBeInTheDocument();
  });

  it("explains a recording shorter than the clip minimum instead of suggesting a higher sensitivity", async () => {
    route({ detail: detailWith({ candidateCount: 0, clipCount: 0 }, 12.7), clips: [], candidates: [] });
    render(<ProjectDetailPage />, { wrapper });

    expect(
      await screen.findByText(/O vídeo \(12,7 s\) é mais curto que a duração mínima dos clipes \(15 s\)\. Use “Analisar novamente” e diminua a “Duração dos clipes” para uma mínima de até 12 s\./),
    ).toBeInTheDocument();
    expect(screen.queryByText(/sensibilidade/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("heading", { name: /Clipes prontos/ })).not.toBeInTheDocument();
  });

  it("keeps the sensitivity hint for recordings long enough", async () => {
    route({ detail: detailWith({ candidateCount: 0, clipCount: 0 }), clips: [], candidates: [] });
    render(<ProjectDetailPage />, { wrapper });

    expect(await screen.findByText(/Tente uma sensibilidade mais alta/)).toBeInTheDocument();
  });

  it("marks the moments of a recording without speech", async () => {
    route({
      detail: detailWith({ clipCount: 0 }),
      clips: [],
      candidates: [candidate("k2", null)],
      signals: { transcript: { available: true, speechDetected: false }, audio: { available: true }, visual: { available: true } },
    });
    render(<ProjectDetailPage />, { wrapper });

    expect(await screen.findByText("Nenhuma fala detectada — legendas não geradas")).toBeInTheDocument();
  });

  it("offers the assistant in “Analisar novamente”, starting from the project's settings", async () => {
    const detail = detailWith();
    detail.project.settings = { ...DEFAULT_ANALYSIS_SETTINGS, focusCategories: ["funny"], customKeywords: ["clutch"], instructions: "só as risadas" };
    route({ detail, clips, candidates: [] });
    const base = fetchMock.getMockImplementation();
    fetchMock.mockImplementation((input, init) => {
      if (urlOf(input).endsWith("/api/projects/p1/jobs") && init?.method === "POST") {
        return Promise.resolve(json({ job: { ...job, id: "j2", status: "queued", progress: 0 } }, 201));
      }
      return base ? base(input, init) : Promise.resolve(json({}, 404));
    });
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<ProjectDetailPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Analisar novamente" }));
    const dialog = await screen.findByRole("dialog", { name: "Analisar novamente" });
    expect(within(dialog).getByRole("heading", { name: "Assistente" })).toBeInTheDocument();
    expect(await within(dialog).findByText("Assistente indisponível neste ambiente")).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Mais engraçados" })).toHaveAttribute("aria-pressed", "true");
    expect(within(dialog).getByLabelText("Pedido em suas palavras (opcional)")).toHaveValue("só as risadas");

    await user.click(within(dialog).getByRole("button", { name: "Frases marcantes" }));
    await user.click(within(dialog).getByRole("button", { name: "Iniciar análise" }));
    await waitFor(() =>
      expect(fetchMock.mock.calls.some((call) => urlOf(call[0]).endsWith("/api/projects/p1/jobs") && call[1]?.method === "POST")).toBe(true),
    );
    const post = fetchMock.mock.calls.find((call) => urlOf(call[0]).endsWith("/api/projects/p1/jobs") && call[1]?.method === "POST");
    const body = JSON.parse(String(post?.[1]?.body)) as { settings: Record<string, unknown> };
    expect(body.settings).toMatchObject({ focusCategories: ["funny", "quote"], customKeywords: ["clutch"], instructions: "só as risadas" });
  });

  it("fits the clip lengths of “Analisar novamente” into a recording shorter than the minimum", async () => {
    route({ detail: detailWith({ candidateCount: 0, clipCount: 0 }, 12.7), clips: [], candidates: [] });
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<ProjectDetailPage />, { wrapper });

    await user.click((await screen.findAllByRole("button", { name: "Analisar novamente" }))[0]);
    const dialog = await screen.findByRole("dialog", { name: "Analisar novamente" });
    expect(within(dialog).getByText(/O vídeo tem 12,7 s, menos que a duração mínima anterior \(15 s\)/)).toBeInTheDocument();
    const [minThumb, maxThumb] = within(dialog).getAllByRole("slider");
    expect(minThumb).toHaveAttribute("aria-valuenow", "12");
    expect(maxThumb).toHaveAttribute("aria-valuenow", "12");
    expect(minThumb).toHaveAttribute("aria-valuemin", "5");
  });
});
