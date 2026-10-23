import { ArrowLeft } from "lucide-react";
import Link from "next/link";
import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/** Typography of the page title, for custom headings passed through `heading`. */
export const PAGE_TITLE_CLASS = "truncate text-2xl font-semibold tracking-tight sm:text-3xl";

export interface PageHeaderProps {
  /** Text of the page `<h1>`. */
  title?: ReactNode;
  /**
   * Replaces the default `<h1>{title}</h1>` with a custom block (e.g. an editable title). The
   * block renders its own `<h1>`; interactive controls stay outside the heading element.
   */
  heading?: ReactNode;
  description?: ReactNode;
  actions?: ReactNode;
  backHref?: string;
  backLabel?: string;
  eyebrow?: ReactNode;
  className?: string;
}

export function PageHeader({ title, heading, description, actions, backHref, backLabel = "Voltar", eyebrow, className }: PageHeaderProps) {
  return (
    <header className={cn("mb-6 flex flex-col gap-4 sm:flex-row sm:items-end sm:justify-between", className)}>
      <div className="min-w-0">
        {backHref ? (
          <Link
            href={backHref}
            className="mb-2 inline-flex items-center gap-1 text-xs font-medium text-fg-muted hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 rounded-sm"
          >
            <ArrowLeft className="size-3.5" aria-hidden="true" />
            {backLabel}
          </Link>
        ) : null}
        {eyebrow ? <div className="mb-1 text-xs font-medium uppercase tracking-wide text-accent">{eyebrow}</div> : null}
        {heading ?? <h1 className={PAGE_TITLE_CLASS}>{title}</h1>}
        {description ? <div className="mt-1 max-w-2xl text-sm text-fg-muted">{description}</div> : null}
      </div>
      {actions ? <div className="flex shrink-0 flex-wrap items-center gap-2">{actions}</div> : null}
    </header>
  );
}
