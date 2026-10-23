import Link from "next/link";
import { Suspense, type ReactNode } from "react";
import { BrandMark } from "@/components/brand-mark";
import { brand } from "@/config/brand";

export default function AuthLayout({ children }: { children: ReactNode }) {
  return (
    <div className="relative flex min-h-dvh flex-col bg-canvas">
      <div className="pointer-events-none absolute inset-0 surface-grid opacity-40 [mask-image:radial-gradient(60%_50%_at_50%_0%,black,transparent)]" />
      <header className="relative z-10 flex h-16 items-center justify-between px-4 sm:px-8">
        <BrandMark />
        <Link href="/" className="text-sm text-fg-muted hover:text-fg">
          Voltar ao site
        </Link>
      </header>
      <main className="relative z-10 flex flex-1 items-center justify-center px-4 py-10">
        <div className="w-full max-w-md">
          <Suspense fallback={<div className="h-96 rounded-lg skeleton-shimmer" aria-busy="true" />}>{children}</Suspense>
        </div>
      </main>
      <footer className="relative z-10 px-4 py-6 text-center text-xs text-fg-subtle">
        {brand.name} ·{" "}
        <Link href="/terms" className="hover:text-fg">
          Termos
        </Link>{" "}
        ·{" "}
        <Link href="/privacy" className="hover:text-fg">
          Privacidade
        </Link>
      </footer>
    </div>
  );
}
