"use client";

import { usePathname, useRouter } from "next/navigation";
import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Sidebar } from "@/components/layout/sidebar";
import { Topbar } from "@/components/layout/topbar";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { clearSessionHandoff } from "@/features/auth/session-handoff";
import { logout } from "@/lib/api/auth";
import { describeError, isApiError, onUnauthorized } from "@/lib/api/client";
import { useLogout, useMe } from "@/lib/api/hooks/use-auth";
import { isCookieGuardEnabled } from "@/lib/auth-guard";
import { setDisplayTimeZone } from "@/lib/format";
import { loginUrlFor } from "@/lib/utils";

/** Where "Sair" lands. The login page skips its session probe for this URL: the cookies are gone. */
export const SIGNED_OUT_LOGIN_URL = "/login?signedOut=1";

function SessionCheck({ gone }: { gone: boolean }) {
  return (
    <div role="status" aria-busy={!gone} className="space-y-4">
      <p className="text-sm text-fg-muted">{gone ? "Sua sessão terminou. Redirecionando para o login…" : "Verificando a sua sessão…"}</p>
      <Skeleton className="h-8 w-48" />
      <Skeleton className="h-28 w-full" />
    </div>
  );
}

/**
 * Authenticated application frame and the client-side auth guard.
 *
 * With the cookie guard on (default), the proxy already redirected guests without the access
 * cookie and the page renders right away. With `NEXT_PUBLIC_COOKIE_GUARD=false` nothing has
 * checked the session yet, so the content waits for the first answer of `GET /api/auth/me`.
 * Only that first confirmation is waited for: a later refetch that fails (network blip, 5xx)
 * keeps the page mounted, with its form state and uploads, under a retryable alert.
 *
 * Sessions that are gone for good (401 after the refresh attempt) are signed out: the API is
 * asked to clear both HttpOnly cookies first (a stale `cf_access` would otherwise keep looking
 * like a session on the next navigation) and then the user lands on the login page with a `next`
 * back to where they were. Concurrent 401s sign out only once.
 *
 * The explicit "Sair" lives here too: the person only leaves once the API confirmed that the
 * cookies are cleared. A failed logout keeps the session, the cache and the page, with a
 * persistent alert, because /login would just restore the still-valid session.
 */
export function AppShell({ children }: { children: ReactNode }) {
  const router = useRouter();
  const pathname = usePathname();
  const me = useMe();
  const signOutRequest = useLogout();
  const [sidebarOpen, setSidebarOpen] = useState(false);
  // Last failed "Sair": kept while a retry runs so the alert (and its spinner) stays in place.
  const [logoutFailure, setLogoutFailure] = useState<unknown>(null);
  const signingOut = useRef(false);
  const cookieGuard = isCookieGuardEnabled();

  const signOut = useCallback(
    (next: string) => {
      if (signingOut.current) return;
      signingOut.current = true;
      clearSessionHandoff();
      void logout()
        .catch(() => undefined)
        .finally(() => router.replace(loginUrlFor(next)));
    },
    [router],
  );

  useEffect(() => {
    return onUnauthorized(() => signOut(`${window.location.pathname}${window.location.search}`));
  }, [signOut]);

  useEffect(() => {
    if (me.error && isApiError(me.error) && me.error.status === 401) signOut(pathname);
  }, [me.error, pathname, signOut]);

  // The hand-off from the auth pages is complete: the proxy let us in and the session answers.
  useEffect(() => {
    if (me.isSuccess) clearSessionHandoff();
  }, [me.isSuccess]);

  const handleLogout = () => {
    if (signingOut.current) return;
    // Set before the request so a 401 seen meanwhile does not start a second sign-out.
    signingOut.current = true;
    signOutRequest.mutate(undefined, {
      onSuccess: () => {
        setLogoutFailure(null);
        router.replace(SIGNED_OUT_LOGIN_URL);
      },
      onError: (error) => {
        // The cookies are still there: stay in the app (alert below) and allow another attempt.
        signingOut.current = false;
        setLogoutFailure(error);
      },
    });
  };

  const sessionGone = !!me.error && isApiError(me.error) && me.error.status === 401;
  const sessionError = me.error && !sessionGone ? me.error : null;

  // Client-side guard: without the proxy redirect, nothing is shown before the API confirmed the
  // session once. A failed background refetch keeps `data` (TanStack Query only flips `status` to
  // "error"), so it never unmounts the page; a final 401 still signs out through the effect above.
  const holdContent = !cookieGuard && me.data === undefined;

  // Absolute dates follow the profile's time zone once the session is known (idempotent; the server
  // render and the hydration pass have no session yet and both use the default zone).
  setDisplayTimeZone(me.data?.profile.timezone);

  return (
    <div className="flex min-h-dvh flex-col bg-canvas lg:flex-row">
      <a
        href="#conteudo"
        className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-accent focus:px-3 focus:py-2 focus:text-accent-fg"
      >
        Pular para o conteúdo
      </a>
      <Sidebar open={sidebarOpen} onOpenChange={setSidebarOpen} me={me.data} />
      <div className="flex min-h-dvh min-w-0 flex-1 flex-col">
        <Topbar
          onMenuClick={() => setSidebarOpen(true)}
          me={me.data}
          loading={me.isPending}
          onLogout={handleLogout}
          loggingOut={signOutRequest.isPending}
        />
        <main id="conteudo" className="mx-auto w-full max-w-7xl flex-1 px-4 py-6 sm:px-6 lg:px-8 lg:py-8">
          {logoutFailure ? (
            <Alert
              variant="danger"
              className="mb-6"
              title="Não foi possível encerrar a sessão — tente novamente"
              action={
                <div className="flex flex-wrap gap-2">
                  <Button size="sm" variant="secondary" onClick={handleLogout} loading={signOutRequest.isPending}>
                    Tentar novamente
                  </Button>
                  <Button size="sm" variant="ghost" onClick={() => setLogoutFailure(null)} disabled={signOutRequest.isPending}>
                    Fechar
                  </Button>
                </div>
              }
            >
              {describeError(logoutFailure)} A sessão continua ativa neste navegador até que a saída seja concluída.
            </Alert>
          ) : null}
          {sessionError ? (
            <Alert
              variant="warning"
              className="mb-6"
              title="Não foi possível carregar a sua conta"
              action={
                <Button size="sm" variant="secondary" onClick={() => me.refetch()} loading={me.isFetching}>
                  Tentar novamente
                </Button>
              }
            >
              {describeError(sessionError)}
            </Alert>
          ) : null}
          {holdContent ? sessionError ? null : <SessionCheck gone={sessionGone} /> : children}
        </main>
      </div>
    </div>
  );
}

export function AppShellSkeleton() {
  return (
    <div role="status" className="flex min-h-dvh flex-col bg-canvas lg:flex-row" aria-busy="true" aria-label="Carregando aplicação">
      <div className="hidden w-64 shrink-0 border-r border-line bg-surface lg:block" />
      <div className="flex min-w-0 flex-1 flex-col">
        <div className="h-16 border-b border-line bg-canvas/80" />
        <div className="mx-auto w-full max-w-7xl flex-1 px-4 py-8 sm:px-6 lg:px-8">
          <div className="h-8 w-48 rounded-md skeleton-shimmer" />
          <div className="mt-6 grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
            {Array.from({ length: 4 }).map((_, index) => (
              <div key={index} className="h-28 rounded-lg skeleton-shimmer" />
            ))}
          </div>
        </div>
      </div>
    </div>
  );
}
