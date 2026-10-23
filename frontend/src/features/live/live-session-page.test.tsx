import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { toast } from "sonner";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LiveSessionPage } from "@/features/live/live-session-page";
import type { ClipSummary, LiveSegment, LiveSession, LiveSessionDetail } from "@/lib/api/types";

vi.mock("next/navigation", () => ({
  useParams: () => ({ id: "s-live" }),
  useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }),
  usePathname: () => "/live/s-live",
  useSearchParams: () => new URLSearchParams(""),
}));

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function problem(status: number): Response {
  return new Response(JSON.stringify({ title: "Erro", status }), { status, headers: { "content-type": "application/problem+json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const session: LiveSession = {
  id: "s-live",
  projectId: "p-live",
  title: "Live de sexta",
  status: "live",
  deviceId: "d1",
  deviceName: "PC do estúdio",
  segmentSeconds: 60,
  startedAt: "2026-10-23T20:00:00Z",
  lastSegmentAt: new Date().toISOString(),
  lastHeartbeatAt: new Date().toISOString(),
  online: true,
  stopRequested: false,
  companionState: { obsConnected: true, streaming: true, recording: true, uploading: false, queueLength: 2, lastError: null },
  stats: { segmentsReceived: 3, segmentsAnalyzed: 1, segmentsFailed: 1, highlights: 2, recordedSeconds: 180 },
};

function segment(sequence: number, status: LiveSegment["status"], error: string | null = null): LiveSegment {
  return {
    id: `seg-${sequence}`,
    sequence,
    startOffsetSeconds: sequence * 60,
    durationSeconds: 60.033,
    status,
    jobId: `job-${sequence}`,
    error,
    createdAt: "2026-10-23T20:01:00Z",
  };
}

function clip(id: string, title: string, status: ClipSummary["status"]): ClipSummary {
  return {
    id,
    projectId: "p-live",
    projectTitle: "Live de sexta",
    title,
    category: "reaction",
    score: 81,
    status,
    aspectRatio: "9:16",
    durationSeconds: 28,
    startSeconds: 100,
    endSeconds: 128,
    thumbnailUrl: null,
    createdAt: "2026-10-23T20:04:00Z",
    updatedAt: "2026-10-23T20:04:00Z",
  };
}

const fullDetail: LiveSessionDetail = {
  session,
  settings: null,
  segments: [segment(2, "analyzing"), segment(1, "failed", "Falha ao decodificar o áudio"), segment(0, "analyzed")],
  highlights: [clip("c2", "Reação ao clutch", "rendering"), clip("c1", "Virada no último round", "ready")],
  candidateCount: 5,
};

let detail: LiveSessionDetail;
let detailResponse: () => Response;
let stopCalls: number;

beforeEach(() => {
  detail = fullDetail;
  detailResponse = () => json(detail);
  stopCalls = 0;
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.endsWith("/api/live/sessions/s-live/stop") && init?.method === "POST") {
      stopCalls += 1;
      detail = { ...detail, session: { ...detail.session, stopRequested: true } };
      return Promise.resolve(json({ session: detail.session }, 202));
    }
    if (url.endsWith("/api/live/sessions/s-live")) return Promise.resolve(detailResponse());
    return Promise.resolve(problem(404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const detailCalls = () => fetchMock.mock.calls.filter(([input]) => urlOf(input).endsWith("/api/live/sessions/s-live")).length;

describe("LiveSessionPage", () => {
  it("shows the status, companion state, counters, project link, segments timeline and highlights", async () => {
    render(<LiveSessionPage />, { wrapper });

    expect(await screen.findByRole("heading", { level: 1, name: "Live de sexta" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Lives" })).toHaveAttribute("href", "/live");
    expect(screen.getAllByText("Ao vivo").length).toBeGreaterThan(0);
    expect(screen.getByRole("link", { name: "Ver projeto" })).toHaveAttribute("href", "/projects/p-live");

    const chips = screen.getByRole("list", { name: "Estado do companion" });
    expect(within(chips).getByText("OBS conectado")).toBeInTheDocument();
    expect(within(chips).getByText("Gravando")).toBeInTheDocument();
    expect(within(chips).getByText("Fila 2")).toBeInTheDocument();

    const timeline = screen.getByRole("list", { name: /Linha do tempo dos segmentos/ });
    const rows = within(timeline).getAllByRole("listitem");
    expect(rows.map((row) => within(row).getByText(/^Segmento \d+$/).textContent)).toEqual(["Segmento 2", "Segmento 1", "Segmento 0"]);
    expect(rows[0]).toHaveTextContent("02:00–03:00 da gravação · 60 s");
    expect(within(rows[0]).getByText("Analisando")).toBeInTheDocument();
    expect(within(rows[1]).getByText("Falhou")).toBeInTheDocument();
    expect(within(rows[1]).getByText("Erro: Falha ao decodificar o áudio")).toBeInTheDocument();
    expect(within(rows[2]).getByText("Analisado")).toBeInTheDocument();

    const highlights = screen.getByRole("region", { name: "Destaques" });
    expect(highlights).toHaveTextContent("5 momentos detectados · 2 clipes");
    expect(within(highlights).getByRole("link", { name: "Reação ao clutch" })).toHaveAttribute("href", "/clips/c2");
    expect(within(highlights).getByRole("link", { name: "Virada no último round" })).toHaveAttribute("href", "/clips/c1");
    expect(within(highlights).getByText("Renderizando")).toBeInTheDocument();
    expect(within(highlights).getByRole("link", { name: "Ver todos no projeto" })).toHaveAttribute("href", "/projects/p-live");
  });

  it("asks for confirmation before 'Encerrar sessão' and then shows that the stop was requested", async () => {
    const user = userEvent.setup();
    const success = vi.spyOn(toast, "success");
    render(<LiveSessionPage />, { wrapper });

    const stopButton = await screen.findByRole("button", { name: "Encerrar sessão" });
    await user.click(stopButton);
    let dialog = await screen.findByRole("dialog", { name: "Encerrar esta sessão?" });
    await user.click(within(dialog).getByRole("button", { name: "Voltar" }));
    expect(stopCalls).toBe(0);
    // Focus goes back to the button that opened the dialog, not to <body>.
    await waitFor(() => expect(stopButton).toHaveFocus());

    await user.click(stopButton);
    dialog = await screen.findByRole("dialog", { name: "Encerrar esta sessão?" });
    await user.click(within(dialog).getByRole("button", { name: "Encerrar sessão" }));

    await waitFor(() => expect(stopCalls).toBe(1));
    expect(success).toHaveBeenCalledWith("Encerramento solicitado");
    expect(await screen.findByText("O companion recebe o pedido no próximo heartbeat (a cada 10 s), envia o que falta e encerra a sessão.")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("button", { name: "Encerrar sessão" })).not.toBeInTheDocument());
    // "Encerrar sessão" is gone once the stop is requested: focus lands on "Atualizar" next to it.
    await waitFor(() => expect(screen.getByRole("button", { name: "Atualizar" })).toHaveFocus());
  });

  it("refreshes every 5 s while the session is active and stops once it ended", async () => {
    vi.useFakeTimers();
    render(<LiveSessionPage />, { wrapper });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(0);
    });
    expect(detailCalls()).toBe(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(detailCalls()).toBe(2);

    detail = { ...fullDetail, session: { ...session, status: "ended", endedAt: "2026-10-23T21:00:00Z" } };
    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    // The answer is rendered on the query's next notification tick.
    await act(async () => {
      await vi.advanceTimersByTimeAsync(50);
    });
    expect(detailCalls()).toBe(3);
    expect(screen.getByText("Sessão finalizada: a página não é mais atualizada automaticamente.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Encerrar sessão" })).not.toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(30_000);
    });
    expect(detailCalls()).toBe(3);
  });

  it("counts the time since the last heartbeat in the chip and in the companion card alike, never 'agora'", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-23T21:00:00Z"));
    const secondsAgo = (seconds: number) => new Date(Date.now() - seconds * 1000).toISOString();
    // The API reports `online: false` 30 s after the last heartbeat; every poll returns this same answer.
    detail = { ...fullDetail, session: { ...session, online: false, lastHeartbeatAt: secondsAgo(35), lastSegmentAt: secondsAgo(50) } };
    render(<LiveSessionPage />, { wrapper });
    await act(async () => {
      await vi.advanceTimersByTimeAsync(20);
    });

    const chip = () => within(screen.getByRole("list", { name: "Estado do companion" })).getByText(/Sem sinal do companion/);
    const lastContact = () => screen.getByText(/Último contato/);
    expect(chip()).toHaveTextContent("Sem sinal do companion · último contato há 35 segundos");
    expect(lastContact()).toHaveTextContent("Último contato há 35 segundos.");

    await act(async () => {
      await vi.advanceTimersByTimeAsync(60_000);
    });
    expect(detailCalls()).toBeGreaterThanOrEqual(12);
    expect(chip()).toHaveTextContent("Sem sinal do companion · último contato há 2 minutos");
    expect(lastContact()).toHaveTextContent("Último contato há 2 minutos.");
    expect(document.body).not.toHaveTextContent(/contato agora/i);
  });

  it("explains an error session with its reason", async () => {
    detail = { ...fullDetail, session: { ...session, status: "error", online: false, endReason: "dispositivo revogado" } };
    render(<LiveSessionPage />, { wrapper });

    const alert = await screen.findByText("A sessão terminou com erro");
    expect(alert.closest('[role="alert"]')).toHaveTextContent("Motivo: dispositivo revogado.");
    expect(screen.queryByRole("button", { name: "Encerrar sessão" })).not.toBeInTheDocument();
  });

  it("uses honest empty states for segments and highlights", async () => {
    detail = {
      ...fullDetail,
      session: { ...session, status: "waiting", startedAt: null, lastHeartbeatAt: null, companionState: null },
      segments: [],
      highlights: [],
      candidateCount: 0,
    };
    render(<LiveSessionPage />, { wrapper });

    expect(await screen.findByText("Nenhum segmento recebido ainda")).toBeInTheDocument();
    expect(screen.getByText("O primeiro segmento chega cerca de 60 s depois do início da gravação.")).toBeInTheDocument();
    expect(screen.getByText("Nenhum destaque ainda")).toBeInTheDocument();
    expect(screen.getByText("Os clipes aparecem aqui conforme os segmentos são analisados.")).toBeInTheDocument();
    expect(screen.getByText("O companion ainda não informou o estado desta sessão.")).toBeInTheDocument();
  });

  it("says when the session does not exist", async () => {
    detailResponse = () => problem(404);
    render(<LiveSessionPage />, { wrapper });

    expect(await screen.findByRole("heading", { level: 1, name: "Sessão não encontrada" })).toBeInTheDocument();
    expect(screen.getByText("Esta sessão não existe ou não pertence à sua conta.")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Lives" })).toHaveAttribute("href", "/live");
  });

  it("offers a retry for other failures", async () => {
    const user = userEvent.setup();
    detailResponse = () => problem(500);
    render(<LiveSessionPage />, { wrapper });

    expect(await screen.findByRole("heading", { level: 1, name: "Erro ao carregar a sessão" })).toBeInTheDocument();
    detailResponse = () => json(detail);
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    expect(await screen.findByRole("heading", { level: 1, name: "Live de sexta" })).toBeInTheDocument();
  });

  it("keeps the 'not available in this environment' wording for 501", async () => {
    detailResponse = () => problem(501);
    render(<LiveSessionPage />, { wrapper });

    expect(await screen.findByText("Captura ao vivo ainda não disponível neste ambiente")).toBeInTheDocument();
  });
});
