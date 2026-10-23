import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { AppShell, SIGNED_OUT_LOGIN_URL } from "@/components/layout/app-shell";
import { queryKeys } from "@/lib/api/query-keys";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  // One stable router object per module, like Next's real `useRouter()`.
  return { replace, pathname: "/projects/abc", router: { replace, push: vi.fn(), prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  usePathname: () => navigation.pathname,
}));
vi.mock("@/components/layout/sidebar", () => ({ Sidebar: () => null }));
// The real Topbar renders "Sair" inside a Radix menu; a plain button is enough to drive the AppShell handler.
vi.mock("@/components/layout/topbar", () => ({
  Topbar: ({ onLogout, loggingOut }: { onLogout: () => void; loggingOut?: boolean }) => (
    <button type="button" onClick={onLogout} disabled={loggingOut}>
      Sair
    </button>
  ),
}));

const fetchMock = vi.fn<typeof fetch>();
let client: QueryClient;

function problem(status: number, code: string): Response {
  return new Response(JSON.stringify({ title: "Erro", status, code }), { status, headers: { "content-type": "application/problem+json" } });
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

const me = {
  user: { id: "u1", email: "ana@exemplo.com", displayName: "Ana", emailConfirmed: true, createdAt: "2026-10-01T00:00:00Z" },
  profile: { displayName: "Ana", locale: "pt-BR" },
  subscription: { planCode: "free", planName: "Gratuito", status: "active" },
  limits: {},
};

function wrapper({ children }: { children: ReactNode }) {
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function renderShell() {
  return render(
    <AppShell>
      <p>conteúdo</p>
    </AppShell>,
    { wrapper },
  );
}

beforeEach(() => {
  client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  fetchMock.mockReset();
  navigation.replace.mockReset();
  vi.stubGlobal("fetch", fetchMock);
  window.history.replaceState({}, "", "/projects/abc?tab=clips");
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
});

describe("AppShell session handling", () => {
  it("asks the API to clear the cookies before redirecting when the session is gone (401 after refresh)", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      return Promise.resolve(problem(401, "unauthorized"));
    });

    renderShell();

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/login?next=%2Fprojects%2Fabc%3Ftab%3Dclips"));
    const urls = fetchMock.mock.calls.map((call) => urlOf(call[0]));
    expect(urls.filter((url) => url.endsWith("/api/auth/logout"))).toHaveLength(1);
    expect(urls.indexOf("http://api.test/api/auth/logout")).toBeGreaterThan(urls.indexOf("http://api.test/api/auth/refresh"));
    const logoutCall = fetchMock.mock.calls.find((call) => urlOf(call[0]).endsWith("/api/auth/logout"));
    expect(logoutCall?.[1]?.method).toBe("POST");
    expect(logoutCall?.[1]?.credentials).toBe("include");
    expect(navigation.replace).toHaveBeenCalledTimes(1);
  });

  it("still redirects (once) when the logout call itself fails", async () => {
    fetchMock.mockImplementation((input) => {
      if (urlOf(input).endsWith("/api/auth/logout")) return Promise.reject(new TypeError("Failed to fetch"));
      return Promise.resolve(problem(401, "unauthorized"));
    });

    renderShell();

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledTimes(1));
    expect(navigation.replace).toHaveBeenCalledWith("/login?next=%2Fprojects%2Fabc%3Ftab%3Dclips");
  });

  it("keeps the person in the app with the session and the cache when POST /api/auth/logout fails", async () => {
    let logoutCalls = 0;
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/auth/me")) return Promise.resolve(json(me));
      if (url.endsWith("/api/auth/logout")) {
        logoutCalls += 1;
        return Promise.resolve(problem(503, "unavailable"));
      }
      return Promise.resolve(problem(404, "not_found"));
    });
    const user = userEvent.setup();

    renderShell();
    await waitFor(() => expect(client.getQueryData(queryKeys.auth.me)).toBeDefined());
    await user.click(screen.getByRole("button", { name: "Sair" }));

    expect(await screen.findByText("Não foi possível encerrar a sessão — tente novamente")).toBeInTheDocument();
    expect(screen.getByText(/A sessão continua ativa neste navegador/)).toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();
    expect(screen.getByText("conteúdo")).toBeInTheDocument();
    // Nothing pretends the person signed out: the cached session is still there.
    expect(client.getQueryData(queryKeys.auth.me)).toEqual(me);
    expect(logoutCalls).toBe(1);

    // Retrying is possible and, once the API confirms, the person leaves for the login page.
    fetchMock.mockImplementation((input) => {
      if (urlOf(input).endsWith("/api/auth/logout")) {
        logoutCalls += 1;
        return Promise.resolve(new Response(null, { status: 204 }));
      }
      return Promise.resolve(problem(401, "unauthorized"));
    });
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith(SIGNED_OUT_LOGIN_URL));
    expect(logoutCalls).toBe(2);
    expect(navigation.replace).toHaveBeenCalledTimes(1);
  });

  it("goes to the login page (which then skips the session probe) only after the API cleared the cookies", async () => {
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/auth/me")) return Promise.resolve(json(me));
      if (url.endsWith("/api/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      return Promise.resolve(problem(401, "unauthorized"));
    });
    const user = userEvent.setup();

    renderShell();
    await waitFor(() => expect(client.getQueryData(queryKeys.auth.me)).toBeDefined());
    await user.click(screen.getByRole("button", { name: "Sair" }));

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/login?signedOut=1"));
    expect(navigation.replace).toHaveBeenCalledTimes(1);
  });

  it("shows a retryable alert instead of signing out when /me fails for another reason", async () => {
    fetchMock.mockResolvedValue(problem(503, "unavailable"));

    renderShell();

    expect(await screen.findByText("Não foi possível carregar a sua conta")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeInTheDocument();
    expect(screen.getByText("conteúdo")).toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();
    expect(fetchMock.mock.calls.some((call) => urlOf(call[0]).endsWith("/api/auth/logout"))).toBe(false);
  });
});

