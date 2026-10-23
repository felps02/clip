"use client";

import * as DialogPrimitive from "@radix-ui/react-dialog";
import { Clapperboard, CreditCard, FolderOpen, LayoutDashboard, Plus, Radio, Settings, Sparkles, X } from "lucide-react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import type { ComponentType, SVGProps } from "react";
import { BrandMark } from "@/components/brand-mark";
import { Button, buttonVariants } from "@/components/ui/button";
import type { MeResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

interface NavItem {
  href: string;
  label: string;
  icon: ComponentType<SVGProps<SVGSVGElement>>;
  /** Extra path prefixes that keep this item active. */
  also?: string[];
}

export const NAV_ITEMS: NavItem[] = [
  { href: "/dashboard", label: "Dashboard", icon: LayoutDashboard },
  { href: "/new", label: "Novo projeto", icon: Plus },
  { href: "/projects", label: "Projetos", icon: FolderOpen },
  { href: "/library", label: "Biblioteca", icon: Clapperboard, also: ["/clips"] },
  { href: "/live", label: "Lives", icon: Radio },
  { href: "/billing", label: "Assinatura", icon: CreditCard },
  { href: "/settings", label: "Configurações", icon: Settings },
];

export function isNavActive(pathname: string, item: NavItem): boolean {
  const prefixes = [item.href, ...(item.also ?? [])];
  return prefixes.some((prefix) => pathname === prefix || pathname.startsWith(`${prefix}/`));
}

function NavList({ pathname, onNavigate }: { pathname: string; onNavigate?: () => void }) {
  return (
    <nav aria-label="Navegação principal" className="flex-1 px-3 py-4">
      <ul className="space-y-1">
        {NAV_ITEMS.map((item) => {
          const active = isNavActive(pathname, item);
          const Icon = item.icon;
          return (
            <li key={item.href}>
              <Link
                href={item.href}
                onClick={onNavigate}
                aria-current={active ? "page" : undefined}
                className={cn(
                  "group flex items-center gap-3 rounded-md px-3 py-2 text-sm font-medium transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60",
                  active ? "bg-accent/10 text-accent" : "text-fg-muted hover:bg-elevated hover:text-fg",
                )}
              >
                <Icon
                  className={cn("size-4 shrink-0", active ? "text-accent" : "text-fg-subtle group-hover:text-fg")}
                  aria-hidden="true"
                />
                {item.label}
              </Link>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}

function PlanCard({ me }: { me?: MeResponse }) {
  if (!me) return null;
  const isFree = me.subscription.planCode === "free";
  return (
    <div className="mx-3 mb-4 rounded-md border border-line bg-elevated/60 p-3">
      <p className="text-[11px] font-medium uppercase tracking-wide text-fg-subtle">Plano atual</p>
      <p className="mt-0.5 text-sm font-semibold text-fg">{me.subscription.planName}</p>
      <p className="mt-1 text-xs text-fg-muted">{me.limits.monthlyProcessingMinutes} min de processamento/mês</p>
      {isFree ? (
        <Link
          href="/billing"
          className={cn(buttonVariants({ variant: "secondary", size: "sm" }), "mt-3 w-full")}
        >
          <Sparkles className="size-3.5" aria-hidden="true" />
          Ver planos
        </Link>
      ) : null}
    </div>
  );
}

export interface SidebarProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  me?: MeResponse;
}

export function Sidebar({ open, onOpenChange, me }: SidebarProps) {
  const pathname = usePathname();
  return (
    <>
      {/* Desktop: the column (background and border) runs the whole page height, and the nav with the
          plan card inside it is sticky and as tall as the viewport, so it stays in view on long pages
          and scrolls by itself on short screens. */}
      <div data-testid="desktop-sidebar-column" className="hidden w-64 shrink-0 border-r border-line bg-surface lg:block">
        <aside data-testid="desktop-sidebar" className="sticky top-0 z-30 flex h-dvh flex-col overflow-y-auto scrollbar-thin">
          <div className="flex h-16 shrink-0 items-center border-b border-line px-5">
            <BrandMark href="/dashboard" />
          </div>
          <NavList pathname={pathname} />
          <PlanCard me={me} />
        </aside>
      </div>

      {/* Mobile drawer */}
      <DialogPrimitive.Root open={open} onOpenChange={onOpenChange}>
        <DialogPrimitive.Portal>
          <DialogPrimitive.Overlay className="fixed inset-0 z-40 bg-canvas/70 backdrop-blur-sm animate-fade-in lg:hidden" />
          <DialogPrimitive.Content
            className="fixed inset-y-0 left-0 z-50 flex w-[min(18rem,85vw)] flex-col border-r border-line bg-surface shadow-pop focus:outline-none lg:hidden"
            aria-describedby={undefined}
          >
            <DialogPrimitive.Title className="sr-only">Menu de navegação</DialogPrimitive.Title>
            <div className="flex h-16 items-center justify-between border-b border-line px-4">
              <BrandMark href="/dashboard" />
              <DialogPrimitive.Close asChild>
                <Button variant="ghost" size="icon-sm" aria-label="Fechar menu">
                  <X aria-hidden="true" />
                </Button>
              </DialogPrimitive.Close>
            </div>
            <NavList pathname={pathname} onNavigate={() => onOpenChange(false)} />
            <PlanCard me={me} />
          </DialogPrimitive.Content>
        </DialogPrimitive.Portal>
      </DialogPrimitive.Root>
    </>
  );
}
