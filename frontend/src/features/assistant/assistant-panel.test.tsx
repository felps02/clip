import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState, type ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AssistantPanel } from "@/features/assistant/assistant-panel";
import { applyPlanLimits, DEFAULT_ANALYSIS_SETTINGS } from "@/features/projects/analysis-settings";
import type { AnalysisSettings, PlanLimits } from "@/lib/api/types";

const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

const chatCalls = () => fetchMock.mock.calls.filter((call) => urlOf(call[0]).endsWith("/api/assistant/chat"));

/** Keeps the settings in state like the pages do, and exposes the latest value. */
function Harness({
  onChange,
  initial = DEFAULT_ANALYSIS_SETTINGS,
  limits = null,
}: {
  onChange?: (next: AnalysisSettings) => void;
  initial?: AnalysisSettings;
  limits?: PlanLimits | null;
}) {
  const [settings, setSettings] = useState(initial);
  return (
    <>
      <AssistantPanel
        settings={settings}
        onSettingsChange={(next) => {
          setSettings(next);
          onChange?.(next);
        }}
        normalize={(next) => applyPlanLimits(next, limits)}
        limits={limits}
        context={{ fileName: "live.mp4", durationSeconds: 3600 }}
      />
      <output data-testid="settings">{JSON.stringify(settings)}</output>
    </>
  );
}

