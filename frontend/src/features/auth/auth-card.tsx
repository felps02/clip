import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

export function AuthCard({
  title,
  description,
  children,
  footer,
  className,
}: {
  title: string;
  description?: ReactNode;
  children: ReactNode;
  footer?: ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("rounded-xl border border-line bg-surface p-6 shadow-pop sm:p-8", className)}>
      <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
      {description ? <p className="mt-1.5 text-sm text-fg-muted">{description}</p> : null}
      <div className="mt-6">{children}</div>
      {footer ? <div className="mt-6 border-t border-line pt-4 text-center text-sm text-fg-muted">{footer}</div> : null}
    </div>
  );
}
