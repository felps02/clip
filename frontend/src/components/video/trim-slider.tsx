"use client";

import { Slider } from "@/components/ui/slider";
import { formatDuration, formatTimecode } from "@/lib/format";
import { clamp, cn } from "@/lib/utils";

export interface TrimSliderProps {
  /** Source duration in seconds (upper bound). */
  max: number;
  min?: number;
  value: [number, number];
  onChange: (value: [number, number]) => void;
  step?: number;
  /** Minimum selection length in seconds. */
  minLength?: number;
  /** Player position, shown as a marker on the track. */
  currentTime?: number | null;
  disabled?: boolean;
  className?: string;
}

/** Dual-thumb range bounded by the source duration with the selection highlighted. */
export function TrimSlider({
  max,
  min = 0,
  value,
  onChange,
  step = 0.1,
  minLength = 1,
  currentTime,
  disabled,
  className,
}: TrimSliderProps) {
  const span = Math.max(0.001, max - min);
  const markerLeft = currentTime === null || currentTime === undefined ? null : clamp(((currentTime - min) / span) * 100, 0, 100);
  const minSteps = Math.max(1, Math.round(minLength / step));

  return (
    <div className={cn("space-y-2", className)}>
      <div className="relative px-2 pt-3">
        {markerLeft !== null ? (
          <div
            className="pointer-events-none absolute top-0 z-10 flex -translate-x-1/2 flex-col items-center"
            style={{ left: `calc(0.5rem + ${markerLeft}% * (100% - 1rem) / 100)` }}
            aria-hidden="true"
          >
            <span className="rounded-xs bg-fg px-1 font-mono text-[10px] text-canvas tabular-nums">
              {formatTimecode(currentTime)}
            </span>
            <span className="h-5 w-px bg-fg" />
          </div>
        ) : null}
        <Slider
          min={min}
          max={max}
          step={step}
          minStepsBetweenThumbs={minSteps}
          value={value}
          onValueChange={(next) => {
            if (next.length === 2) onChange([next[0], next[1]]);
          }}
          thumbLabels={["Início do clipe", "Fim do clipe"]}
          disabled={disabled}
          className="mt-4"
        />
      </div>
      <div className="flex items-center justify-between px-2 font-mono text-[11px] text-fg-subtle tabular-nums">
        <span>{formatDuration(min)}</span>
        <span className="text-fg-muted">
          seleção {formatTimecode(value[0])} – {formatTimecode(value[1])} · {formatDuration(value[1] - value[0])}
        </span>
        <span>{formatDuration(max)}</span>
      </div>
    </div>
  );
}