const current = (): AnalysisSettings => JSON.parse(screen.getByTestId("settings").textContent ?? "{}") as AnalysisSettings;

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function statusResponse(available: boolean) {
  return json({ available, provider: available ? "local" : "none", model: available ? "qwen3-1.7b" : null, reason: available ? null : "no_provider" });
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("AssistantPanel", () => {
  it("says the assistant is unavailable, never calls the chat and keeps the shortcuts working", async () => {
    fetchMock.mockImplementation((input) =>
      Promise.resolve(urlOf(input).endsWith("/api/assistant/status") ? statusResponse(false) : json({ title: "x" }, 404)),
    );
    const user = userEvent.setup();
    render(<Harness />, { wrapper });

    expect(await screen.findByText("Assistente indisponível neste ambiente")).toBeInTheDocument();
    expect(screen.queryByLabelText("Mensagem para o assistente")).not.toBeInTheDocument();
    expect(screen.getByText(/ajustes prontos, sem IA/)).toBeInTheDocument();

    const funny = screen.getByRole("button", { name: "Mais engraçados" });
    expect(funny).toHaveAttribute("aria-pressed", "false");
    await user.click(funny);
    expect(current().focusCategories).toEqual(["funny"]);
    expect(funny).toHaveAttribute("aria-pressed", "true");

    await user.click(screen.getByRole("button", { name: "Clipes curtos (15–30 s)" }));
    expect(current()).toMatchObject({ minClipSeconds: 15, targetClipSeconds: 20, maxClipSeconds: 30 });

    await user.click(funny);
    expect(current().focusCategories).toEqual([]);
    expect(chatCalls()).toHaveLength(0);
  });

  it("treats a failing status call as unavailable", async () => {
    fetchMock.mockImplementation(() => Promise.resolve(json({ title: "Erro", code: "not_found" }, 404)));
    render(<Harness />, { wrapper });
    expect(await screen.findByText("Assistente indisponível neste ambiente")).toBeInTheDocument();
  });

  it("sends the conversation per the contract, shows the API reply with its changes and applies them live", async () => {
    let resolveChat: (response: Response) => void = () => undefined;
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) return new Promise<Response>((resolve) => (resolveChat = resolve));
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const onChange = vi.fn();
    const user = userEvent.setup();
    render(<Harness onChange={onChange} />, { wrapper });

    const input = await screen.findByLabelText("Mensagem para o assistente");
    await user.type(input, "quero clipes curtos e engraçados{Enter}");

    // Typing indicator while the API answers; nothing is invented on the client meanwhile.
    expect(await screen.findByText("O assistente está respondendo…")).toBeInTheDocument();
    expect(onChange).not.toHaveBeenCalled();
    await waitFor(() => expect(chatCalls()).toHaveLength(1));
    const [url, init] = chatCalls()[0];
    expect(urlOf(url)).toBe("http://api.test/api/assistant/chat");
    expect(init?.method).toBe("POST");
    const body = JSON.parse(String(init?.body)) as Record<string, unknown>;
    expect(body.messages).toEqual([{ role: "user", content: "quero clipes curtos e engraçados" }]);
    expect(body.currentSettings).toMatchObject({ minClipSeconds: 15, maxClipSeconds: 60, focusCategories: [] });
    expect(body.context).toEqual({ fileName: "live.mp4", durationSeconds: 3600 });

    await act(async () => {
      resolveChat(
        json({
          reply: "Pronto: foco em momentos engraçados e clipes de 10 a 25 s.",
          settingsPatch: { focusCategories: ["funny"], minClipSeconds: 10, targetClipSeconds: 15, maxClipSeconds: 25 },
          changes: [
            { field: "focusCategories", label: "Foco: momentos engraçados", value: ["funny"] },
            { field: "minClipSeconds", label: "Duração: 10–25 s", value: 10 },
          ],
          provider: "local",
          model: "qwen3-1.7b",
        }),
      );
    });

    const conversation = await screen.findByRole("list", { name: "Conversa com o assistente" });
    expect(within(conversation).getByText("Pronto: foco em momentos engraçados e clipes de 10 a 25 s.")).toBeInTheDocument();
    expect(screen.queryByText("O assistente está respondendo…")).not.toBeInTheDocument();
    expect(current()).toMatchObject({
      focusCategories: ["funny"],
      minClipSeconds: 10,
      targetClipSeconds: 15,
      maxClipSeconds: 25,
      instructions: "quero clipes curtos e engraçados",
    });

    const changes = within(conversation).getByRole("list", { name: "Alterações aplicadas" });
    expect(within(changes).getByText("Foco: momentos engraçados")).toBeInTheDocument();
    await user.click(within(changes).getByRole("button", { name: "Desfazer: Duração: 10–25 s" }));
    expect(current()).toMatchObject({ focusCategories: ["funny"], minClipSeconds: 15, targetClipSeconds: 30, maxClipSeconds: 60 });
    expect(within(changes).getByText("desfeito")).toBeInTheDocument();

    await user.click(within(changes).getByRole("button", { name: "Desfazer: Foco: momentos engraçados" }));
    expect(current().focusCategories).toEqual([]);
    // The last request stays as the analysis instructions.
    expect(current().instructions).toBe("quero clipes curtos e engraçados");
  });

  it("keeps “Todos os momentos encontrados” when a reply changes the maximum, and Desfazer restores both", async () => {
    const limits: PlanLimits = {
      monthlyProcessingMinutes: 600,
      maxSourceDurationMinutes: 240,
      maxFileSizeMb: 4096,
      maxActiveProjects: 20,
      maxCandidatesPerJob: 30,
      maxAutoRenderPerJob: 30,
      maxRendersPerMonth: 200,
      maxRenderHeight: 1080,
      maxConcurrentJobs: 2,
      priority: 1,
      retentionDays: 30,
      storageQuotaGb: 50,
      transcriptionModel: "small",
      allowWebcamLayout: true,
      allowCustomSubtitleStyles: true,
      assistantMessagesPerMonth: 1000,
    };
    const replies = [
      { reply: "Até 30 momentos.", settingsPatch: { maxCandidates: 30 }, changes: [{ field: "maxCandidates", label: "Máximo de momentos: 30", value: 30 }] },
      { reply: "Só 5 momentos.", settingsPatch: { maxCandidates: 5 }, changes: [{ field: "maxCandidates", label: "Máximo de momentos: 5", value: 5 }] },
    ];
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) return Promise.resolve(json({ ...replies.shift(), provider: "local", model: "qwen3-1.7b" }));
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const user = userEvent.setup();
    render(<Harness limits={limits} initial={applyPlanLimits(DEFAULT_ANALYSIS_SETTINGS, limits)} />, { wrapper });
    expect(current()).toMatchObject({ maxCandidates: 20, autoRenderTop: 20 });

    const input = await screen.findByLabelText("Mensagem para o assistente");
    await user.type(input, "quero até 30 momentos{Enter}");
    await screen.findByText("Até 30 momentos.");
    expect(current()).toMatchObject({ maxCandidates: 30, autoRenderTop: 30 });

    await user.type(input, "só 5{Enter}");
    await screen.findByText("Só 5 momentos.");
    expect(current()).toMatchObject({ maxCandidates: 5, autoRenderTop: 5 });

    await user.click(screen.getByRole("button", { name: "Desfazer: Máximo de momentos: 5" }));
    expect(current()).toMatchObject({ maxCandidates: 30, autoRenderTop: 30 });
    await user.click(screen.getByRole("button", { name: "Desfazer: Máximo de momentos: 30" }));
    expect(current()).toMatchObject({ maxCandidates: 20, autoRenderTop: 20 });
  });

  it("marks as undone every change of the reply that the same Desfazer restored", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) {
        return Promise.resolve(
          json({
            reply: "Até 30 momentos, 8 viram clipes.",
            settingsPatch: { maxCandidates: 30, autoRenderTop: 8, focusCategories: ["funny"] },
            changes: [
              { field: "maxCandidates", label: "Máximo de momentos: 30", value: 30 },
              { field: "autoRenderTop", label: "Clipes automáticos: 8", value: 8 },
              { field: "focusCategories", label: "Foco: momentos engraçados", value: ["funny"] },
            ],
          }),
        );
      }
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const user = userEvent.setup();
    render(<Harness />, { wrapper });

    await user.type(await screen.findByLabelText("Mensagem para o assistente"), "30 momentos, 8 clipes, engraçados{Enter}");
    const changes = await screen.findByRole("list", { name: "Alterações aplicadas" });
    expect(current()).toMatchObject({ maxCandidates: 30, autoRenderTop: 8, focusCategories: ["funny"] });

    await user.click(within(changes).getByRole("button", { name: "Desfazer: Clipes automáticos: 8" }));
    expect(current()).toMatchObject({ maxCandidates: 20, autoRenderTop: 20, focusCategories: ["funny"] });
    expect(within(changes).getAllByText("desfeito")).toHaveLength(2);
    expect(within(changes).queryByRole("button", { name: "Desfazer: Máximo de momentos: 30" })).not.toBeInTheDocument();
    expect(within(changes).getByRole("button", { name: "Desfazer: Foco: momentos engraçados" })).toBeInTheDocument();
  });

  it("sends the previous turns with the next message and keeps the last request as instructions", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) return Promise.resolve(json({ reply: "Feito.", settingsPatch: {}, changes: [] }));
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const user = userEvent.setup();
    render(<Harness />, { wrapper });

    const input = await screen.findByLabelText("Mensagem para o assistente");
    await user.type(input, "primeiro pedido{Enter}");
    await screen.findByText("Feito.");
    await user.type(input, "segundo pedido{Enter}");
    await waitFor(() => expect(chatCalls()).toHaveLength(2));
    const body = JSON.parse(String(chatCalls()[1][1]?.body)) as { messages: unknown[] };
    expect(body.messages).toEqual([
      { role: "user", content: "primeiro pedido" },
      { role: "assistant", content: "Feito." },
      { role: "user", content: "segundo pedido" },
    ]);
    await waitFor(() => expect(current().instructions).toBe("segundo pedido"));
  });

  it("switches to the unavailable notice when the chat answers 503, without writing a reply", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) {
        return Promise.resolve(json({ title: "Indisponível", status: 503, code: "assistant_unavailable" }, 503));
      }
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const user = userEvent.setup();
    render(<Harness />, { wrapper });

    await user.type(await screen.findByLabelText("Mensagem para o assistente"), "mais hype{Enter}");

    expect(await screen.findByText("Assistente indisponível neste ambiente")).toBeInTheDocument();
    expect(screen.queryByLabelText("Mensagem para o assistente")).not.toBeInTheDocument();
    expect(screen.getByText("Não enviada")).toBeInTheDocument();
    expect(screen.queryByText(/^Assistente: /)).not.toBeInTheDocument();
    expect(current().instructions).toBeNull();
    // Shortcuts still work.
    await user.click(screen.getByRole("button", { name: "Mais reações e hype" }));
    expect(current().focusCategories).toEqual(["reaction", "hype"]);
  });

  it("explains rate limits and the monthly message limit", async () => {
    const responses = [
      json({ title: "Muitas", status: 429, code: "rate_limited" }, 429),
      json({ title: "Limite", status: 409, code: "plan_limit" }, 409),
    ];
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/assistant/status")) return Promise.resolve(statusResponse(true));
      if (url.endsWith("/api/assistant/chat")) return Promise.resolve(responses.shift() ?? json({}, 500));
      return Promise.resolve(json({ title: "x" }, 404));
    });
    const user = userEvent.setup();
    render(<Harness />, { wrapper });

    const input = await screen.findByLabelText("Mensagem para o assistente");
    await user.type(input, "oi{Enter}");
    expect(await screen.findByText(/Muitas mensagens em pouco tempo/)).toBeInTheDocument();
    // The unsent text comes back to the field.
    expect(input).toHaveValue("oi");
    await user.click(screen.getByRole("button", { name: "Enviar" }));
    expect(await screen.findByText(/limite de mensagens do assistente do seu plano/)).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Ver planos" })).toHaveAttribute("href", "/billing");
  });
});
