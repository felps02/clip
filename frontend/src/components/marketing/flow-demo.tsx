import { FileVideo, Sparkles } from "lucide-react";
import { cn } from "@/lib/utils";

/** Deterministic waveform so the server and the client render the same bars. */
const WAVE = [
  14, 22, 18, 30, 36, 62, 88, 70, 36, 22, 18, 26, 30, 20, 24, 28, 22, 30, 40, 76, 92, 58, 30, 20, 16, 24, 22, 30, 26, 18,
  24, 30, 44, 80, 68, 38, 24, 18, 22, 14,
];
/** Peak bars at the centres of the three chip columns (1/6, 1/2 and 5/6 of the width). */
const WAVE_PEAKS = [6, 20, 33];
/**
 * Chip labels, in the order of the peaks. The chips sit in three equal columns above the waveform
 * (not absolutely positioned at the peaks), so they can never overlap or leave the card.
 */
const PEAKS: Array<{ index: number; label: string }> = [
  { index: WAVE_PEAKS[0], label: "reação" },
  { index: WAVE_PEAKS[1], label: "hype" },
  { index: WAVE_PEAKS[2], label: "engraçado" },
];

function Connector({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 64 24"
      className={cn("h-6 w-16 shrink-0 text-line-strong", className)}
      aria-hidden="true"
    >
      <path
        d="M2 12 H54"
        stroke="currentColor"
        strokeWidth="2"
        strokeDasharray="6 6"
        strokeLinecap="round"
        className="animate-flow-dash"
      />
      <path d="M50 6 L58 12 L50 18" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" />
    </svg>
  );
}

function StageLabel({ step, children }: { step: number; children: string }) {
  return (
    <p className="mb-3 flex items-center gap-2 text-xs font-medium uppercase tracking-wide text-fg-subtle">
      <span className="flex size-5 items-center justify-center rounded-full bg-elevated font-mono text-[10px] text-fg">{step}</span>
      {children}
    </p>
  );
}

/** Upload → análise → clipes, built with CSS/SVG only (no screenshots, no numbers). */
export function FlowDemo({ className }: { className?: string }) {
  const maxWave = Math.max(...WAVE);
  return (
    <div
      className={cn(
        "@container relative min-w-0 overflow-hidden rounded-xl border border-line bg-surface/80 p-5 shadow-card backdrop-blur sm:p-6",
        className,
      )}
    >
      <p className="sr-only">
        Demonstração do fluxo: você envia a gravação, a análise identifica os momentos mais intensos na linha do tempo e
        os clipes verticais são gerados com legendas.
      </p>
      <div className="pointer-events-none absolute -right-20 -top-20 size-64 rounded-full bg-accent/10 blur-3xl" aria-hidden="true" />
      <div className="relative flex flex-col items-stretch gap-4 @5xl:flex-row @5xl:items-center" aria-hidden="true">
        {/* 1. Upload */}
        <div className="min-w-0 flex-1 rounded-lg border border-line bg-canvas/60 p-4">
          <StageLabel step={1}>Upload</StageLabel>
          <div className="flex items-center gap-3">
            <div className="flex size-11 items-center justify-center rounded-md border border-line bg-elevated text-fg-muted">
              <FileVideo className="size-5" />
            </div>
            <div className="min-w-0 flex-1">
              <div className="h-2.5 w-3/4 rounded-full bg-line-strong" />
              <div className="mt-2 h-1.5 w-full overflow-hidden rounded-full bg-line">
                <div className="h-full rounded-full bg-accent animate-grow" />
              </div>
            </div>
          </div>
          <div className="mt-4 flex gap-1.5">
            <div className="h-1.5 w-10 rounded-full bg-line-strong" />
            <div className="h-1.5 w-16 rounded-full bg-line" />
          </div>
        </div>

        <Connector className="hidden rotate-0 self-center @5xl:block" />
        <Connector className="mx-auto rotate-90 @5xl:hidden" />

        {/* 2. Análise */}
        <div className="min-w-0 flex-[1.4] rounded-lg border border-line bg-canvas/60 p-4">
          <StageLabel step={2}>Análise</StageLabel>
          <div className="relative h-24">
            <div className="absolute inset-x-0 top-0 grid h-5 grid-cols-3 gap-1">
              {PEAKS.map((peak) => (
                <span key={peak.index} className="flex min-w-0 justify-center">
                  <span className="truncate rounded-full border border-accent/40 bg-accent/15 px-1.5 py-0.5 text-[10px] font-medium leading-none text-accent">
                    {peak.label}
                  </span>
                </span>
              ))}
            </div>
            <svg viewBox={`0 0 ${WAVE.length * 10} 100`} preserveAspectRatio="none" className="absolute inset-x-0 bottom-0 h-16 w-full">
              {WAVE.map((height, index) => {
                const h = (height / maxWave) * 100;
                const isPeak = PEAKS.some((peak) => Math.abs(peak.index - index) <= 1);
                return (
                  <rect
                    key={index}
                    x={index * 10 + 2}
                    y={100 - h}
                    width={6}
                    height={h}
                    rx={2}
                    className={isPeak ? "fill-accent" : "fill-line-strong"}
                  />
                );
              })}
            </svg>
            <div className="absolute bottom-0 top-5 w-0.5 bg-fg/80 animate-scan" style={{ left: 0 }} />
          </div>
          <div className="mt-3 flex items-center gap-2 text-[11px] text-fg-subtle">
            <Sparkles className="size-3.5 text-accent" />
            áudio · fala · imagem
          </div>
        </div>

        <Connector className="hidden self-center @5xl:block" />
        <Connector className="mx-auto rotate-90 @5xl:hidden" />

        {/* 3. Clipes */}
        <div className="min-w-0 flex-1 rounded-lg border border-line bg-canvas/60 p-4">
          <StageLabel step={3}>Clipes</StageLabel>
          <div className="flex items-end justify-center gap-2 sm:gap-3">
            {PEAKS.map((peak, index) => (
              <div
                key={peak.label}
                className={cn(
                  "relative flex h-24 w-14 flex-col justify-end overflow-hidden rounded-md border border-line bg-[linear-gradient(170deg,#262b33,#14171c)] p-1.5",
                  index === 1 && "h-28 w-16 border-accent/50 shadow-glow animate-float",
                )}
              >
                <span className="absolute left-1.5 top-1.5 rounded-xs bg-black/60 px-1 font-mono text-[8px] text-fg">9:16</span>
                <div className="space-y-1">
                  <div className="mx-auto h-1.5 w-10 rounded-full bg-fg/90" />
                  <div className="mx-auto h-1.5 w-7 rounded-full bg-accent" />
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
      <p className="relative mt-4 text-center text-[11px] text-fg-subtle">
        Ilustração do fluxo: não representa uma análise real.
      </p>
    </div>
  );
}
