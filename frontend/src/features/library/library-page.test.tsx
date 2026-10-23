import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LibraryPage } from "@/features/library/library-page";

const navigation = vi.hoisted(() => ({ search: "", router: { replace: vi.fn(), push: vi.fn(), prefetch: vi.fn() } }));

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  usePathname: () => "/library",
  useSearchParams: () => new URLSearchParams(navigation.search),
}));

const fetchMock = vi.fn<typeof fetch>();
let projectListCalls: string[] = [];

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

function project(id: string, title: string) {
  return {
    id,
    title,
    sourceKind: "upload",
    sourcePlatform: "upload",
    status: "processing",
    createdAt: "2026-10-23T10:00:00Z",
    updatedAt: "2026-10-23T10:00:00Z",
    candidateCount: 0,
    clipCount: 0,
    // An active job used to make the filter poll the whole list every 2 s.
    latestJob: { id: `j-${id}`, kind: "analyze_source", status: "analyzing", progress: 10, updatedAt: "2026-10-23T10:00:00Z" },
  };
}

beforeEach(() => {
  navigation.search = "";
  projectListCalls = [];
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.includes("/api/clips")) return Promise.resolve(json({ items: [], page: 1, pageSize: 24, total: 0 }));
    if (url.includes("/api/projects")) {
      projectListCalls.push(url);
      const page = Number(new URL(url).searchParams.get("page"));
      const items = page === 1 ? Array.from({ length: 100 }, (_, index) => project(`p${index}`, `Live ${index}`)) : [project("p100", "Live 100")];
      return Promise.resolve(json({ items, page, pageSize: 100, total: 101 }));
    }
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers({ shouldAdvanceTime: true });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("LibraryPage project filter", () => {
  it("loads every project name once (all pages) and does not poll the projects list", async () => {
    render(<LibraryPage />, { wrapper });

    const filter = await screen.findByRole("combobox", { name: "Filtrar por projeto" });
    expect(await within(filter).findByRole("option", { name: "Live 100" })).toBeInTheDocument();
    expect(projectListCalls).toHaveLength(2);

    await vi.advanceTimersByTimeAsync(10_000);
    expect(projectListCalls).toHaveLength(2);
  });
});
