import type { SubtitlePosition, SubtitleStyle } from "@/lib/api/types";
import { SUBTITLE_POSITION_LABELS, SUBTITLE_STYLE_LABELS } from "@/lib/labels";
import { cn } from "@/lib/utils";

const styleClass: Record<SubtitleStyle, string> = {
  bold: "text-lg font-black uppercase tracking-wide text-white [text-shadow:0_0_2px_#000,0_0_2px_#000,2px_2px_0_#000,-2px_-2px_0_#000,2px_-2px_0_#000,-2px_2px_0_#000]",
  clean: "text-base font-semibold text-white [text-shadow:0_1px_2px_rgba(0,0,0,0.9)]",
  boxed: "rounded-xs bg-black/70 px-2 py-0.5 text-base font-semibold text-white",
};

const positionClass: Record<SubtitlePosition, string> = {
  bottom: "items-end pb-3",
  center: "items-center",
  top: "items-start pt-3",
};

/**
 * Labelled "Prévia do estilo": a sample frame with the subtitle text in the chosen style and
 * position (an illustration of the style, not a frame of the recording).
 */
export function SubtitlePreview({
  style,
  position,
  enabled = true,
  text = "esse momento foi insano",
  className,
}: {
  style: SubtitleStyle;
  position: SubtitlePosition;
  enabled?: boolean;
  text?: string;
  className?: string;
}) {
  return (
    <figure className={cn("min-w-0 space-y-1.5", className)}>
      <figcaption className="text-xs font-medium text-fg-muted">Prévia do estilo</figcaption>
      <div
        className="relative aspect-video w-full overflow-hidden rounded-md border border-line bg-[radial-gradient(60%_80%_at_70%_30%,#2b3240,transparent),linear-gradient(160deg,#1c2027,#0b0d10)]"
        aria-label={
          enabled
            ? `Prévia da legenda no estilo ${SUBTITLE_STYLE_LABELS[style] ?? style}, posição ${(SUBTITLE_POSITION_LABELS[position] ?? position).toLowerCase()}`
            : "Legendas desativadas"
        }
        role="img"
      >
        {enabled ? (
          <div className={cn("absolute inset-0 flex justify-center px-6 text-center", positionClass[position])}>
            <span className={styleClass[style]}>{text}</span>
          </div>
        ) : (
          <div className="absolute inset-0 flex items-center justify-center text-xs text-fg-subtle">sem legendas</div>
        )}
      </div>
    </figure>
  );
}
