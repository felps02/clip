import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ProjectsPage } from "@/features/projects/projects-page";
import type { ProjectSummary } from "@/lib/api/types";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  return { replace, router: { replace, push: vi.fn(), prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  usePathname: () => "/projects",
  useSearchParams: () => new URLSearchParams(""),
}));

const project: ProjectSummary = {
  id: "p1",
  title: "Live de sábado",
  sourceKind: "upload",
  sourcePlatform: "upload",
  status: "processing",
  createdAt: "2026-10-20T15:30:00Z",
  updatedAt: "2026-10-20T16:00:00Z",
  durationSeconds: 5400,
  latestJob: { id: "j1", kind: "analyze_source", status: "analyzing", progress: 42, updatedAt: "2026-10-20T16:00:00Z" },
  candidateCount: 0,
  clipCount: 3,
  thumbnailUrl: null,
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

const cancelCalls = () => fetchMock.mock.calls.filter((call) => urlOf(call[0]).endsWith("/api/projects/p1/jobs/j1/cancel"));

beforeEach(() => {
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/projects/p1/jobs/j1/cancel")) return Promise.resolve(json({ job: { ...project.latestJob, status: "cancelled" } }, 202));
    if (url.includes("/api/projects")) return Promise.resolve(json({ items: [project], page: 1, pageSize: 20, total: 1 }));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ProjectsPage", () => {
  it("asks for confirmation before cancelling a running analysis", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<ProjectsPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Ações para Live de sábado" }));
    await user.click(await screen.findByRole("menuitem", { name: "Cancelar processamento" }));

    expect(cancelCalls()).toHaveLength(0);
    const dialog = await screen.findByRole("dialog", { name: "Cancelar processamento?" });
    expect(dialog).toHaveTextContent("minutos já processados continuam contando na cota");

    await user.click(within(dialog).getByRole("button", { name: "Cancelar processamento" }));

    await waitFor(() => expect(cancelCalls()).toHaveLength(1));
    expect(cancelCalls()[0][1]?.method).toBe("POST");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
  });

  it("keeps the request out when the confirmation is dismissed", async () => {
    const user = userEvent.setup({ pointerEventsCheck: 0 });
    render(<ProjectsPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Ações para Live de sábado" }));
    await user.click(await screen.findByRole("menuitem", { name: "Cancelar processamento" }));
    const dialog = await screen.findByRole("dialog", { name: "Cancelar processamento?" });
    await user.click(within(dialog).getByRole("button", { name: "Continuar processando" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(cancelCalls()).toHaveLength(0);
  });

  it("keeps explicit table semantics and labels every cell for narrow screens", async () => {
    render(<ProjectsPage />, { wrapper });

    const table = await screen.findByRole("table");
    expect(within(table).getAllByRole("columnheader")).toHaveLength(7);
    const rows = within(table).getAllByRole("row");
    expect(rows).toHaveLength(2);
    const row = rows[1];
    expect(within(row).getAllByRole("cell")).toHaveLength(7);
    for (const label of ["Fonte:", "Status:", "Clipes:", "Criado em:"]) {
      expect(within(row).getByText(label)).toBeInTheDocument();
    }
    expect(within(row).getByText("Último trabalho")).toBeInTheDocument();
    expect(within(row).getByText("Analisando")).toBeInTheDocument();
  });
});
