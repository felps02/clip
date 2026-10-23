// @vitest-environment node
import { NextRequest } from "next/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { handoffVerdict, isCookieGuardEnabled } from "@/lib/auth-guard";
import { isAppRoute, proxy, resolveAuthRedirect } from "@/proxy";

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("resolveAuthRedirect", () => {
  it("sends app routes without the access cookie to /login with a next parameter", () => {
    expect(resolveAuthRedirect("/projects/abc", "?tab=clips", false)).toBe("/login?next=%2Fprojects%2Fabc%3Ftab%3Dclips");
    expect(resolveAuthRedirect("/dashboard", "", false)).toBe("/login?next=%2Fdashboard");
  });

  it("lets app routes through when the cookie is present and never touches guest or public routes", () => {
    expect(resolveAuthRedirect("/dashboard", "", true)).toBeNull();
    expect(resolveAuthRedirect("/login", "?next=%2Fdashboard", true)).toBeNull();
    expect(resolveAuthRedirect("/login", "", false)).toBeNull();
    expect(resolveAuthRedirect("/register", "", true)).toBeNull();
    expect(resolveAuthRedirect("/", "", false)).toBeNull();
    expect(isAppRoute("/projectsx")).toBe(false);
  });

  it("never redirects when the cookie guard is disabled", () => {
    expect(resolveAuthRedirect("/dashboard", "", false, false)).toBeNull();
  });
});

describe("isCookieGuardEnabled", () => {
  it("is on by default and only turned off explicitly", () => {
    expect(isCookieGuardEnabled(undefined)).toBe(true);
    expect(isCookieGuardEnabled("")).toBe(true);
    expect(isCookieGuardEnabled("true")).toBe(true);
    expect(isCookieGuardEnabled("false")).toBe(false);
    expect(isCookieGuardEnabled(" FALSE ")).toBe(false);
    expect(isCookieGuardEnabled("0")).toBe(false);
  });
});

describe("handoffVerdict", () => {
  it("allows the hand-off only when the proxy would let `next` through", () => {
    expect(handoffVerdict("/projects/abc?tab=clips", true, true)).toEqual({ guard: true, allowed: true });
    expect(handoffVerdict("/projects/abc?tab=clips", false, true)).toEqual({ guard: true, allowed: false });
    // Unsafe or missing targets fall back to /dashboard, an app route.
    expect(handoffVerdict("https://evil.example/x", false, true)).toEqual({ guard: true, allowed: false });
    expect(handoffVerdict(null, true, true)).toEqual({ guard: true, allowed: true });
  });

  it("judges a crafted off-site `next` as the /dashboard fallback the auth pages actually navigate to", () => {
    // As written none of these is an app route (the first two even resolve to another host), so judging
    // them verbatim would answer "allowed" for a navigation the page never makes.
    for (const next of ["/\\evil.example", "/\t/evil.example", "/..//evil.example"]) {
      expect(handoffVerdict(next, false, true)).toEqual({ guard: true, allowed: false });
      expect(handoffVerdict(next, true, true)).toEqual({ guard: true, allowed: true });
    }
  });

  it("always allows when the guard is disabled (the client-side guard handles auth)", () => {
    expect(handoffVerdict("/dashboard", false, false)).toEqual({ guard: false, allowed: true });
  });
});

describe("proxy", () => {
  it("redirects a deep link without the cookie", () => {
    const response = proxy(new NextRequest("http://web.test/projects/abc?x=1"));
    expect(response.status).toBe(307);
    expect(response.headers.get("location")).toBe("http://web.test/login?next=%2Fprojects%2Fabc%3Fx%3D1");
  });

  it("does not bounce /login back to the app when a (possibly stale) access cookie exists", () => {
    const request = new NextRequest("http://web.test/login?next=%2Fdashboard", { headers: { cookie: "cf_access=stale" } });
    const response = proxy(request);
    expect(response.status).toBe(200);
    expect(response.headers.get("location")).toBeNull();
    expect(response.headers.get("x-middleware-next")).toBe("1");
  });

  it("does not redirect at all with NEXT_PUBLIC_COOKIE_GUARD=false", () => {
    vi.stubEnv("NEXT_PUBLIC_COOKIE_GUARD", "false");
    const response = proxy(new NextRequest("http://web.test/dashboard"));
    expect(response.headers.get("location")).toBeNull();
    expect(response.headers.get("x-middleware-next")).toBe("1");
  });
});
