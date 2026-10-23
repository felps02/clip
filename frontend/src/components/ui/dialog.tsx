"use client";

import * as DialogPrimitive from "@radix-ui/react-dialog";
import { X } from "lucide-react";
import { forwardRef, useRef, type ComponentPropsWithoutRef, type ComponentRef, type HTMLAttributes, type ReactNode, type RefObject } from "react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";

export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;
export const DialogPortal = DialogPrimitive.Portal;

export const DialogOverlay = forwardRef<
  ComponentRef<typeof DialogPrimitive.Overlay>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Overlay>
>(function DialogOverlay({ className, ...props }, ref) {
  return (
    <DialogPrimitive.Overlay
      ref={ref}
      className={cn("fixed inset-0 z-50 bg-canvas/80 backdrop-blur-sm animate-fade-in", className)}
      {...props}
    />
  );
});

export interface DialogContentProps extends ComponentPropsWithoutRef<typeof DialogPrimitive.Content> {
  size?: "sm" | "md" | "lg" | "xl";
  hideClose?: boolean;
}

const sizes = { sm: "max-w-sm", md: "max-w-lg", lg: "max-w-2xl", xl: "max-w-4xl" };

export const DialogContent = forwardRef<ComponentRef<typeof DialogPrimitive.Content>, DialogContentProps>(
  function DialogContent({ className, children, size = "md", hideClose, ...props }, ref) {
    return (
      <DialogPortal>
        <DialogOverlay />
        <DialogPrimitive.Content
          ref={ref}
          className={cn(
            "fixed left-1/2 top-1/2 z-50 flex max-h-[calc(100dvh-2rem)] w-[calc(100vw-2rem)] -translate-x-1/2 -translate-y-1/2 flex-col gap-4 overflow-y-auto rounded-lg border border-line bg-surface p-6 shadow-pop animate-slide-up focus:outline-none",
            sizes[size],
            className,
          )}
          {...props}
        >
          {children}
          {hideClose ? null : (
            <DialogPrimitive.Close asChild>
              <Button variant="ghost" size="icon-sm" className="absolute right-3 top-3" aria-label="Fechar">
                <X aria-hidden="true" />
              </Button>
            </DialogPrimitive.Close>
          )}
        </DialogPrimitive.Content>
      </DialogPortal>
    );
  },
);

export function DialogHeader({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("flex flex-col gap-1.5 pr-8 text-left", className)} {...props} />;
}

export function DialogFooter({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return <div className={cn("flex flex-col-reverse gap-2 sm:flex-row sm:justify-end", className)} {...props} />;
}

export const DialogTitle = forwardRef<
  ComponentRef<typeof DialogPrimitive.Title>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Title>
>(function DialogTitle({ className, ...props }, ref) {
  return (
    <DialogPrimitive.Title
      ref={ref}
      className={cn("text-lg font-semibold leading-tight tracking-tight", className)}
      {...props}
    />
  );
});

export const DialogDescription = forwardRef<
  ComponentRef<typeof DialogPrimitive.Description>,
  ComponentPropsWithoutRef<typeof DialogPrimitive.Description>
>(function DialogDescription({ className, ...props }, ref) {
  return <DialogPrimitive.Description ref={ref} className={cn("text-sm text-fg-muted", className)} {...props} />;
});

/** An element that can take keyboard focus when a dialog closes. */
export type FocusTargetRef = RefObject<HTMLElement | null>;

/**
 * Where keyboard focus goes when a `ConfirmDialog` closes. By default it goes back to the element that
 * opened the dialog. `afterConfirm` / `afterCancel` take that place for one way of closing (after a
 * confirmation the opener often disappears with the thing it acted on), and `fallback` is used when
 * neither the chosen element nor the opener is on the page any more (or nothing had focus when the
 * dialog opened: Safari does not focus a clicked button).
 */
export interface ConfirmDialogReturnFocus {
  afterConfirm?: FocusTargetRef;
  afterCancel?: FocusTargetRef;
  fallback?: FocusTargetRef;
}

export interface ConfirmDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: ReactNode;
  description?: ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  destructive?: boolean;
  loading?: boolean;
  onConfirm: () => void;
  returnFocus?: ConfirmDialogReturnFocus;
  children?: ReactNode;
}

/** Focuses the first candidate that is still on the page and takes focus (not disabled, not removed). */
function focusFirstAvailable(candidates: ReadonlyArray<HTMLElement | null | undefined>): void {
  for (const element of candidates) {
    if (!element?.isConnected) continue;
    element.focus();
    if (document.activeElement === element) return;
  }
}

/** True when focus is on an element of the page: not on <body>, not on something that was removed. */
function hasFocusOnPage(): boolean {
  const active = document.activeElement;
  return active instanceof HTMLElement && active !== document.body && active.isConnected;
}

/**
 * Accessible confirmation dialog for destructive or irreversible actions. It is controlled (no
 * trigger), so it restores focus itself when it closes: Radix alone returns focus to the trigger, and
 * without one keyboard and screen-reader users would land on <body> (WCAG 2.4.3).
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  confirmLabel = "Confirmar",
  cancelLabel = "Cancelar",
  destructive = false,
  loading = false,
  onConfirm,
  returnFocus,
  children,
}: ConfirmDialogProps) {
  const openerRef = useRef<HTMLElement | null>(null);
  // Set by the confirm button until the dialog closes; a cancel (button, Esc, close button) clears it.
  const confirmedRef = useRef(false);

  const requestOpenChange = (next: boolean) => {
    if (loading) return;
    if (!next) confirmedRef.current = false;
    onOpenChange(next);
  };

  return (
    <Dialog open={open} onOpenChange={requestOpenChange}>
      <DialogContent
        size="sm"
        aria-describedby={description ? undefined : undefined}
        onOpenAutoFocus={() => {
          // Runs before focus moves into the dialog: the focused element is the one that opened it.
          const active = document.activeElement;
          openerRef.current = active instanceof HTMLElement && active !== document.body ? active : null;
          confirmedRef.current = false;
        }}
        onCloseAutoFocus={(event) => {
          // Radix would focus the missing trigger; this dialog picks the element instead.
          event.preventDefault();
          // The page may already have moved focus on purpose (e.g. to a field it rendered in place of the opener).
          if (hasFocusOnPage()) return;
          const preferred = confirmedRef.current ? returnFocus?.afterConfirm : returnFocus?.afterCancel;
          focusFirstAvailable([preferred?.current, openerRef.current, returnFocus?.fallback?.current]);
        }}
      >
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          {description ? <DialogDescription>{description}</DialogDescription> : null}
        </DialogHeader>
        {children}
        <DialogFooter>
          <Button variant="secondary" onClick={() => requestOpenChange(false)} disabled={loading}>
            {cancelLabel}
          </Button>
          <Button
            variant={destructive ? "danger" : "primary"}
            onClick={() => {
              confirmedRef.current = true;
              onConfirm();
            }}
            loading={loading}
          >
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
