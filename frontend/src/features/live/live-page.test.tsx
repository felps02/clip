import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { toast } from "sonner";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LivePage, TOKEN_COPY_FAILED_TEXT } from "@/features/live/live-page";

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

function serve(sessions: () => Promise<Response>) {
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.endsWith("/api/live/sessions")) return sessions();
    if (url.endsWith("/api/live/companion/devices")) return Promise.resolve(json({ items: [] }));
    if (url.endsWith("/api/live/companion/tokens") && init?.method === "POST") return Promise.resolve(json({ token: "cft_secret_token" }));
    // a final 401 would need the refresh endpoint; everything else is "not found"
    return Promise.resolve(problem(404));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  // Own properties set by the tests below (jsdom has neither execCommand nor navigator.clipboard).
  Reflect.deleteProperty(document, "execCommand");
  Reflect.deleteProperty(window.navigator, "clipboard");
});

const TOKEN = "cft_secret_token";

/** The LAN demo (http://<ip>:3000) is not a secure context: no navigator.clipboard there. */
function withoutClipboardApi() {
  Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
}

/** `document.execCommand("copy")` that records what was selected in the focused field when it ran. */
function stubExecCommand(result: boolean) {
  const selections: string[] = [];
  const execCommand = vi.fn((command: string) => {
    const active = document.activeElement;
    if (command === "copy" && active instanceof HTMLInputElement) {
      selections.push(active.value.slice(active.selectionStart ?? 0, active.selectionEnd ?? 0));
    }
    return result;
  });
  Object.defineProperty(document, "execCommand", { configurable: true, value: execCommand });
  return { execCommand, selections };
}

async function issueToken(user: ReturnType<typeof userEvent.setup>) {
  serve(() => Promise.resolve(json({ items: [] })));
  render(<LivePage />, { wrapper });
  await user.click(await screen.findByRole("button", { name: "Gerar token" }));
  return (await screen.findByDisplayValue(TOKEN)) as HTMLInputElement;
}

describe("LivePage states", () => {
  it("shows loading placeholders, never 'no devices', while the server has not answered", () => {
    serve(() => new Promise<Response>(() => undefined));

    render(<LivePage />, { wrapper });

    expect(screen.getByText("Carregando sessões…")).toBeInTheDocument();
    expect(screen.getByText("Consultando o servidor…")).toBeInTheDocument();
    expect(screen.queryByText("Nenhum dispositivo conectado")).not.toBeInTheDocument();
    expect(screen.queryByText("Nenhuma sessão ao vivo")).not.toBeInTheDocument();
  });

  it("shows the failure (with retry) instead of empty states when the query fails", async () => {
    serve(() => Promise.resolve(problem(500)));

    render(<LivePage />, { wrapper });

    expect(await screen.findByText("Não foi possível consultar as sessões")).toBeInTheDocument();
    expect(screen.getByText("A consulta ao servidor falhou, então tokens e dispositivos não podem ser exibidos agora.")).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: "Tentar novamente" }).length).toBeGreaterThanOrEqual(1);
    expect(screen.queryByText("Nenhum dispositivo conectado")).not.toBeInTheDocument();
    expect(screen.queryByText("Nenhuma sessão ao vivo")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Gerar token" })).not.toBeInTheDocument();
  });

  it("says live capture is not enabled when the endpoints do not exist (404/501)", async () => {
    serve(() => Promise.resolve(problem(404)));

    render(<LivePage />, { wrapper });

    expect(await screen.findByText("Captura ao vivo ainda não disponível neste ambiente")).toBeInTheDocument();
    expect(screen.getByText("A geração de tokens fica disponível quando a captura ao vivo for habilitada no servidor.")).toBeInTheDocument();
    expect(screen.queryByText("Nenhum dispositivo conectado")).not.toBeInTheDocument();
  });

  it("uses the empty states only for real empty lists", async () => {
    serve(() => Promise.resolve(json({ items: [] })));

    render(<LivePage />, { wrapper });

    expect(await screen.findByText("Nenhuma sessão ao vivo")).toBeInTheDocument();
    expect(await screen.findByText("Nenhum dispositivo conectado")).toBeInTheDocument();
  });

  it("asks before hiding a one-time token that was not copied", async () => {
    serve(() => Promise.resolve(json({ items: [] })));
    const user = userEvent.setup();

    render(<LivePage />, { wrapper });
    await user.click(await screen.findByRole("button", { name: "Gerar token" }));
    expect(await screen.findByDisplayValue(TOKEN)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    expect(await screen.findByText("Ocultar o token sem copiar?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Voltar ao token" }));
    const field = screen.getByDisplayValue(TOKEN) as HTMLInputElement;
    // Back on the token itself, selected (Ctrl+C copies it), not on <body>.
    await waitFor(() => expect(field).toHaveFocus());
    expect(field.value.slice(field.selectionStart ?? 0, field.selectionEnd ?? 0)).toBe(TOKEN);

    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    await user.click(await screen.findByRole("button", { name: "Ocultar mesmo assim" }));
    expect(screen.queryByDisplayValue(TOKEN)).not.toBeInTheDocument();
    await waitFor(() => expect(screen.getByLabelText("Nome do dispositivo")).toHaveFocus());
    // The dialog's own focus handling runs after it closed: it must leave the name field focused.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.getByLabelText("Nome do dispositivo")).toHaveFocus();
  });
});

