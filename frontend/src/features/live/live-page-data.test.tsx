import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { hydrateRoot } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { toast } from "sonner";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LivePage, TOKEN_MANUAL_COPY_HINT } from "@/features/live/live-page";
import { queryKeys } from "@/lib/api/query-keys";
import type { CompanionDevice, LiveSession, Paged } from "@/lib/api/types";
import { formatDateTime } from "@/lib/format";

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

const minutesAgo = (minutes: number) => new Date(Date.now() - minutes * 60_000).toISOString();

const stats = (received: number, analyzed: number, failed: number, highlights: number, recordedSeconds: number) => ({
  segmentsReceived: received,
  segmentsAnalyzed: analyzed,
  segmentsFailed: failed,
  highlights,
  recordedSeconds,
});

const liveSession: LiveSession = {
  id: "s-live",
  projectId: "p-live",
  title: "Live de sexta",
  status: "live",
  deviceId: "d-online",
  deviceName: "PC do estúdio",
  segmentSeconds: 60,
  startedAt: "2026-10-23T20:00:00Z",
  lastSegmentAt: minutesAgo(1),
  lastHeartbeatAt: minutesAgo(0),
  online: true,
  stopRequested: false,
  companionState: {
    obsConnected: true,
    streaming: true,
    recording: true,
    uploading: true,
    queueLength: 2,
    lastError: "Falha de rede ao enviar o segmento 7",
  },
  stats: stats(12, 10, 1, 3, 734),
};

const waitingSession: LiveSession = {
  id: "s-waiting",
  projectId: "p-waiting",
  title: "Live de sábado",
  status: "waiting",
  segmentSeconds: 60,
  online: false,
  stopRequested: false,
  lastHeartbeatAt: null,
  companionState: null,
  stats: stats(0, 0, 0, 0, 0),
};

const endedSession: LiveSession = {
  id: "s-ended",
  projectId: "p-ended",
  title: "Live de quinta",
  status: "ended",
  segmentSeconds: 60,
  startedAt: "2026-10-22T20:00:00Z",
  endedAt: "2026-10-22T22:00:00Z",
  lastHeartbeatAt: "2026-10-22T22:00:00Z",
  online: false,
  stopRequested: false,
  endReason: "gravação encerrada no OBS",
  companionState: { obsConnected: true, streaming: false, recording: false, uploading: false, queueLength: 0 },
  stats: stats(120, 120, 0, 9, 7200),
};

const errorSession: LiveSession = {
  ...endedSession,
  id: "s-error",
  title: "Live de quarta",
  status: "error",
  endReason: "dispositivo revogado",
};

const devices: CompanionDevice[] = [
  { id: "d-online", name: "PC do estúdio", tokenPrefix: "a1b2c3d4", createdAt: "2026-10-20T10:00:00Z", lastSeenAt: minutesAgo(0), online: true },
  { id: "d-offline", name: "Notebook", tokenPrefix: "e5f6g7h8", createdAt: "2026-10-21T10:00:00Z", lastSeenAt: minutesAgo(90), online: false },
  { id: "d-new", name: "PC novo", tokenPrefix: "i9j0k1l2", createdAt: "2026-10-23T10:00:00Z", lastSeenAt: null, online: false },
  {
    id: "d-revoked",
    name: "PC antigo",
    tokenPrefix: "m3n4o5p6",
    createdAt: "2026-10-01T10:00:00Z",
    lastSeenAt: "2026-10-02T10:00:00Z",
    revokedAt: "2026-10-03T10:00:00Z",
    online: false,
  },
];

let sessionItems: LiveSession[];
let sessionsResponse: (url: string) => Response;
let deviceItems: CompanionDevice[];
let deleted: string[];
let tokenBodies: unknown[];

