import { NextResponse, type NextRequest } from "next/server";
import { ACCESS_COOKIE, isAppRoute, resolveAuthRedirect } from "@/lib/auth-guard";

/**
 * Optimistic auth guard (rules in `src/lib/auth-guard.ts`). App routes requested without the
 * API's `cf_access` cookie go to `/login?next=…`; `NEXT_PUBLIC_COOKIE_GUARD=false` disables the
 * redirect for deployments where the web host never sees that cookie (API on another host without
 * `Auth__CookieDomain`), leaving authentication to the client-side guard in `AppShell`.
 *
 * Guest pages are never redirected here: the cookie proves nothing, so `/login` and `/register`
 * restore the session with `POST /api/auth/refresh` and, before handing off to the app, ask
 * `/auth/handoff` whether this guard would let them through — a hand-off that would bounce is
 * never attempted, so the two pages cannot ping-pong.
 */
export { isAppRoute, resolveAuthRedirect };

export function proxy(request: NextRequest) {
  const { pathname, search } = request.nextUrl;
  const redirect = resolveAuthRedirect(pathname, search, request.cookies.has(ACCESS_COOKIE));
  if (redirect) return NextResponse.redirect(new URL(redirect, request.url));
  return NextResponse.next();
}

export const config = {
  matcher: [
    "/dashboard/:path*",
    "/new/:path*",
    "/projects/:path*",
    "/library/:path*",
    "/clips/:path*",
    "/live/:path*",
    "/billing/:path*",
    "/settings/:path*",
  ],
};
