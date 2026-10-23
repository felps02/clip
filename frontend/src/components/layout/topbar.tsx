"use client";

import { Menu, Plus } from "lucide-react";
import Link from "next/link";
import { BrandMark } from "@/components/brand-mark";
import { NotificationsMenu } from "@/components/layout/notifications-menu";
import { UserMenu } from "@/components/layout/user-menu";
import { Button, buttonVariants } from "@/components/ui/button";
import type { MeResponse } from "@/lib/api/types";
import { cn } from "@/lib/utils";

export interface TopbarProps {
  onMenuClick: () => void;
  me?: MeResponse;
  loading?: boolean;
  onLogout: () => void;
  loggingOut?: boolean;
}

export function Topbar({ onMenuClick, me, loading, onLogout, loggingOut }: TopbarProps) {
  return (
    <header className="sticky top-0 z-20 flex h-16 items-center gap-3 border-b border-line bg-canvas/80 px-4 backdrop-blur-md sm:px-6 lg:px-8">
      <Button variant="ghost" size="icon" className="lg:hidden" onClick={onMenuClick} aria-label="Abrir menu">
        <Menu aria-hidden="true" />
      </Button>
      <div className="lg:hidden">
        <BrandMark href="/dashboard" compact />
      </div>
      <div className="ml-auto flex items-center gap-1 sm:gap-2">
        <Link href="/new" className={cn(buttonVariants({ variant: "primary", size: "sm" }), "hidden sm:inline-flex")}>
          <Plus aria-hidden="true" />
          Novo projeto
        </Link>
        <NotificationsMenu />
        <UserMenu me={me} loading={loading} onLogout={onLogout} loggingOut={loggingOut} />
      </div>
    </header>
  );
}
