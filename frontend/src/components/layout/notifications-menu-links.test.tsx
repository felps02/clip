import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NotificationsMenu } from "@/components/layout/notifications-menu";
import type { Notification } from "@/lib/api/types";

vi.mock("next/navigation", () => ({ useRouter: () => ({ push: vi.fn(), replace: vi.fn(), prefetch: vi.fn() }) }));

const fetchMock = vi.fn<typeof fetch>();
let readCalls: string[] = [];

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

const items: Notification[] = [
  {
    id: "n-live",
    type: "clip_ready",
    title: "Novo destaque na live",
    body: "Live de sexta · reação aos 12:30",
    data: { clipId: "c9", projectId: "p1", sessionId: "s1" },
    createdAt: "2026-10-23T20:10:00Z",
    readAt: null,
  },
  {
    id: "n-clip",
    type: "clip_ready",
    title: "Clutch no último round",
    data: { clipId: "c1", projectId: "p1" },
    createdAt: "2026-10-23T19:00:00Z",
    readAt: "2026-10-23T19:05:00Z",
  },
  { id: "n-system", type: "system", title: "Manutenção programada", createdAt: "2026-10-22T10:00:00Z", readAt: null },
];

/** jsdom cannot navigate; the app's router handles the click in the browser. */
function stopAnchorNavigation(event: MouseEvent) {
  if (event.target instanceof Element && event.target.closest("a")) event.preventDefault();
}

beforeEach(() => {
  readCalls = [];
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.includes("/api/notifications/unread-count")) return Promise.resolve(json({ count: 2 }));
    if (url.includes("/read") && init?.method === "POST") {
      readCalls.push(url);
      return Promise.resolve(new Response(null, { status: 204 }));
    }
    if (url.includes("/api/notifications")) return Promise.resolve(json({ items, page: 1, pageSize: 10, total: items.length }));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
  window.addEventListener("click", stopAnchorNavigation);
});

afterEach(() => {
  window.removeEventListener("click", stopAnchorNavigation);
  vi.unstubAllGlobals();
});

async function openMenu(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByRole("button", { name: "Notificações, 2 não lidas" }));
  return screen.findByRole("menuitem", { name: /Novo destaque na live/ });
}

describe("NotificationsMenu items", () => {
  it("renders clip_ready and live highlight notifications with a pt-BR label, linked to the clip", async () => {
    const user = userEvent.setup();
    render(<NotificationsMenu />, { wrapper });

    const live = await openMenu(user);
    expect(live.tagName).toBe("A");
    expect(live).toHaveAttribute("href", "/clips/c9");
    expect(live).toHaveTextContent("Live de sexta · reação aos 12:30");

    const clip = screen.getByRole("menuitem", { name: /Clutch no último round/ });
    expect(clip).toHaveAttribute("href", "/clips/c1");
    expect(clip).toHaveTextContent("Clipe pronto");
    expect(clip).not.toHaveTextContent("clip_ready");

    // Nothing to open: stays a plain menu item, still labelled in pt-BR.
    const system = screen.getByRole("menuitem", { name: /Manutenção programada/ });
    expect(system).not.toHaveAttribute("href");
    expect(system).toHaveTextContent("Sistema");
  });

  it("marks the notification as read and closes the menu when the link is followed from the keyboard", async () => {
    const user = userEvent.setup();
    render(<NotificationsMenu />, { wrapper });

    const live = await openMenu(user);
    live.focus();
    await user.keyboard("{Enter}");

    await waitFor(() => expect(readCalls).toEqual(["http://api.test/api/notifications/n-live/read"]));
    await waitFor(() => expect(screen.queryByRole("menu")).not.toBeInTheDocument());
  });
});
