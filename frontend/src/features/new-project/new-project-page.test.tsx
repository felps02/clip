import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NewProjectPage } from "@/features/new-project/new-project-page";
import { estimateProcessing } from "@/features/new-project/upload-tab";
import type { MeResponse, UsageResponse } from "@/lib/api/types";
import { readMediaDuration } from "@/lib/media";
import { DEFAULT_EDITING_PREFERENCES, DEFAULT_NOTIFICATION_PREFERENCES } from "@/lib/preferences";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }) }));
vi.mock("@/lib/media", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/lib/media")>()),
  readMediaDuration: vi.fn(),
}));

const limits: MeResponse["limits"] = {
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
  assistantMessagesPerMonth: 100,
};

const me: MeResponse = {
  user: { id: "u1", email: "a@b.c", displayName: "Ana", emailConfirmed: true, createdAt: "2026-01-01T00:00:00Z" },
  profile: {
    displayName: "Ana",
    locale: "pt-BR",
    editingPreferences: { ...DEFAULT_EDITING_PREFERENCES },
    notificationPreferences: { ...DEFAULT_NOTIFICATION_PREFERENCES },
  },
  subscription: { planCode: "free", planName: "Gratuito", status: "active" },
  limits,
};

const usage: UsageResponse = {
  plan: { code: "free", name: "Gratuito", limits },
  period: { start: "2026-10-01T00:00:00Z", end: "2026-11-01T00:00:00Z" },
  used: { processingSeconds: 60, renders: 1, storageBytes: 0, activeProjects: 1, assistantMessages: 3 },
  remaining: { processingSeconds: 3540, renders: 9, assistantMessages: 97 },
  history: [],
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
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const sliderValues = () => screen.getAllByRole("slider").slice(0, 2).map((thumb) => Number(thumb.getAttribute("aria-valuenow")));

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/auth/me")) return Promise.resolve(json(me));
    if (url.endsWith("/api/account/usage")) return Promise.resolve(json(usage));
    if (url.endsWith("/api/assistant/status")) return Promise.resolve(json({ available: false, provider: "none", model: null }));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
  vi.mocked(readMediaDuration).mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("estimateProcessing", () => {
  it("rounds the recording up to whole minutes and compares it with what is left in the period", () => {
    expect(estimateProcessing(12.7, usage)).toEqual({ estimateMinutes: 1, remainingSeconds: 3540, exceedsRemaining: false });
    expect(estimateProcessing(3600, usage)).toMatchObject({ estimateMinutes: 60, exceedsRemaining: true });
    expect(estimateProcessing(600, { ...usage, remaining: { processingSeconds: null } })).toMatchObject({ remainingSeconds: null, exceedsRemaining: false });
    // Older APIs without `remaining.processingSeconds`: computed from the plan and the usage.
    expect(estimateProcessing(600, { ...usage, remaining: {} }).remainingSeconds).toBe(3540);
    expect(estimateProcessing(90, undefined)).toMatchObject({ estimateMinutes: 2, remainingSeconds: undefined });
  });
});

describe("NewProjectPage", () => {
  it("fits the clip lengths into a recording shorter than the minimum, says so, and restores them when the file is removed", async () => {
    vi.mocked(readMediaDuration).mockResolvedValue(12.7);
    const user = userEvent.setup();
    render(<NewProjectPage />, { wrapper });

    await waitFor(() => expect(fetchMock.mock.calls.some((call) => urlOf(call[0]).endsWith("/api/auth/me"))).toBe(true));
    await waitFor(() => expect(sliderValues()).toEqual([15, 60]));
    await user.upload(screen.getByLabelText("Escolher arquivo"), new File(["x"], "curto.mp4", { type: "video/mp4" }));

    expect(
      await screen.findByText((_, element) =>
        element?.tagName === "SPAN" && /^O vídeo \(12,7 s\) é mais curto que a duração mínima dos clipes \(15 s\)\./.test(element.textContent ?? ""),
      ),
    ).toBeInTheDocument();
    expect(sliderValues()).toEqual([12, 12]);
    expect(screen.getByText(/Ajustada para o vídeo de 12,7 s: clipes de 12 a 12 s \(antes: 15 a 60 s\)/)).toBeInTheDocument();
    expect(await screen.findByText(/\(restam 59 min no período\)/)).toBeInTheDocument();
    expect(screen.getByText("Consumo estimado:", { exact: false })).toHaveTextContent("Consumo estimado: 1 min de processamento");

    await user.click(screen.getByRole("button", { name: "Remover arquivo" }));
    expect(sliderValues()).toEqual([15, 60]);
    expect(screen.queryByText(/Ajustada para o vídeo/)).not.toBeInTheDocument();
  });

  it("warns before the upload when the recording uses more minutes than are left or than the plan allows", async () => {
    vi.mocked(readMediaDuration).mockResolvedValue(7300);
    const user = userEvent.setup();
    render(<NewProjectPage />, { wrapper });

    await waitFor(() => expect(sliderValues()).toEqual([15, 60]));
    await user.upload(screen.getByLabelText("Escolher arquivo"), new File(["x"], "longa.mp4", { type: "video/mp4" }));

    expect(await screen.findByText(/O consumo estimado passa do que resta no período/)).toBeInTheDocument();
    expect(screen.getByText(/A duração excede o limite do seu plano \(120 min por vídeo\)/)).toBeInTheDocument();
    expect(screen.getByText("Consumo estimado:", { exact: false })).toHaveTextContent("Consumo estimado: 122 min de processamento");
    // Nothing was adjusted: the recording is longer than the clips.
    expect(sliderValues()).toEqual([15, 60]);
    expect(screen.getByRole("button", { name: "Enviar e analisar" })).toBeEnabled();
  });

  it("shows the assistant next to the settings, unavailable here, with the shortcuts working", async () => {
    const user = userEvent.setup();
    render(<NewProjectPage />, { wrapper });

    expect(await screen.findByRole("heading", { name: "Assistente" })).toBeInTheDocument();
    expect(await screen.findByText("Assistente indisponível neste ambiente")).toBeInTheDocument();
    await waitFor(() => expect(sliderValues()).toEqual([15, 60]));
    await user.click(screen.getByRole("button", { name: "Clipes longos (45–90 s)" }));
    expect(sliderValues()).toEqual([45, 90]);
    expect(screen.getByRole("button", { name: "Engraçados" })).toHaveAttribute("aria-pressed", "false");
    await user.click(screen.getByRole("button", { name: "Mais engraçados" }));
    expect(screen.getByRole("button", { name: "Engraçados" })).toHaveAttribute("aria-pressed", "true");
  });
});
