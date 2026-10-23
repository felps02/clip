import Link from "next/link";
import { BrandMark } from "@/components/brand-mark";
import { buttonVariants } from "@/components/ui/button";
import { brand, resolveLegal } from "@/config/brand";

const NAV = [
  { href: "/#como-funciona", label: "Como funciona" },
  { href: "/#planos", label: "Planos" },
  { href: "/#faq", label: "FAQ" },
];

export function MarketingHeader() {
  return (
    <header className="sticky top-0 z-30 border-b border-line/80 bg-canvas/80 backdrop-blur-md">
      {/* Below md the section links wrap into a second row instead of disappearing. */}
      <div className="mx-auto flex max-w-7xl flex-wrap items-center justify-between gap-x-4 px-4 sm:px-6 lg:px-8">
        <div className="flex h-16 items-center">
          <BrandMark />
        </div>
        <nav
          aria-label="Navegação do site"
          className="order-last flex w-full items-center gap-5 overflow-x-auto pb-2.5 md:order-none md:w-auto md:gap-6 md:pb-0"
        >
          {NAV.map((item) => (
            <Link key={item.href} href={item.href} className="shrink-0 text-sm text-fg-muted transition-colors hover:text-fg">
              {item.label}
            </Link>
          ))}
        </nav>
        <div className="flex h-16 items-center gap-2">
          <Link href="/login" className={buttonVariants({ variant: "ghost", size: "sm" })}>
            Entrar
          </Link>
          <Link href="/register" className={buttonVariants({ size: "sm" })}>
            Criar conta
          </Link>
        </div>
      </div>
    </header>
  );
}

export function MarketingFooter() {
  const legal = resolveLegal();
  return (
    <footer className="border-t border-line bg-surface/40">
      <div className="mx-auto flex max-w-7xl flex-col gap-6 px-4 py-10 sm:flex-row sm:items-center sm:justify-between sm:px-6 lg:px-8">
        <div>
          <BrandMark />
          <p className="mt-2 max-w-sm text-sm text-fg-muted">{brand.shortDescription}</p>
        </div>
        <nav aria-label="Links legais" className="flex flex-wrap gap-x-6 gap-y-2 text-sm text-fg-muted">
          <Link href="/terms" className="hover:text-fg">
            Termos de uso
          </Link>
          <Link href="/privacy" className="hover:text-fg">
            Política de privacidade
          </Link>
          {brand.supportEmail ? (
            <a href={`mailto:${brand.supportEmail}`} className="hover:text-fg">
              Suporte
            </a>
          ) : null}
        </nav>
      </div>
      <div className="border-t border-line px-4 py-4 text-center text-xs text-fg-subtle">
        {legal ? `${brand.name} é operado por ${legal.companyName} (CNPJ ${legal.cnpj}).` : `${brand.name}.`} Todos os direitos reservados.
      </div>
    </footer>
  );
}
