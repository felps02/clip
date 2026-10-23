"use client";

import { Bell, CheckCheck } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { toast } from "sonner";
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
import { describeError } from "@/lib/api/client";
import {
  useMarkAllNotificationsRead,
  useMarkNotificationRead,
  useNotifications,
  useUnreadCount,
} from "@/lib/api/hooks/use-notifications";
import type { Notification } from "@/lib/api/types";
import { RelativeTime } from "@/components/ui/relative-time";
import { notificationHeading, notificationHref } from "@/lib/notifications";
import { cn } from "@/lib/utils";

function NotificationContent({ notification }: { notification: Notification }) {
  const { label, title, showLabel } = notificationHeading(notification);
  return (
    <>
      <span
        className={cn(
          "mt-1.5 size-2 shrink-0 rounded-full",
          notification.readAt ? "bg-transparent" : "bg-accent",
        )}
        aria-hidden="true"
      />
      <span className="min-w-0 flex-1">
        {showLabel ? (
          <span className="block truncate text-[11px] font-medium uppercase tracking-wide text-fg-subtle">{label}</span>
        ) : null}
        <span className={cn("block truncate text-sm", notification.readAt ? "text-fg-muted" : "font-medium text-fg")}>
          {title}
        </span>
        {notification.body ? (
          <span className="mt-0.5 line-clamp-2 block text-xs text-fg-muted">{notification.body}</span>
        ) : null}
        <RelativeTime value={notification.createdAt} className="mt-1 block text-[11px] text-fg-subtle" />
      </span>
      {!notification.readAt ? <span className="sr-only">Não lida</span> : null}
    </>
  );
}

export function NotificationsMenu() {
  const [open, setOpen] = useState(false);
  const unread = useUnreadCount();
  const list = useNotifications({ enabled: open });
  const markRead = useMarkNotificationRead();
  const markAll = useMarkAllNotificationsRead();

  const count = unread.data ?? 0;
  const items = list.data?.items ?? [];

  const markAsRead = (notification: Notification) => {
    if (!notification.readAt) {
      markRead.mutate(notification.id, { onError: (error) => toast.error(describeError(error)) });
    }
  };

  return (
    <DropdownMenu open={open} onOpenChange={setOpen}>
      <DropdownMenuTrigger asChild>
        <Button
          variant="ghost"
          size="icon"
          className="relative"
          aria-label={count > 0 ? `Notificações, ${count} não lidas` : "Notificações"}
        >
          <Bell aria-hidden="true" />
          {count > 0 ? (
            <span className="absolute right-1.5 top-1.5 flex h-4 min-w-4 items-center justify-center rounded-full bg-accent px-1 text-[10px] font-semibold text-accent-fg">
              {count > 99 ? "99+" : count}
            </span>
          ) : null}
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent align="end" className="w-[min(22rem,calc(100vw-2rem))] p-0">
        <DropdownMenuLabel className="px-3 pb-1 pt-2.5 text-sm font-semibold normal-case tracking-normal text-fg">Notificações</DropdownMenuLabel>
        {/* A menu item (not a plain button) so arrow keys reach it: Radix menus trap Tab. */}
        <div className="px-1 pb-1">
          <DropdownMenuItem
            disabled={count === 0 || markAll.isPending}
            onSelect={(event) => {
              // Keep the menu open so the list shows the result.
              event.preventDefault();
              markAll.mutate(undefined, {
                onSuccess: () => toast.success("Todas as notificações foram marcadas como lidas"),
                onError: (error) => toast.error(describeError(error)),
              });
            }}
          >
            <CheckCheck aria-hidden="true" />
            Marcar todas como lidas
          </DropdownMenuItem>
        </div>
        <DropdownMenuSeparator className="my-0" />
        <div className="max-h-96 overflow-y-auto p-1">
          {list.isPending ? (
            <div className="space-y-2 p-2" aria-busy="true">
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
              <Skeleton className="h-12 w-full" />
            </div>
          ) : list.isError ? (
            <p className="px-3 py-6 text-center text-sm text-danger">{describeError(list.error)}</p>
          ) : items.length === 0 ? (
            <p className="px-3 py-8 text-center text-sm text-fg-muted">Nenhuma notificação por enquanto.</p>
          ) : (
            items.map((notification) => {
              const href = notificationHref(notification);
              // A real link (open in a new tab works too). Next's Link prevents the click's default
              // action, which makes Radix skip `onSelect`, so the link itself marks and closes.
              return href ? (
                <DropdownMenuItem key={notification.id} asChild className="items-start gap-3 py-2.5">
                  <Link
                    href={href}
                    onClick={() => {
                      markAsRead(notification);
                      setOpen(false);
                    }}
                  >
                    <NotificationContent notification={notification} />
                  </Link>
                </DropdownMenuItem>
              ) : (
                <DropdownMenuItem key={notification.id} onSelect={() => markAsRead(notification)} className="items-start gap-3 py-2.5">
                  <NotificationContent notification={notification} />
                </DropdownMenuItem>
              );
            })
          )}
        </div>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
