import { cva, type VariantProps } from "class-variance-authority";
import type { HTMLAttributes } from "react";
import type { Tone } from "@/lib/labels";
import { cn } from "@/lib/utils";

export const badgeVariants = cva(
  "inline-flex items-center gap-1 whitespace-nowrap rounded-full border px-2 py-0.5 text-[11px] font-medium leading-4 tracking-wide",
  {
    variants: {
      tone: {
        neutral: "border-line bg-elevated text-fg-muted",
        accent: "border-accent/30 bg-accent/10 text-accent",
        success: "border-success/30 bg-success/10 text-success",
        warning: "border-warning/30 bg-warning/10 text-warning",
        danger: "border-danger/30 bg-danger/10 text-danger",
        info: "border-info/30 bg-info/10 text-info",
        outline: "border-line-strong bg-transparent text-fg-muted",
      },
    },
    defaultVariants: { tone: "neutral" },
  },
);

export interface BadgeProps extends HTMLAttributes<HTMLSpanElement>, VariantProps<typeof badgeVariants> {
  tone?: Tone | "outline";
  /** Shows a small pulsing dot (e.g. running jobs). */
  pulse?: boolean;
}

export function Badge({ className, tone, pulse, children, ...props }: BadgeProps) {
  return (
    <span className={cn(badgeVariants({ tone }), className)} {...props}>
      {pulse ? <span className="size-1.5 rounded-full bg-current animate-pulse-soft" aria-hidden="true" /> : null}
      {children}
    </span>
  );
}
