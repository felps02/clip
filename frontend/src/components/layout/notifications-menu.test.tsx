import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NotificationsMenu } from "@/components/layout/notifications-menu";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }) }));

const fetchMock = vi.fn<typeof fetch>();
let readAllCalls = 0;

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
  readAllCalls = 0;
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.includes("/api/notifications/unread-count")) return Promise.resolve(json({ count: readAllCalls > 0 ? 0 : 2 }));
    if (url.endsWith("/api/notifications/read-all") && init?.method === "POST") {
      readAllCalls += 1;
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (url.includes("/api/notifications")) {
      return Promise.resolve(
        json({
          items: [{ id: "n1", type: "job_completed", title: "Análise concluída", createdAt: "2026-10-23T10:00:00Z", readAt: null }],
          page: 1,
          pageSize: 10,
          total: 1,
        }),
      );
    }
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("NotificationsMenu", () => {
  it("lets keyboard users reach and activate 'Marcar todas como lidas'", async () => {
    const user = userEvent.setup();
    render(<NotificationsMenu />, { wrapper });

    const trigger = await screen.findByRole("button", { name: "Notificações, 2 não lidas" });
    trigger.focus();
    await user.keyboard("{Enter}");

    const markAll = await screen.findByRole("menuitem", { name: "Marcar todas como lidas" });
    await waitFor(() => expect(markAll).toHaveFocus());
    await user.keyboard("{Enter}");

    await waitFor(() => expect(readAllCalls).toBe(1));
  });
});
