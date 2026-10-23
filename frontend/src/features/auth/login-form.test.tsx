import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { LoginForm } from "@/features/auth/login-form";
import { HANDOFF_WATCHDOG_MS } from "@/features/auth/session-handoff";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  // One stable router object per module, like Next's real `useRouter()`.
  return { replace, search: "next=%2Fprojects%2Fabc", router: { replace, push: vi.fn(), prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  useSearchParams: () => new URLSearchParams(navigation.search),
}));

const fetchMock = vi.fn<typeof fetch>();

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function problem(status: number, code: string): Response {
  return new Response(JSON.stringify({ title: "Erro", status, code }), { status, headers: { "content-type": "application/problem+json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

/** Routes the mocked fetch: API refresh/login answers and the web host's hand-off verdict. */
function serve({ refresh, login, handoffAllowed }: { refresh: () => Response | Promise<Response>; login?: () => Response; handoffAllowed: boolean }) {
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/auth/refresh")) return Promise.resolve(refresh());
    if (url.endsWith("/api/auth/login") && login) return Promise.resolve(login());
    if (url.startsWith("/auth/handoff")) return Promise.resolve(json({ guard: true, allowed: handoffAllowed }));
    return Promise.resolve(problem(404, "not_found"));
  });
}

beforeEach(() => {
  fetchMock.mockReset();
  navigation.replace.mockReset();
  navigation.search = "next=%2Fprojects%2Fabc";
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("LoginForm session restore", () => {
  it("restores the session with POST /api/auth/refresh and continues to `next` once the web host sees the cookie", async () => {
    serve({ refresh: () => json({ user: { id: "u1" } }), handoffAllowed: true });

    render(<LoginForm />, { wrapper });

    expect(screen.getByRole("status")).toHaveTextContent("Verificando se você já está conectado…");
    expect(screen.getByRole("button", { name: "Entrar" })).toBeDisabled();
    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/projects/abc"));
    const urls = fetchMock.mock.calls.map((call) => urlOf(call[0]));
    expect(urls).toEqual(["http://api.test/api/auth/refresh", "/auth/handoff?next=%2Fprojects%2Fabc"]);
    expect(fetchMock.mock.calls[0][1]?.method).toBe("POST");
    expect(fetchMock.mock.calls[0][1]?.credentials).toBe("include");
  });

  it.each([
    ["a backslash", "next=%2F%5Cevil.example"],
    ["a tab", "next=%2F%09%2Fevil.example"],
    ["dot segments", "next=%2F..%2F%2Fevil.example"],
  ])("never hands a restored session off to another site through a `next` with %s", async (_label, search) => {
    navigation.search = search;
    serve({ refresh: () => json({ user: { id: "u1" } }), handoffAllowed: true });

    render(<LoginForm />, { wrapper });

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/dashboard"));
    expect(navigation.replace).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls.map((call) => urlOf(call[0]))).toContain("/auth/handoff?next=%2Fdashboard");
  });

  it("never bounces to the app when the proxy would send it back (cookie invisible to the web host)", async () => {
    serve({ refresh: () => json({ user: { id: "u1" } }), handoffAllowed: false });

    render(<LoginForm />, { wrapper });

    expect(await screen.findByText("Não foi possível entrar no aplicativo")).toBeInTheDocument();
    expect(screen.getByText(/Auth__CookieDomain/)).toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();
    expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
  });

  it("when the web host cannot answer the probe, navigates but stops saying 'redirecting' if the page is bounced back", async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      fetchMock.mockImplementation((input) => {
        const url = urlOf(input);
        if (url.endsWith("/api/auth/refresh")) return Promise.resolve(json({ user: { id: "u1" } }));
        return Promise.resolve(new Response("upstream error", { status: 502 }));
      });

      render(<LoginForm />, { wrapper });

      await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/projects/abc"));
      expect(screen.getByRole("status")).toHaveTextContent("Sessão restaurada. Redirecionando…");
      // The router kept this page mounted (the proxy sent the navigation back): no endless "redirecting".
      await vi.advanceTimersByTimeAsync(HANDOFF_WATCHDOG_MS + 100);
      expect(await screen.findByText("Não foi possível entrar no aplicativo")).toBeInTheDocument();
      expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
      expect(navigation.replace).toHaveBeenCalledTimes(1);
    } finally {
      vi.useRealTimers();
    }
  });

  it("does not probe the session when the guard is off but still continues when a session exists", async () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    serve({ refresh: () => json({ user: { id: "u1" } }), handoffAllowed: false });

    render(<LoginForm />, { wrapper });

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/projects/abc"));
    expect(fetchMock.mock.calls.map((call) => urlOf(call[0]))).toEqual(["http://api.test/api/auth/refresh"]);
  });

  it("shows the form when no session can be restored (401) and does not bounce to the app", async () => {
    serve({ refresh: () => problem(401, "unauthorized"), handoffAllowed: true });

    render(<LoginForm />, { wrapper });

    await waitFor(() => expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled());
    expect(screen.queryByRole("status")).not.toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("falls back to the form when the API is unreachable", async () => {
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    render(<LoginForm />, { wrapper });

    await waitFor(() => expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled());
    expect(navigation.replace).not.toHaveBeenCalled();
  });

  it("right after an explicit sign-out (signedOut=1) shows the form without asking the API for a session", async () => {
    navigation.search = "signedOut=1";
    serve({ refresh: () => json({ user: { id: "u1" } }), handoffAllowed: true });

    render(<LoginForm />, { wrapper });

    expect(screen.getByRole("status")).toHaveTextContent("Você saiu da sua conta neste navegador.");
    expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled();
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(fetchMock).not.toHaveBeenCalled();
    expect(navigation.replace).not.toHaveBeenCalled();
  });
});

describe("LoginForm submit", () => {
  it("after a successful login stays on the page with an explanation when the hand-off would bounce", async () => {
    serve({ refresh: () => problem(401, "unauthorized"), login: () => json({ user: { id: "u1" } }), handoffAllowed: false });
    const user = userEvent.setup();

    render(<LoginForm />, { wrapper });
    await waitFor(() => expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled());
    await user.type(screen.getByLabelText(/E-mail/), "ana@exemplo.com");
    await user.type(screen.getByLabelText(/Senha/), "segredo123");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    expect(await screen.findByText("Não foi possível entrar no aplicativo")).toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();
  });

  it("continues to `next` after a successful login when the web host sees the cookie", async () => {
    serve({ refresh: () => problem(401, "unauthorized"), login: () => json({ user: { id: "u1" } }), handoffAllowed: true });
    const user = userEvent.setup();

    render(<LoginForm />, { wrapper });
    await waitFor(() => expect(screen.getByRole("button", { name: "Entrar" })).toBeEnabled());
    await user.type(screen.getByLabelText(/E-mail/), "ana@exemplo.com");
    await user.type(screen.getByLabelText(/Senha/), "segredo123");
    await user.click(screen.getByRole("button", { name: "Entrar" }));

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/projects/abc"));
  });
});
