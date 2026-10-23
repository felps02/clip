import type { Tone } from "@/lib/labels";
import { clamp, cn } from "@/lib/utils";

export interface ProgressProps {
  /** 0–max */
  value: number;
  max?: number;
  tone?: Tone;
  size?: "sm" | "md" | "lg";
  /** Accessible name of the progressbar. */
  label?: string;
  /** Human-readable value announced instead of the raw number (`aria-valuetext`). */
  valueText?: string;
  className?: string;
  /** Animated stripe to signal activity when progress is unknown or slow. */
  indeterminate?: boolean;
}

const toneClass: Record<Tone, string> = {
  neutral: "bg-fg-muted",
  accent: "bg-accent",
  success: "bg-success",
  warning: "bg-warning",
  danger: "bg-danger",
  info: "bg-info",
};

const sizeClass = { sm: "h-1", md: "h-2", lg: "h-3" };

export function Progress({ value, max = 100, tone = "accent", size = "md", label, valueText, className, indeterminate }: ProgressProps) {
  const percent = max > 0 ? clamp((value / max) * 100, 0, 100) : 0;
  return (
    <div
      role="progressbar"
      aria-valuemin={0}
      aria-valuemax={max}
      aria-valuenow={indeterminate ? undefined : Math.round(value)}
      aria-valuetext={valueText}
      aria-label={label}
      className={cn("w-full overflow-hidden rounded-full bg-line", sizeClass[size], className)}
    >
      <div
        className={cn(
          "h-full rounded-full transition-[width] duration-300 ease-soft",
          toneClass[tone],
          indeterminate && "w-1/3 animate-shimmer",
        )}
        style={indeterminate ? undefined : { width: `${percent}%` }}
      />
    </div>
  );
}

/** Usage relative to a plan limit; `null` when the limit is unlimited (0 or missing). */
export function usageRatio(used: number, limit: number): number | null {
  return !limit || limit <= 0 ? null : used / limit;
}

/** Visible (not colour-only) state of a usage meter at the 80 % / 100 % thresholds. */
export function usageThresholdLabel(ratio: number | null): string | null {
  if (ratio === null) return null;
  if (ratio >= 1) return "Limite atingido";
  if (ratio >= 0.8) return `${Math.floor(ratio * 100)}% do limite usado`;
  return null;
}

/** Usage meter with threshold-based tone (80% warning, 100% danger). */
export function UsageMeter({
  used,
  limit,
  label,
  valueText,
  className,
}: {
  used: number;
  limit: number;
  label: string;
  valueText?: string;
  className?: string;
}) {
  const ratio = usageRatio(used, limit);
  const tone: Tone = ratio === null ? "accent" : ratio >= 1 ? "danger" : ratio >= 0.8 ? "warning" : "accent";
  return (
    <Progress
      value={ratio === null ? 0 : Math.min(ratio, 1) * 100}
      tone={tone}
      size="sm"
      label={label}
      valueText={valueText}
      className={className}
    />
  );
}

/** Label, "used / limit" and meter; the progressbar is named after the row and announces the values. */
export function UsageRow({ label, used, limit, format }: { label: string; used: number; limit: number; format: (value: number) => string }) {
  const ratio = usageRatio(used, limit);
  const limitText = ratio === null ? "ilimitado" : format(limit);
  const threshold = usageThresholdLabel(ratio);
  const valueText = `${format(used)} de ${limitText}${threshold ? ` (${threshold.toLowerCase()})` : ""}`;
  return (
    <div className="space-y-1.5">
      <div className="flex flex-wrap items-center justify-between gap-x-3 text-sm">
        <span className="text-fg-muted">{label}</span>
        <span className="font-medium tabular-nums">
          {format(used)}
          <span className="text-fg-subtle"> / {limitText}</span>
        </span>
      </div>
      <UsageMeter used={used} limit={limit} label={label} valueText={valueText} />
      {threshold ? (
        <p className={cn("text-xs", ratio !== null && ratio >= 1 ? "text-danger" : "text-warning")} aria-hidden="true">
          {threshold}
        </p>
      ) : null}
    </div>
  );
}
