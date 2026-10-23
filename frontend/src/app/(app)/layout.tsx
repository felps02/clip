import { Suspense, type ReactNode } from "react";
import { AppShell, AppShellSkeleton } from "@/components/layout/app-shell";

/**
 * Everything under the app is request-time (session cookie, polling, search params),
 * so the client tree sits behind a Suspense boundary and the shell skeleton is the static part.
 */
export default function AppLayout({ children }: { children: ReactNode }) {
  return (
    <Suspense fallback={<AppShellSkeleton />}>
      <AppShell>{children}</AppShell>
    </Suspense>
  );
}
