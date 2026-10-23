import { cookies } from "next/headers";
import type { NextRequest } from "next/server";
import { ACCESS_COOKIE, handoffVerdict } from "@/lib/auth-guard";

/**
 * `GET /auth/handoff?next=/dashboard` → `{ guard, allowed }`.
 *
 * Same-origin probe for the auth pages: after `POST /api/auth/refresh` (or a login) succeeded
 * against the API, it tells whether this web host actually receives the access cookie, i.e.
 * whether the proxy guard would let a navigation to `next` through. The pages only navigate when
 * it does, so a session the web host cannot see never turns into a login ↔ app redirect loop.
 */
export async function GET(request: NextRequest) {
  const jar = await cookies();
  const verdict = handoffVerdict(request.nextUrl.searchParams.get("next"), jar.has(ACCESS_COOKIE));
  return Response.json(verdict, { headers: { "Cache-Control": "no-store" } });
}
