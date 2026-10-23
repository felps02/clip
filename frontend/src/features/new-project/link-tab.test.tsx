import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LinkTab } from "@/features/new-project/link-tab";
import { DEFAULT_ANALYSIS_SETTINGS } from "@/features/projects/analysis-settings";
import { UUID_V4_PATTERN } from "@/lib/uuid";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }) }));

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

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/sources/validate")) return Promise.resolve(json({ platform: "other", supported: true, alternatives: [] }));
    if (url.endsWith("/api/projects")) return Promise.resolve(json({ project: { id: "p1" }, job: { id: "j1" } }, 201));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("LinkTab project creation", () => {
  it("sends a v4 Idempotency-Key even outside secure contexts (no crypto.randomUUID)", async () => {
    const realCrypto = globalThis.crypto;
    vi.stubGlobal("crypto", { getRandomValues: realCrypto.getRandomValues.bind(realCrypto) });
    const user = userEvent.setup();

    render(<LinkTab settings={DEFAULT_ANALYSIS_SETTINGS} onSettingsChange={() => undefined} onSwitchToUpload={() => undefined} />, { wrapper });
    expect(screen.getByLabelText("URL")).toHaveAttribute("placeholder", "https://…");
    await user.type(screen.getByLabelText("URL"), "https://videos.exemplo.com/live.mp4");
    await user.click(screen.getByRole("button", { name: "Verificar" }));
    await user.click(await screen.findByRole("button", { name: "Processar" }));

    await waitFor(() => expect(fetchMock.mock.calls.some((call) => urlOf(call[0]).endsWith("/api/projects"))).toBe(true));
    const call = fetchMock.mock.calls.find((entry) => urlOf(entry[0]).endsWith("/api/projects"));
    const headers = (call?.[1]?.headers ?? {}) as Record<string, string>;
    expect(headers["Idempotency-Key"]).toMatch(UUID_V4_PATTERN);
  });

  it("shows the verdict right under the field, focuses it and announces it", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/sources/validate")) {
        return Promise.resolve(
          json({
            platform: "twitch",
            supported: false,
            reason: "A Twitch não permite baixar transmissões de terceiros.",
            alternatives: [{ kind: "upload", title: "Envie o arquivo", description: "Baixe a gravação e envie aqui." }],
          }),
        );
      }
      return Promise.resolve(json({ title: "not found" }, 404));
    });
    const user = userEvent.setup();
    render(<LinkTab settings={DEFAULT_ANALYSIS_SETTINGS} onSettingsChange={() => undefined} onSwitchToUpload={() => undefined} />, { wrapper });

    const field = screen.getByLabelText("URL");
    await user.type(field, "https://www.twitch.tv/videos/123");
    await user.click(screen.getByRole("button", { name: "Verificar" }));

    const verdict = await screen.findByRole("region", { name: "Resultado da verificação do link" });
    await waitFor(() => expect(verdict).toHaveFocus());
    expect(verdict).toHaveTextContent("Não suportado");
    expect(verdict).toHaveTextContent("A Twitch não permite baixar transmissões de terceiros.");
    // Same card as the field: no second card further down the page.
    expect(field.closest("form")?.parentElement).toBe(verdict.parentElement);
    expect(screen.getByText("Link verificado: Twitch, não pode ser processado diretamente.")).toHaveAttribute("aria-live", "polite");
  });
});
