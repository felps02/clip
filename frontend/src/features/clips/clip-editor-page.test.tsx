import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ClipEditorPage } from "@/features/clips/clip-editor-page";
import type { ClipDetail, MeResponse, PlanLimits, SubtitleSettings, UpdateClipRequest } from "@/lib/api/types";
import { normalizeEditingPreferences, normalizeNotificationPreferences } from "@/lib/preferences";

const navigation = vi.hoisted(() => ({ router: { replace: vi.fn(), push: vi.fn(), prefetch: vi.fn() } }));

vi.mock("next/navigation", () => ({
  useParams: () => ({ id: "clip-1" }),
  useRouter: () => navigation.router,
  usePathname: () => "/clips/clip-1",
}));

const freeLimits: PlanLimits = {
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
  transcriptionModel: "base",
  allowWebcamLayout: false,
  allowCustomSubtitleStyles: false,
};

let limits: PlanLimits;
/** The clip as the API stores it; PATCH applies the plan like PlanLimitHelpers.CapSubtitles. */
let stored: ClipDetail;
let patchBodies: UpdateClipRequest[];
const fetchMock = vi.fn<typeof fetch>();

function clipWith(subtitles: SubtitleSettings): ClipDetail {
  return {
    id: "clip-1",
    projectId: "p1",
    projectTitle: "Live de sexta",
    title: "Virada no fim",
    category: "highlight",
    score: 81,
    status: "ready",
    aspectRatio: "9:16",
    durationSeconds: 30,
    startSeconds: 120,
    endSeconds: 150,
    createdAt: "2026-10-20T10:00:00Z",
    updatedAt: "2026-10-20T10:00:00Z",
    currentRender: null,
    subtitles,
    framing: { mode: "auto", focusX: 0.5, focusY: 0.5 },
    renders: [],
    source: { previewable: false, previewUrl: null, durationSeconds: 3600 },
    activeJob: null,
  };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  limits = { ...freeLimits };
  stored = clipWith({ enabled: true, style: "bold", position: "bottom" });
  patchBodies = [];
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.endsWith("/api/clips/clip-1") && init?.method === "PATCH") {
      const body = JSON.parse(String(init.body)) as UpdateClipRequest;
      patchBodies.push(body);
      let subtitles = body.subtitles ?? stored.subtitles;
      if (body.subtitles && !limits.allowCustomSubtitleStyles) subtitles = { ...body.subtitles, style: "bold", position: "bottom" };
      stored = { ...stored, title: body.title ?? stored.title, subtitles };
      return Promise.resolve(json({ clip: stored, needsRender: !!body.subtitles }));
    }
    if (url.endsWith("/api/clips/clip-1")) return Promise.resolve(json(stored));
    if (url.endsWith("/api/auth/me")) {
      const me: MeResponse = {
        user: { id: "u1", email: "ana@exemplo.com", displayName: "Ana", emailConfirmed: true, createdAt: "2026-10-01T00:00:00Z" },
        profile: {
          displayName: "Ana",
          locale: "pt-BR",
          editingPreferences: normalizeEditingPreferences(null),
          notificationPreferences: normalizeNotificationPreferences(null),
        },
        subscription: { planCode: "free", planName: "Gratuito", status: "active" },
        limits,
      };
      return Promise.resolve(json(me));
    }
    return Promise.resolve(json({ title: "not found", status: 404 }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ClipEditorPage subtitles and the plan", () => {
  it("disables the positions a plan without custom styles does not include, and says why", async () => {
    render(<ClipEditorPage />, { wrapper });

    const position = await screen.findByLabelText("Posição");
    await waitFor(() => expect(within(position).getByRole("option", { name: "Em cima (indisponível no seu plano)" })).toBeDisabled());
    expect(within(position).getByRole("option", { name: "Centro (indisponível no seu plano)" })).toBeDisabled();
    expect(within(position).getByRole("option", { name: "Embaixo" })).toBeEnabled();
    expect(position).toHaveAccessibleDescription(/as legendas usam o estilo Destaque, na parte de baixo do quadro/);
    expect(screen.queryByText(/As legendas atuais deste clipe/)).not.toBeInTheDocument();
  });

  it("explains subtitles the plan no longer includes and, after saving, shows what the API stored instead of staying dirty", async () => {
    stored = clipWith({ enabled: true, style: "boxed", position: "top" });
    const user = userEvent.setup();
    render(<ClipEditorPage />, { wrapper });

    const style = await screen.findByLabelText("Estilo");
    const position = screen.getByLabelText("Posição");
    expect(
      await screen.findByText(/As legendas atuais deste clipe \(Caixa, em cima\) não estão incluídas no seu plano/),
    ).toBeInTheDocument();
    expect(position).toHaveValue("top");
    const save = screen.getByRole("button", { name: "Salvar alterações" });
    expect(save).toBeDisabled();

    await user.selectOptions(style, "bold");
    expect(save).toBeEnabled();
    await user.click(save);

    await waitFor(() => expect(position).toHaveValue("bottom"));
    expect(patchBodies).toEqual([{ subtitles: { enabled: true, style: "bold", position: "top" } }]);
    expect(style).toHaveValue("bold");
    expect(save).toBeDisabled();
    expect(screen.queryByText("Salve as alterações antes de renderizar.")).not.toBeInTheDocument();
    expect(screen.queryByText(/As legendas atuais deste clipe/)).not.toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Prévia da legenda no estilo Destaque, posição embaixo" })).toBeInTheDocument();
    expect(screen.getByText("Renderização pendente")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Renderizar novamente" })).toBeEnabled();
  });

  it("offers every position when the plan allows custom styles", async () => {
    limits = { ...freeLimits, allowCustomSubtitleStyles: true };
    stored = clipWith({ enabled: true, style: "clean", position: "center" });
    render(<ClipEditorPage />, { wrapper });

    const position = await screen.findByLabelText("Posição");
    await waitFor(() => expect(within(position).getByRole("option", { name: "Em cima" })).toBeEnabled());
    expect(position).toHaveValue("center");
    expect(screen.queryByText(/não estão incluídos no seu plano/)).not.toBeInTheDocument();
    expect(screen.queryByText(/As legendas atuais deste clipe/)).not.toBeInTheDocument();
  });
});

describe("ClipEditorPage player and missing subtitles (round 3)", () => {
  it("sizes the player after the clip (9:16 tall and narrow) and says when no speech was found", async () => {
    stored = {
      ...stored,
      currentRender: {
        id: "r1",
        version: 1,
        status: "ready",
        width: 720,
        height: 1280,
        hasSubtitles: false,
        streamUrl: "http://api.test/media/r1.mp4",
        downloadUrl: null,
      },
      renders: [{ id: "r1", version: 1, status: "ready", width: 720, height: 1280, hasSubtitles: false, aspectRatio: "9:16" }],
    };
    const { container } = render(<ClipEditorPage />, { wrapper });

    expect(await screen.findByText("Nenhuma fala detectada — legendas não geradas")).toBeInTheDocument();
    const frame = container.querySelector("[data-aspect-ratio]");
    expect(frame).toHaveAttribute("data-aspect-ratio", "9:16");
    expect(frame?.className).toContain("max-w-[calc(70dvh*9/16)]");
    expect(frame?.firstElementChild?.className).toContain("aspect-[9/16]");
    expect(screen.getByText(/sem legendas \(nenhuma fala detectada\)/)).toBeInTheDocument();
  });

  it("follows a square render and says nothing when the render has subtitles", async () => {
    stored = {
      ...stored,
      aspectRatio: "1:1",
      currentRender: { id: "r1", version: 1, status: "ready", width: 1080, height: 1080, hasSubtitles: true, streamUrl: "http://api.test/media/r1.mp4" },
    };
    const { container } = render(<ClipEditorPage />, { wrapper });

    await waitFor(() => expect(container.querySelector("[data-aspect-ratio]")).toHaveAttribute("data-aspect-ratio", "1:1"));
    expect(screen.queryByText(/Nenhuma fala detectada/)).not.toBeInTheDocument();
  });
});