describe("AppShell as the client-side guard (NEXT_PUBLIC_COOKIE_GUARD=false)", () => {
  it("holds the page until /api/auth/me confirms the session", async () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    const pendingMe: { resolve: (response: Response) => void } = { resolve: () => undefined };
    fetchMock.mockImplementation((input) => {
      if (urlOf(input).endsWith("/api/auth/me")) {
        return new Promise<Response>((resolve) => {
          pendingMe.resolve = resolve;
        });
      }
      return Promise.resolve(problem(404, "not_found"));
    });

    renderShell();

    expect(screen.getByText("Verificando a sua sessão…")).toBeInTheDocument();
    expect(screen.queryByText("conteúdo")).not.toBeInTheDocument();
    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    pendingMe.resolve(json(me));
    expect(await screen.findByText("conteúdo")).toBeInTheDocument();
  });

  it("keeps a confirmed page mounted, with its state, when a later /api/auth/me refetch fails", async () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    let meStatus = 200;
    fetchMock.mockImplementation((input) => {
      if (urlOf(input).endsWith("/api/auth/me")) return Promise.resolve(meStatus === 200 ? json(me) : problem(meStatus, "unavailable"));
      return Promise.resolve(problem(404, "not_found"));
    });
    const user = userEvent.setup();

    render(
      <AppShell>
        <input aria-label="Rascunho" />
      </AppShell>,
      { wrapper },
    );
    const draft = await screen.findByLabelText("Rascunho");
    await user.type(draft, "título em edição");

    // A background refetch (another page's useMe after staleTime, or an auth.me invalidation) hits a 503.
    meStatus = 503;
    await act(() => client.invalidateQueries({ queryKey: queryKeys.auth.me }));

    expect(await screen.findByText("Não foi possível carregar a sua conta")).toBeInTheDocument();
    expect(screen.getByLabelText("Rascunho")).toBe(draft);
    expect(draft).toHaveValue("título em edição");
    expect(screen.queryByText("Verificando a sua sessão…")).not.toBeInTheDocument();
    expect(navigation.replace).not.toHaveBeenCalled();

    // "Tentar novamente" recovers without remounting anything.
    meStatus = 200;
    await user.click(screen.getByRole("button", { name: "Tentar novamente" }));
    await waitFor(() => expect(screen.queryByText("Não foi possível carregar a sua conta")).not.toBeInTheDocument());
    expect(screen.getByLabelText("Rascunho")).toBe(draft);
  });

  it("still signs out when a later /api/auth/me answers 401 for good", async () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    let sessionGone = false;
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      if (url.endsWith("/api/auth/me") && !sessionGone) return Promise.resolve(json(me));
      return Promise.resolve(problem(401, "unauthorized"));
    });

    renderShell();
    expect(await screen.findByText("conteúdo")).toBeInTheDocument();
    sessionGone = true;
    await act(() => client.invalidateQueries({ queryKey: queryKeys.auth.me }));

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledTimes(1));
    expect(navigation.replace.mock.calls[0][0]).toMatch(/^\/login\?next=%2Fprojects%2Fabc/);
    expect(fetchMock.mock.calls.filter((call) => urlOf(call[0]).endsWith("/api/auth/logout"))).toHaveLength(1);
  });

  it("never shows the page to a guest and signs out to /login with next", async () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    fetchMock.mockImplementation((input) => {
      if (urlOf(input).endsWith("/api/auth/logout")) return Promise.resolve(new Response(null, { status: 204 }));
      return Promise.resolve(problem(401, "unauthorized"));
    });

    renderShell();

    await waitFor(() => expect(navigation.replace).toHaveBeenCalledWith("/login?next=%2Fprojects%2Fabc%3Ftab%3Dclips"));
    expect(screen.queryByText("conteúdo")).not.toBeInTheDocument();
  });
});
