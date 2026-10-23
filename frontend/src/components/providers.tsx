"use client";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { useState, type ReactNode } from "react";
import { Toaster } from "sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { isApiError } from "@/lib/api/client";

function makeQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        staleTime: 15_000,
        refetchOnWindowFocus: false,
        retry: (failureCount, error) => {
          // Client errors (4xx) are final; network/5xx errors get one retry.
          if (isApiError(error) && error.status > 0 && error.status < 500) return false;
          return failureCount < 1;
        },
      },
      mutations: {
        retry: false,
      },
    },
  });
}

export function Providers({ children }: { children: ReactNode }) {
  // One client per browser session; a fresh one per server render.
  const [queryClient] = useState(makeQueryClient);
  return (
    <QueryClientProvider client={queryClient}>
      <TooltipProvider delayDuration={250}>{children}</TooltipProvider>
      <Toaster
        theme="dark"
        position="bottom-right"
        closeButton
        richColors
        toastOptions={{
          classNames: {
            toast: "!bg-elevated !border-line !text-fg !shadow-pop",
            description: "!text-fg-muted",
          },
        }}
      />
    </QueryClientProvider>
  );
}
