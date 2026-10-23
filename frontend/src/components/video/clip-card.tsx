import { Film, Play } from "lucide-react";
import Image from "next/image";
import Link from "next/link";
import type { ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import type { ClipSummary } from "@/lib/api/types";
import { formatDate, formatDuration } from "@/lib/format";
import { categoryLabel, CLIP_STATUS_LABELS, CLIP_STATUS_TONES } from "@/lib/labels";
import { cn } from "@/lib/utils";

export interface ClipCardProps {
  clip: ClipSummary;
  /** Link target for the title/thumbnail. Defaults to the editor. */
  href?: string | null;
  /** Shown as a play overlay on the thumbnail. */
  onPreview?: (clip: ClipSummary) => void;
  /** Action area (e.g. a dropdown menu) rendered next to the title. */
  actions?: ReactNode;
  showProject?: boolean;
  className?: string;
}

export function ClipCard({ clip, href, onPreview, actions, showProject, className }: ClipCardProps) {
  const target = href === undefined ? `/clips/${clip.id}` : href;
  const canPreview = !!onPreview && clip.status === "ready";

  const thumbnail = clip.thumbnailUrl ? (
    <Image
      src={clip.thumbnailUrl}
      alt=""
      fill
      unoptimized
      sizes="(min-width: 1280px) 25vw, (min-width: 768px) 33vw, 100vw"
      className="object-cover transition-transform duration-300 ease-soft group-hover:scale-[1.03]"
    />
  ) : (
    <div className="absolute inset-0 flex items-center justify-center bg-[radial-gradient(80%_80%_at_30%_20%,rgba(179,245,59,0.12),transparent),linear-gradient(160deg,#1c2027,#0b0d10)]">
      <Film className="size-8 text-fg-subtle" aria-hidden="true" />
    </div>
  );

  return (
    <article
      className={cn(
        "group flex flex-col overflow-hidden rounded-lg border border-line bg-surface shadow-card transition-[border-color,transform] duration-200 ease-soft hover:border-line-strong",
        className,
      )}
    >
      <div className="relative aspect-video w-full overflow-hidden bg-elevated">
        {target ? (
          <Link href={target} className="absolute inset-0 focus-visible:outline-none" tabIndex={-1} aria-hidden="true">
            {thumbnail}
          </Link>
        ) : (
          thumbnail
        )}
        <div className="pointer-events-none absolute inset-x-0 bottom-0 h-16 bg-gradient-to-t from-black/70 to-transparent" />
        <span className="absolute left-2 top-2 rounded-xs bg-black/60 px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wide text-fg">
          {clip.aspectRatio}
        </span>
        {clip.status !== "ready" ? (
          <Badge tone={CLIP_STATUS_TONES[clip.status]} pulse={clip.status === "rendering"} className="absolute right-2 top-2">
            {CLIP_STATUS_LABELS[clip.status]}
          </Badge>
        ) : null}
        <span className="absolute bottom-2 right-2 rounded-xs bg-black/70 px-1.5 py-0.5 font-mono text-[11px] font-medium tabular-nums text-fg">
          {formatDuration(clip.durationSeconds)}
        </span>
        {canPreview ? (
          <button
            type="button"
            onClick={() => onPreview?.(clip)}
            className="absolute inset-0 flex items-center justify-center bg-black/0 opacity-0 transition-opacity duration-200 hover:bg-black/30 hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-none"
            aria-label={`Pré-visualizar ${clip.title}`}
          >
            <span className="flex size-12 items-center justify-center rounded-full bg-accent text-accent-fg shadow-glow">
              <Play className="ml-0.5 size-5" aria-hidden="true" />
            </span>
          </button>
        ) : null}
      </div>
      <div className="flex flex-1 flex-col gap-2 p-4">
        <div className="flex items-start justify-between gap-2">
          <h3 className="line-clamp-2 min-w-0 text-sm font-semibold leading-snug">
            {target ? (
              <Link
                href={target}
                className="rounded-xs focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 hover:text-accent"
              >
                {clip.title}
              </Link>
            ) : (
              clip.title
            )}
          </h3>
          {actions ? <div className="-mr-2 -mt-1 shrink-0">{actions}</div> : null}
        </div>
        {showProject && clip.projectTitle ? (
          <p className="truncate text-xs text-fg-subtle">{clip.projectTitle}</p>
        ) : null}
        <div className="mt-auto flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-fg-muted">
          {clip.category ? <Badge tone="accent">{categoryLabel(clip.category)}</Badge> : null}
          {clip.status === "ready" ? <Badge tone="success">{CLIP_STATUS_LABELS.ready}</Badge> : null}
          {typeof clip.score === "number" ? (
            <span className="inline-flex items-center gap-1">
              relevância estimada <strong className="font-semibold text-fg">{Math.round(clip.score)}</strong>
            </span>
          ) : null}
          <time dateTime={clip.createdAt} className="ml-auto text-fg-subtle">
            {formatDate(clip.createdAt)}
          </time>
        </div>
      </div>
    </article>
  );
}
