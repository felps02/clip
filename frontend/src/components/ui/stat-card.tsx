import type { ReactNode } from "react";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";

export interface StatCardProps {
  label: string;
  value: ReactNode;
  hint?: ReactNode;
  icon?: ReactNode;
  loading?: boolean;
  accent?: boolean;
  className?: string;
}

export function StatCard({ label, value, hint, icon, loading, accent, className }: StatCardProps) {
  return (
    <div
      className={cn(
        "relative overflow-hidden rounded-lg border border-line bg-surface p-5 shadow-card",
        accent && "border-accent/30",
        className,
      )}
    >
      {accent ? (
        <div
          className="pointer-events-none absolute -right-10 -top-10 size-32 rounded-full bg-accent/10 blur-2xl"
          aria-hidden="true"
        />
      ) : null}
      {/* Two label lines are always reserved, so a label that wraps ("Minutos processados no período")
          does not push its value below the values of the neighbouring cards. */}
      <div className="flex items-start justify-between gap-3">
        <p className="line-clamp-2 min-h-8 text-xs font-medium uppercase leading-4 tracking-wide text-fg-subtle">{label}</p>
        {icon ? <div className="text-fg-subtle [&_svg]:size-4">{icon}</div> : null}
      </div>
      {loading ? (
        <Skeleton className="mt-3 h-8 w-20" />
      ) : (
        <p className={cn("mt-2 text-3xl font-semibold tabular-nums tracking-tight", accent && "text-accent")}>{value}</p>
      )}
      {hint ? <p className="mt-1 text-xs text-fg-muted">{hint}</p> : null}
    </div>
  );
}