beforeEach(() => {
  sessionItems = [liveSession, waitingSession, endedSession, errorSession];
  sessionsResponse = () => json({ items: sessionItems, page: 1, pageSize: 20, total: sessionItems.length });
  deviceItems = devices;
  deleted = [];
  tokenBodies = [];
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    const method = init?.method ?? "GET";
    if (url.endsWith("/api/live/companion/tokens") && method === "POST") {
      const body = JSON.parse(String(init?.body)) as { name: string };
      tokenBodies.push(body);
      const device = { id: "d-created", name: body.name, tokenPrefix: "q7r8s9t0", createdAt: minutesAgo(0), online: false };
      return Promise.resolve(json({ device, token: "cfd_q7r8s9t0xxxxxxxx" }, 201));
    }
    if (url.includes("/api/live/companion/devices/") && method === "DELETE") {
      deleted.push(url);
      // Like the API: the device stays in the list, revoked (revoked devices last).
      const id = decodeURIComponent(url.slice(url.lastIndexOf("/") + 1));
      const revoked = deviceItems.filter((device) => device.id === id).map((device) => ({ ...device, online: false, revokedAt: minutesAgo(0) }));
      deviceItems = [...deviceItems.filter((device) => device.id !== id), ...revoked];
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (url.endsWith("/api/live/companion/devices")) return Promise.resolve(json({ items: deviceItems }));
    if (url.includes("/api/live/sessions")) return Promise.resolve(sessionsResponse(url));
    return Promise.resolve(problem(404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  Reflect.deleteProperty(document, "execCommand");
  Reflect.deleteProperty(window.navigator, "clipboard");
});

/** `<dt>` → `<dd>` text of a counters list. */
function counters(scope: HTMLElement): Record<string, string> {
  return Object.fromEntries(within(scope).getAllByRole("term").map((term) => [term.textContent, term.nextElementSibling?.textContent ?? ""]));
}

async function sessionRow(title: string): Promise<HTMLElement> {
  const link = await screen.findByRole("link", { name: new RegExp(title) });
  const row = link.closest("li");
  if (!row) throw new Error(`no row for ${title}`);
  return row;
}

describe("LivePage sessions", () => {
  it("shows each session's real state, companion chips, counters and a link to its detail", async () => {
    render(<LivePage />, { wrapper });

    const live = await sessionRow("Live de sexta");
    expect(within(live).getByRole("link", { name: /Live de sexta/ })).toHaveAttribute("href", "/live/s-live");
    expect(within(live).getByText("Ao vivo")).toBeInTheDocument();
    expect(live).toHaveTextContent(`PC do estúdio · início ${formatDateTime("2026-10-23T20:00:00Z")} · último segmento há 1 minuto`);
    const chips = within(live).getByRole("list", { name: "Estado do companion" });
    for (const chip of ["OBS conectado", "Gravando", "Enviando", "Fila 2", "Último erro: Falha de rede ao enviar o segmento 7"]) {
      expect(within(chips).getByText(chip)).toBeInTheDocument();
    }
    expect(counters(live)).toEqual({
      "Segmentos recebidos": "12",
      "Segmentos analisados": "10",
      "Segmentos com falha": "1",
      Destaques: "3",
      "Tempo gravado": "12:14",
    });

    const waiting = await sessionRow("Live de sábado");
    expect(within(waiting).getByText("Aguardando")).toBeInTheDocument();
    expect(waiting).toHaveTextContent("nenhum segmento recebido ainda");
    expect(within(waiting).getByText("O companion ainda não informou o estado desta sessão.")).toBeInTheDocument();

    const ended = await sessionRow("Live de quinta");
    expect(within(ended).getByText("Encerrada")).toBeInTheDocument();
    expect(ended).toHaveTextContent(`fim ${formatDateTime("2026-10-22T22:00:00Z")}`);
    expect(ended).not.toHaveTextContent("último segmento");
    expect(within(ended).getByText("Motivo: gravação encerrada no OBS")).toBeInTheDocument();
    // Companion chips only describe sessions that can still change.
    expect(within(ended).queryByRole("list", { name: "Estado do companion" })).not.toBeInTheDocument();
    expect(counters(ended)["Tempo gravado"]).toBe("2:00:00");

    const failed = await sessionRow("Live de quarta");
    expect(within(failed).getByText("Erro")).toBeInTheDocument();
    expect(within(failed).getByText("Motivo: dispositivo revogado")).toBeInTheDocument();

    expect(screen.getByText("Atualizando a cada 5 s enquanto houver sessão ativa.")).toBeInTheDocument();
  });

  it("labels the 'ending' state and flags a companion that stopped sending heartbeats", async () => {
    sessionItems = [{ ...liveSession, status: "ending", online: false, lastHeartbeatAt: minutesAgo(3), stopRequested: false }];
    render(<LivePage />, { wrapper });

    const row = await sessionRow("Live de sexta");
    expect(within(row).getByText("Encerrando")).toBeInTheDocument();
    expect(within(row).getByText(/Sem sinal do companion · último contato/)).toHaveTextContent("há 3 minutos");
  });

  it("says the list does not refresh by itself when no session is active", async () => {
    sessionItems = [endedSession, errorSession];
    render(<LivePage />, { wrapper });

    expect(await screen.findByText(/Sem sessão ativa, a lista não se atualiza sozinha/)).toBeInTheDocument();
    expect(screen.queryByText("Atualizando a cada 5 s enquanto houver sessão ativa.")).not.toBeInTheDocument();
  });

  it("also treats 501 as 'not available in this environment'", async () => {
    sessionsResponse = () => problem(501);
    render(<LivePage />, { wrapper });

    expect(await screen.findByText("Captura ao vivo ainda não disponível neste ambiente")).toBeInTheDocument();
    expect(screen.getByText("A geração de tokens fica disponível quando a captura ao vivo for habilitada no servidor.")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Gerar token" })).not.toBeInTheDocument();
  });

  it("keeps the last answer on screen when a refresh fails", async () => {
    const user = userEvent.setup();
    sessionItems = [endedSession];
    render(<LivePage />, { wrapper });
    await sessionRow("Live de quinta");

    sessionsResponse = () => problem(503);
    await user.click(screen.getByRole("button", { name: "Atualizar" }));

    expect(await screen.findByText("Não foi possível atualizar as sessões")).toBeInTheDocument();
    expect(screen.getByRole("link", { name: /Live de quinta/ })).toBeInTheDocument();
    expect(screen.queryByText("Não foi possível consultar as sessões")).not.toBeInTheDocument();
  });

  it("pages through the sessions", async () => {
    const user = userEvent.setup();
    sessionsResponse = (url) =>
      url.includes("page=2")
        ? json({ items: [errorSession], page: 2, pageSize: 20, total: 21 })
        : json({ items: [endedSession], page: 1, pageSize: 20, total: 21 });
    render(<LivePage />, { wrapper });
    await sessionRow("Live de quinta");

    await user.click(screen.getByRole("button", { name: /Próxima/ }));

    expect(await screen.findByRole("link", { name: /Live de quarta/ })).toBeInTheDocument();
    expect(fetchMock.mock.calls.map(([input]) => urlOf(input))).toContain("http://api.test/api/live/sessions?page=2");
  });
});

describe("LivePage devices", () => {
  it("lists devices with online state, last activity and token prefix", async () => {
    render(<LivePage />, { wrapper });

    const list = await screen.findByRole("list", { name: "Dispositivos" });
    const rows = within(list).getAllByRole("listitem");
    expect(rows).toHaveLength(4);

    expect(within(rows[0]).getByText("PC do estúdio")).toBeInTheDocument();
    expect(within(rows[0]).getByText("Online")).toBeInTheDocument();
    expect(within(rows[0]).getByText("cfd_a1b2c3d4…")).toBeInTheDocument();
    expect(rows[0]).toHaveTextContent("última atividade agora");

    expect(within(rows[1]).getByText("Offline")).toBeInTheDocument();
    expect(rows[1]).toHaveTextContent("última atividade há 2 horas");

    expect(rows[2]).toHaveTextContent("nunca conectou");

    expect(within(rows[3]).getByText("Revogado")).toBeInTheDocument();
    expect(rows[3]).toHaveTextContent(`revogado em ${formatDateTime("2026-10-03T10:00:00Z")}`);
    expect(within(rows[3]).queryByRole("button", { name: /Revogar/ })).not.toBeInTheDocument();
  });

  it("revokes a device only after confirmation", async () => {
    const user = userEvent.setup();
    const success = vi.spyOn(toast, "success");
    render(<LivePage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Revogar PC do estúdio" }));
    const dialog = await screen.findByRole("dialog", { name: "Revogar “PC do estúdio”?" });
    expect(dialog).toHaveTextContent("as sessões abertas dele são encerradas com erro");
    await user.click(within(dialog).getByRole("button", { name: "Cancelar" }));
    expect(deleted).toEqual([]);

    await user.click(screen.getByRole("button", { name: "Revogar PC do estúdio" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revogar" }));

    await waitFor(() => expect(deleted).toEqual(["http://api.test/api/live/companion/devices/d-online"]));
    expect(success).toHaveBeenCalledWith("Dispositivo revogado");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("returns focus to the device's button after Cancelar and to the device list after a revoke", async () => {
    const user = userEvent.setup();
    render(<LivePage />, { wrapper });

    const revokeButton = await screen.findByRole("button", { name: "Revogar PC do estúdio" });
    await user.click(revokeButton);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancelar" }));
    await waitFor(() => expect(revokeButton).toHaveFocus());

    await user.click(revokeButton);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revogar" }));

    // The revoked device loses its button with the refreshed list; focus stays on the list, not on <body>.
    const list = screen.getByRole("list", { name: "Dispositivos" });
    await waitFor(() => expect(list).toHaveFocus());
    await waitFor(() => expect(screen.queryByRole("button", { name: "Revogar PC do estúdio" })).not.toBeInTheDocument());
    expect(within(list).getAllByRole("listitem")[3]).toHaveTextContent("PC do estúdio");
    expect(list).toHaveFocus();
  });

  it("creates a token for the typed name, shown once with a warning, and asks before hiding it", async () => {
    const user = userEvent.setup();
    render(<LivePage />, { wrapper });

    await user.type(await screen.findByLabelText("Nome do dispositivo"), "PC da sala");
    await user.click(screen.getByRole("button", { name: "Gerar token" }));

    const field = await screen.findByDisplayValue("cfd_q7r8s9t0xxxxxxxx");
    expect(tokenBodies).toEqual([{ name: "PC da sala" }]);
    expect(field).toHaveAttribute("readonly");
    const alert = field.closest<HTMLElement>('[role="alert"]');
    expect(alert).toHaveTextContent("Token gerado — copie agora");
    expect(alert).toHaveTextContent("Token de “PC da sala”. Ele aparece só desta vez");
    // One token at a time: the form comes back only after the token is hidden.
    expect(screen.queryByRole("button", { name: "Gerar token" })).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    await user.click(await screen.findByRole("button", { name: "Ocultar mesmo assim" }));
    expect(screen.queryByDisplayValue("cfd_q7r8s9t0xxxxxxxx")).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText("Nome do dispositivo")).toHaveFocus());

    // An empty name falls back to a default the API accepts (1–60 characters).
    await user.click(screen.getByRole("button", { name: "Gerar token" }));
    await screen.findByDisplayValue("cfd_q7r8s9t0xxxxxxxx");
    expect(tokenBodies).toEqual([{ name: "PC da sala" }, { name: "Meu computador" }]);
  });

  it("keeps the one-time token out of the query client: only the token field holds it until it is hidden", async () => {
    const user = userEvent.setup();
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const token = "cfd_q7r8s9t0xxxxxxxx";
    const cached = () =>
      JSON.stringify([
        ...client.getMutationCache().getAll().map((mutation) => mutation.state),
        ...client.getQueryCache().getAll().map((query) => query.state.data),
      ]);
    render(
      <QueryClientProvider client={client}>
        <LivePage />
      </QueryClientProvider>,
    );

    await user.click(await screen.findByRole("button", { name: "Gerar token" }));
    expect(await screen.findByDisplayValue(token)).toBeInTheDocument();
    // The finished mutation leaves the cache right away (gcTime 0 + reset), not 5 minutes later.
    await waitFor(() => expect(client.getMutationCache().getAll()).toHaveLength(0));
    expect(cached()).not.toContain(token);

    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    await user.click(await screen.findByRole("button", { name: "Ocultar mesmo assim" }));
    expect(screen.queryByDisplayValue(token)).not.toBeInTheDocument();

    // A second token: the same.
    await user.click(screen.getByRole("button", { name: "Gerar token" }));
    expect(await screen.findByDisplayValue(token)).toBeInTheDocument();
    await waitFor(() => expect(client.getMutationCache().getAll()).toHaveLength(0));
    expect(cached()).not.toContain(token);
    expect(tokenBodies).toHaveLength(2);
  });

  it("keeps a 'copie manualmente' hint next to the token when no copy method works", async () => {
    const user = userEvent.setup();
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
    Object.defineProperty(document, "execCommand", { configurable: true, value: vi.fn(() => false) });
    render(<LivePage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Gerar token" }));
    await screen.findByDisplayValue("cfd_q7r8s9t0xxxxxxxx");
    expect(screen.queryByText(TOKEN_MANUAL_COPY_HINT)).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Copiar" }));

    expect(await screen.findByText(TOKEN_MANUAL_COPY_HINT)).toBeInTheDocument();
  });
});

describe("LivePage clock", () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  async function advance(ms: number) {
    await act(async () => {
      await vi.advanceTimersByTimeAsync(ms);
    });
  }

  it("keeps the last contact and the other relative times current while every poll returns the same data", async () => {
    vi.useFakeTimers();
    vi.setSystemTime(new Date("2026-10-23T21:00:00Z"));
    const secondsAgo = (seconds: number) => new Date(Date.now() - seconds * 1000).toISOString();
    // The API reports `online: false` 30 s after the last heartbeat.
    sessionItems = [{ ...liveSession, online: false, lastHeartbeatAt: secondsAgo(35), lastSegmentAt: secondsAgo(50) }];
    deviceItems = [{ ...devices[0], lastSeenAt: secondsAgo(10) }];
    const sessionCalls = () => fetchMock.mock.calls.filter(([input]) => urlOf(input).includes("/api/live/sessions")).length;
    render(<LivePage />, { wrapper });
    // Sessions first; the devices card (and its query) mounts once they answered. Its answer is
    // rendered on a 0 ms notification timer that the fake clock places 1 ms later.
    await advance(0);
    await advance(20);

    const row = () => {
      const item = screen.getByRole("link", { name: /Live de sexta/ }).closest("li");
      if (!item) throw new Error("no session row");
      return item;
    };
    const chip = () => within(row()).getByText(/Sem sinal do companion/);
    const device = () => within(screen.getByRole("list", { name: "Dispositivos" })).getAllByRole("listitem")[0];

    expect(chip()).toHaveTextContent("Sem sinal do companion · último contato há 35 segundos");
    expect(row()).toHaveTextContent("último segmento há 50 segundos");
    expect(device()).toHaveTextContent("última atividade agora");

    await advance(10_000);
    expect(chip()).toHaveTextContent("último contato há 45 segundos");

    await advance(290_000);
    expect(chip()).toHaveTextContent("Sem sinal do companion · último contato há 6 minutos");
    expect(row()).toHaveTextContent("último segmento há 6 minutos");
    expect(device()).toHaveTextContent("última atividade há 5 minutos");
    // Polled every 5 s the whole time, with the same answer.
    expect(sessionCalls()).toBeGreaterThanOrEqual(60);
    expect(screen.queryByText(/último contato agora/)).not.toBeInTheDocument();
  });
});

describe("LivePage hydration", () => {
  it("renders last activity as an absolute date on the server and as relative time only after hydration", async () => {
    const lastSeenAt = minutesAgo(5);
    const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } });
    const sessions: Paged<LiveSession> = { items: [endedSession], page: 1, pageSize: 20, total: 1 };
    client.setQueryData(queryKeys.live.sessions({}), sessions);
    client.setQueryData(queryKeys.live.devices, [{ ...devices[1], lastSeenAt }]);
    const app = (
      <QueryClientProvider client={client}>
        <LivePage />
      </QueryClientProvider>
    );

    const html = renderToString(app);
    expect(html).toContain(formatDateTime(lastSeenAt));
    expect(html).not.toContain("há 5 minutos");

    const container = document.createElement("div");
    container.innerHTML = html;
    document.body.appendChild(container);
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    let root: ReturnType<typeof hydrateRoot> | undefined;
    await act(async () => {
      root = hydrateRoot(container, app);
    });

    expect(container).toHaveTextContent("última atividade há 5 minutos");
    // No hydration mismatch was reported.
    expect(errors).not.toHaveBeenCalled();
    act(() => root?.unmount());
    container.remove();
  });
});
