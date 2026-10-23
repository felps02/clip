// @vitest-environment node
import { afterEach, describe, expect, it, vi } from "vitest";
import { buildUrl, currentHostname, getApiBaseUrl } from "@/lib/api/client";

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("getApiBaseUrl during SSR (no window)", () => {
  it("replaces {host} with localhost", () => {
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "http://{host}:5080");
    expect(typeof window).toBe("undefined");
    expect(currentHostname()).toBe("localhost");
    expect(getApiBaseUrl()).toBe("http://localhost:5080");
    expect(buildUrl("/api/plans")).toBe("http://localhost:5080/api/plans");
  });

  it("uses http://localhost:5080 when the variable is unset", () => {
    vi.stubEnv("NEXT_PUBLIC_API_BASE_URL", "");
    expect(getApiBaseUrl()).toBe("http://localhost:5080");
  });
});
