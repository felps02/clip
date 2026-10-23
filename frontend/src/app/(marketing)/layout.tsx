import type { ReactNode } from "react";
import { MarketingFooter, MarketingHeader } from "@/components/marketing/site-chrome";

export default function MarketingLayout({ children }: { children: ReactNode }) {
  return (
    <div className="flex min-h-dvh flex-col">
      <a
        href="#conteudo"
        className="sr-only focus:not-sr-only focus:absolute focus:left-4 focus:top-4 focus:z-50 focus:rounded-md focus:bg-accent focus:px-3 focus:py-2 focus:text-accent-fg"
      >
        Pular para o conteúdo
      </a>
      <MarketingHeader />
      <main id="conteudo" className="flex-1">
        {children}
      </main>
      <MarketingFooter />
    </div>
  );
}
