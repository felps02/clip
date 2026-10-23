import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { api, buildUrl, DEFAULT_API_BASE_URL, getApiBaseUrl, resolveApiBaseUrl } from "@/lib/api/client";
import { clipsArchiveUrl } from "@/lib/api/projects";

describe("resolveApiBaseUrl", () => {
  it("replaces every {host} token with the page's host name", () => {
    expect(resolveApiBaseUrl("http://{host}:5080", "10.0.0.57")).toBe("http://10.0.0.57:5080");
    expect(resolveApiBaseUrl("http://{host}:5080", "localhost")).toBe("http://localhost:5080");
    expect(resolveApiBaseUrl("https://api.{host}", "example.com")).toBe("https://api.example.com");
    expect(resolveApiBaseUrl("http://{host}:5080/{host}", "box")).toBe("http://box:5080/box");
  });

  it("defaults to the API on port 5080 of the same host when unset or blank", () => {
    expect(DEFAULT_API_BASE_URL).toBe("http://{host}:5080");
    expect(resolveApiBaseUrl(undefined, "10.0.0.57")).toBe("http://10.0.0.57:5080");
    expect(resolveApiBaseUrl("", "localhost")).toBe("http://localhost:5080");
    expect(resolveApiBaseUrl("   ", "192.168.0.10")).toBe("http://192.168.0.10:5080");
  });

  it("keeps a fixed URL as is (without trailing slashes)", () => {
    expect(resolveApiBaseUrl("https://api.example.com/", "10.0.0.57")).toBe("https://api.example.com");
    expect(resolveApiBaseUrl("http://localhost:5080//", "10.0.0.57")).toBe("http://localhost:5080");
  });

  it("brackets IPv6 literals and falls back to localhost for an empty host name", () => {
    expect(resolveApiBaseUrl("http://{host}:5080", "::1")).toBe("http://[::1]:5080");
    expect(resolveApiBaseUrl("http://{host}:5080", "[::1]")).toBe("http://[::1]:5080");
    expect(resolveApiBaseUrl("http://{host}:5080", "")).toBe("http://localhost:5080");
  });
});

describe("getApiBaseUrl in the browser", () => {
  const fetchMock = vi.fn<typeof fetch>();

  beforeEach(() => {
    fetchMock.mockReset();
    fetchMock.mockResolvedValue(new Response(JSON.stringify({ items: [] }), { status: 200, headers: { "content-type": "application/json" } }));
    vi.stubGlobal("fetch", fetchMock);
  });

  afterEach(() => {
    vi.unstubAllEnvs();
    vi.unstubAllGlobals();
  });

  it("uses window.location.hostname for {host}", () => {
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "http://{host}:5080");
    expect(window.location.hostname).toBe("localhost");
    expect(getApiBaseUrl()).toBe("http://localhost:5080");
  });

  it("uses the default (same host, port 5080) when the variable is unset", () => {
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "");
    expect(getApiBaseUrl()).toBe(`http://${window.location.hostname}:5080`);
  });

  it("is what requests, the plans fetch and the archive link use", async () => {
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "http://{host}:5080/");
    expect(buildUrl("/api/plans")).toBe("http://localhost:5080/api/plans");
    expect(clipsArchiveUrl("p 1")).toBe("http://localhost:5080/api/projects/p%201/clips/archive");

    await api.get("/api/plans");
    expect(fetchMock.mock.calls[0]?.[0]).toBe("http://localhost:5080/api/plans");
  });
});
