"use client";

import { ChevronDown, CreditCard, LogOut, Settings } from "lucide-react";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Skeleton } from "@/components/ui/skeleton";
import type { MeResponse } from "@/lib/api/types";

export function initialsOf(name: string | undefined | null): string {
  if (!name) return "?";
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  const first = parts[0][0] ?? "";
  const last = parts.length > 1 ? (parts[parts.length - 1][0] ?? "") : "";
  return `${first}${last}`.toUpperCase();
}

export interface UserMenuProps {
  me?: MeResponse;
  loading?: boolean;
  /** Explicit sign-out. The app shell owns the request so a failure keeps the person in the app. */
  onLogout: () => void;
  loggingOut?: boolean;
}

export function UserMenu({ me, loading, onLogout, loggingOut }: UserMenuProps) {
  if (loading && !me) {
    return <Skeleton className="h-10 w-28 rounded-full" />;
  }

  return (
    <DropdownMenu>
      <DropdownMenuTrigger asChild>
        <Button variant="ghost" className="h-10 gap-2 rounded-full px-1.5 pr-2" aria-label="Menu do usuário">
          <span className="flex size-7 items-center justify-center rounded-full bg-accent/15 text-xs font-semibold text-accent">
            {initialsOf(me?.user.displayName)}
          </span>
          <span className="hidden max-w-32 truncate text-sm sm:inline">{me?.user.displayName ?? "Conta"}</span>
          <ChevronDown className="size-3.5 text-fg-subtle" aria-hidden="true" />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-60">
        <DropdownMenuLabel className="normal-case tracking-normal">
          <span className="block truncate text-sm font-medium text-fg">{me?.user.displayName}</span>
          <span className="block truncate text-xs font-normal text-fg-muted">{me?.user.email}</span>
        </DropdownMenuLabel>
        <DropdownMenuSeparator />
        <DropdownMenuItem asChild>
          <Link href="/settings">
            <Settings aria-hidden="true" />
            Configurações
          </Link>
        </DropdownMenuItem>
        <DropdownMenuItem asChild>
          <Link href="/billing">
            <CreditCard aria-hidden="true" />
            Assinatura
          </Link>
        </DropdownMenuItem>
        <DropdownMenuSeparator />
        <DropdownMenuItem onSelect={onLogout} disabled={loggingOut}>
          <LogOut aria-hidden="true" />
          Sair
        </DropdownMenuItem>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