describe("LivePage token copy", () => {
  it("shows the token in a read-only field, selected as soon as it is issued", async () => {
    const user = userEvent.setup();
    const field = await issueToken(user);

    expect(field).toHaveAttribute("readonly");
    expect(field).toHaveAccessibleName("Token do dispositivo");
    expect(field).toHaveFocus();
    expect(field.value.slice(field.selectionStart ?? 0, field.selectionEnd ?? 0)).toBe(TOKEN);
  });

  it("copies with the Clipboard API where it exists (https, localhost)", async () => {
    const user = userEvent.setup();
    const success = vi.spyOn(toast, "success");
    const { execCommand } = stubExecCommand(true);
    await issueToken(user);

    await user.click(screen.getByRole("button", { name: "Copiar" }));

    expect(await navigator.clipboard.readText()).toBe(TOKEN);
    expect(success).toHaveBeenCalledWith("Token copiado");
    expect(execCommand).not.toHaveBeenCalled();
    // Copied: hiding needs no confirmation.
    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    expect(screen.queryByDisplayValue(TOKEN)).not.toBeInTheDocument();
    expect(screen.queryByText("Ocultar o token sem copiar?")).not.toBeInTheDocument();
  });

  it("falls back to copying the selected field when navigator.clipboard is undefined (LAN demo over http)", async () => {
    const user = userEvent.setup();
    withoutClipboardApi();
    const success = vi.spyOn(toast, "success");
    const error = vi.spyOn(toast, "error");
    const { execCommand, selections } = stubExecCommand(true);
    await issueToken(user);

    await user.click(screen.getByRole("button", { name: "Copiar" }));

    expect(execCommand).toHaveBeenCalledWith("copy");
    expect(selections).toEqual([TOKEN]);
    expect(success).toHaveBeenCalledWith("Token copiado");
    expect(error).not.toHaveBeenCalled();
    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    expect(screen.queryByDisplayValue(TOKEN)).not.toBeInTheDocument();
    expect(screen.queryByText("Ocultar o token sem copiar?")).not.toBeInTheDocument();
  });

  it("asks for a manual copy, with the token selected, only when no copy works", async () => {
    const user = userEvent.setup();
    withoutClipboardApi();
    const error = vi.spyOn(toast, "error");
    stubExecCommand(false);
    const field = await issueToken(user);

    await user.click(screen.getByRole("button", { name: "Copiar" }));

    expect(error).toHaveBeenCalledWith(TOKEN_COPY_FAILED_TEXT);
    expect(field).toHaveFocus();
    expect(field.value.slice(field.selectionStart ?? 0, field.selectionEnd ?? 0)).toBe(TOKEN);
    // Not copied yet: hiding still asks.
    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    expect(await screen.findByText("Ocultar o token sem copiar?")).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Voltar ao token" }));

    // A manual copy (Ctrl+C) of the field counts as copied.
    fireEvent.copy(screen.getByDisplayValue(TOKEN));
    await user.click(screen.getByRole("button", { name: "Ocultar" }));
    expect(screen.queryByDisplayValue(TOKEN)).not.toBeInTheDocument();
  });
});
