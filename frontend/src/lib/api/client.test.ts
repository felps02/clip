import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, api, describeError, isCredentialEndpoint, normalizeFieldErrors, problemToApiError, request } from "@/lib/api/client";

function jsonResponse(status: number, body: unknown, contentType = "application/json"): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": contentType } });
}

function problem(status: number, code: string, extra: Record<string, unknown> = {}): Response {
  return jsonResponse(status, { type: "about:blank", title: "Erro", status, code, ...extra }, "application/problem+json");
}

const fetchMock = vi.fn<typeof fetch>();

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("ProblemDetails parsing", () => {
  it("maps a problem+json body to ApiError with code, title, detail and normalised field errors", async () => {
    fetchMock.mockResolvedValueOnce(
      problem(422, "validation_failed", {
        title: "Dados inválidos",
        detail: "Alguns campos precisam de atenção.",
        errors: { Email: ["E-mail já cadastrado"], "Settings.MinClipSeconds": "Mínimo 10", Password: [] },
      }),
    );

    await expect(api.post("/api/auth/register", { email: "a@b.c" })).rejects.toSatisfy((error: unknown) => {
      expect(error).toBeInstanceOf(ApiError);
      const apiError = error as ApiError;
      expect(apiError.status).toBe(422);
      expect(apiError.code).toBe("validation_failed");
      expect(apiError.title).toBe("Dados inválidos");
      expect(apiError.detail).toBe("Alguns campos precisam de atenção.");
      expect(apiError.errors).toEqual({ email: ["E-mail já cadastrado"], "settings.minClipSeconds": ["Mínimo 10"] });
      expect(apiError.fieldMessages()).toEqual({ email: "E-mail já cadastrado", "settings.minClipSeconds": "Mínimo 10" });
      return true;
    });
  });

  it("falls back to status-based code and pt-BR title when the body is not a problem document (never shows the raw body)", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("<html><body><h1>502 Bad Gateway</h1></body></html>", { status: 502, headers: { "content-type": "text/html" } }),
    );

    const error = await api.get("/api/projects").catch((caught: unknown) => caught);

    expect(error).toMatchObject({ status: 502, code: "unavailable", title: "Servidor indisponível" });
    expect((error as { detail?: string }).detail).toBeUndefined();
    expect(describeError(error)).toBe("Servidor indisponível");
  });

  it("does not surface arbitrary runtime error messages", () => {
    expect(describeError(new TypeError("Cannot read properties of undefined (reading 'x')"))).toBe("Algo deu errado. Tente novamente.");
  });

  it("wraps network failures as ApiError with status 0", async () => {
    fetchMock.mockRejectedValueOnce(new TypeError("Failed to fetch"));

    await expect(api.get("/api/projects")).rejects.toMatchObject({ status: 0, code: "network_error" });
  });

  it("normalises field error keys and values", () => {
    expect(normalizeFieldErrors({ DisplayName: ["x"], nested: "y" })).toEqual({ displayName: ["x"], nested: ["y"] });
    expect(normalizeFieldErrors(undefined)).toBeUndefined();
    expect(problemToApiError(409, { code: "plan_limit" }).code).toBe("plan_limit");
  });
});

describe("JSON helpers", () => {
  it("sends JSON bodies with credentials and returns parsed data", async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(201, { project: { id: "p1" } }));

    const result = await api.post<{ project: { id: string } }>("/api/projects", { title: "x" }, { headers: { "Idempotency-Key": "k1" } });

    expect(result.project.id).toBe("p1");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("http://api.test/api/projects");
    expect(init?.credentials).toBe("include");
    expect(init?.method).toBe("POST");
    expect(init?.body).toBe(JSON.stringify({ title: "x" }));
    expect((init?.headers as Record<string, string>)["Content-Type"]).toBe("application/json");
    expect((init?.headers as Record<string, string>)["Idempotency-Key"]).toBe("k1");
  });

  it("serialises query strings and returns undefined for 204", async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }));

    const result = await request<void>("/api/clips", { query: { page: 2, q: "", status: undefined, projectId: "abc" } });

    expect(result).toBeUndefined();
    expect(fetchMock.mock.calls[0][0]).toBe("http://api.test/api/clips?page=2&projectId=abc");
  });
});

describe("refresh on 401", () => {
  it("refreshes once and retries the original request", async () => {
    fetchMock
      .mockResolvedValueOnce(problem(401, "unauthorized"))
      .mockResolvedValueOnce(jsonResponse(200, { user: { id: "u1" } }))
      .mockResolvedValueOnce(jsonResponse(200, { items: [] }));

    const result = await api.get<{ items: unknown[] }>("/api/projects");

    expect(result.items).toEqual([]);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(fetchMock.mock.calls[0][0]).toBe("http://api.test/api/projects");
    expect(fetchMock.mock.calls[1][0]).toBe("http://api.test/api/auth/refresh");
    expect(fetchMock.mock.calls[1][1]?.method).toBe("POST");
    expect(fetchMock.mock.calls[2][0]).toBe("http://api.test/api/projects");
  });

  it("gives up after one failed refresh and surfaces the 401", async () => {
    fetchMock.mockResolvedValueOnce(problem(401, "unauthorized")).mockResolvedValueOnce(problem(401, "unauthorized"));

    await expect(api.get("/api/projects")).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("de-duplicates concurrent refreshes", async () => {
    let resolveRefresh: (value: Response) => void = () => undefined;
    const refreshPromise = new Promise<Response>((resolve) => {
      resolveRefresh = resolve;
    });
    fetchMock.mockImplementation((input) => {
      const url = String(input);
      if (url.endsWith("/api/auth/refresh")) return refreshPromise;
      if (fetchMock.mock.calls.filter((call) => String(call[0]) === url).length <= 1) {
        return Promise.resolve(problem(401, "unauthorized"));
      }
      return Promise.resolve(jsonResponse(200, { ok: url }));
    });

    const pending = Promise.all([api.get("/api/a"), api.get("/api/b"), api.get("/api/c")]);
    await Promise.resolve();
    await Promise.resolve();
    resolveRefresh(jsonResponse(200, { user: {} }));
    const results = await pending;

    expect(results).toEqual([{ ok: "http://api.test/api/a" }, { ok: "http://api.test/api/b" }, { ok: "http://api.test/api/c" }]);
    const refreshCalls = fetchMock.mock.calls.filter((call) => String(call[0]).endsWith("/api/auth/refresh"));
    expect(refreshCalls).toHaveLength(1);
  });

  it("never refreshes for credential endpoints", async () => {
    fetchMock.mockResolvedValueOnce(problem(401, "invalid_credentials"));

    await expect(api.post("/api/auth/login", { email: "a@b.c", password: "x" })).rejects.toMatchObject({ code: "invalid_credentials" });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(isCredentialEndpoint("/api/auth/login")).toBe(true);
    expect(isCredentialEndpoint("/api/auth/refresh")).toBe(true);
    expect(isCredentialEndpoint("/api/auth/me")).toBe(false);
    expect(isCredentialEndpoint("/api/projects")).toBe(false);
  });

  it("respects skipAuthRefresh", async () => {
    fetchMock.mockResolvedValueOnce(problem(401, "unauthorized"));

    await expect(request("/api/plans", { skipAuthRefresh: true })).rejects.toMatchObject({ status: 401 });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
