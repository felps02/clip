"use client";

import { Button } from "@/components/ui/button";

/**
 * Route error boundary. The raw `error.message` (often an English runtime message) is never
 * shown; the digest identifies the server-side log entry for support.
 */
export default function GlobalError({ error, reset }: { error: Error & { digest?: string }; reset: () => void }) {
  return (
    <div className="flex flex-1 flex-col items-center justify-center px-4 py-20 text-center">
      <p className="text-xs font-medium uppercase tracking-widest text-danger">Algo deu errado</p>
      <h1 className="mt-2 text-2xl font-semibold tracking-tight">Não foi possível carregar esta página</h1>
      <p className="mt-3 max-w-md text-sm text-fg-muted">
        {error.digest
          ? "Ocorreu um erro inesperado. Tente novamente; se o problema continuar, informe o código abaixo ao suporte."
          : "Ocorreu um erro inesperado. Tente novamente em instantes."}
        {error.digest ? <span className="mt-1 block font-mono text-xs text-fg-subtle">ref. {error.digest}</span> : null}
      </p>
      <Button className="mt-8" onClick={reset}>
        Tentar novamente
      </Button>
    </div>
  );
}
