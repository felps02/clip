"use client";

import { formatDateTime, formatElapsed, formatRelativeTime } from "@/lib/format";
import { useNow, type ClockPrecision } from "@/lib/hooks/use-now";

export interface RelativeTimeProps {
  value: string | null | undefined;
  className?: string;
}

interface TickingTimeProps {
  value: string;
  className?: string;
  precision: ClockPrecision;
  format: (value: string, now: number) => string;
}

/**
 * The server render and the hydration pass show the absolute date and time (explicit time zone),
 * which is the same on both sides, so the text that depends on the current time never causes a
 * hydration mismatch. Afterwards the text follows a clock, not only the data: a query that keeps
 * returning the same answer does not freeze it.
 */
function TickingTime({ value, className, precision, format }: TickingTimeProps) {
  const now = useNow(precision);
  const absolute = formatDateTime(value);
  return (
    <time dateTime={value} title={absolute} className={className}>
      {now === null ? absolute : format(value, now)}
    </time>
  );
}

/** "há 5 minutos", "agora" (under 45 s): updated every 15 s, relative only after hydration. */
export function RelativeTime({ value, className }: RelativeTimeProps) {
  if (!value) return <span className={className}>—</span>;
  return <TickingTime value={value} className={className} precision="minute" format={formatRelativeTime} />;
}

/**
 * Time since a past event, counted every second under a minute ("há 35 segundos") and never "agora":
 * for "último contato" next to a "sem sinal" state. Relative only after hydration, like `RelativeTime`.
 */
export function ElapsedTime({ value, className }: RelativeTimeProps) {
  if (!value) return <span className={className}>—</span>;
  return <TickingTime value={value} className={className} precision="second" format={formatElapsed} />;
}
