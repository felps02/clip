"use client";

import { Captions, Download, FileArchive, Film, Pencil, Play, Sparkles } from "lucide-react";
import Image from "next/image";
import Link from "next/link";
import { useState, type MouseEvent } from "react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Spinner } from "@/components/ui/spinner";
import { openDownload } from "@/lib/download";
import { NO_SPEECH_SUBTITLES_TEXT, renderMissingSubtitles } from "@/features/projects/subtitle-notes";
import { describeError } from "@/lib/api/client";
import { useClip, useClipDownload } from "@/lib/api/hooks/use-clips";
import { clipsArchiveUrl } from "@/lib/api/projects";
import type { AspectRatio, ClipSummary } from "@/lib/api/types";
import { formatDuration } from "@/lib/format";
import { categoryLabel, CLIP_STATUS_LABELS, CLIP_STATUS_TONES } from "@/lib/labels";
import { cn } from "@/lib/utils";

const frameClass: Record<AspectRatio, string> = {
  "9:16": "aspect-[9/16]",
  "1:1": "aspect-square",
  "16:9": "aspect-video",
};

/** Inline player of a ready clip: the signed stream URL only comes with the clip's detail. */
function InlineClipPlayer({ clip }: { clip: ClipSummary }) {
  const detail = useClip(clip.id);
  const render = detail.data?.currentRender;
  const streamUrl = render?.status === "ready" ? render.streamUrl : null;
  if (detail.isError) {
    return (
      <div className="absolute inset-0 flex items-center justify-center bg-elevated p-4 text-center text-xs text-fg-muted" role="alert">
        Não foi possível carregar o vídeo. {describeError(detail.error)}
      </div>
    );
  }
  if (!streamUrl) {
    return (
      <div className="absolute inset-0 flex items-center justify-center bg-black/60">
        <Spinner className="size-6 text-fg" label="Carregando o clipe" />
      </div>
    );
  }
  return (
    <video
      className="absolute inset-0 h-full w-full bg-black object-contain"
      src={streamUrl}
      poster={clip.thumbnailUrl ?? undefined}
      controls
      autoPlay
      playsInline
      preload="metadata"
      aria-label={`Clipe ${clip.title}`}
    >
      Seu navegador não suporta a reprodução deste vídeo.
    </video>
  );
}

export interface ResultClipCardProps {
  clip: ClipSummary;
  playing: boolean;
  onPlay: (clip: ClipSummary) => void;
  /** Subtitles were requested for this project's clips (for the "no speech" note). */
  subtitlesRequested?: boolean;
}

/** A finished clip of the results screen: thumbnail → inline player, "Baixar" and "Editar". */
export function ResultClipCard({ clip, playing, onPlay, subtitlesRequested }: ResultClipCardProps) {
  const download = useClipDownload();
  const ready = clip.status === "ready";
  const noSubtitles = renderMissingSubtitles(subtitlesRequested, clip.currentRender);

  const startDownload = () => {
    download.mutate(
      { clipId: clip.id },
      { onSuccess: ({ url }) => openDownload(url), onError: (error) => toast.error(describeError(error)) },
    );
  };

  return (
    <article
      className="flex min-w-0 flex-col overflow-hidden rounded-lg border border-line bg-surface shadow-card"
      aria-label={clip.title}
    >
      <div className={cn("relative mx-auto w-full overflow-hidden bg-black", frameClass[clip.aspectRatio] ?? "aspect-video")}>
        {playing && ready ? (
          <InlineClipPlayer clip={clip} />
        ) : (
          <>
            {clip.thumbnailUrl ? (
              <Image src={clip.thumbnailUrl} alt="" fill unoptimized sizes="(min-width: 1024px) 25vw, 50vw" className="object-cover" />
            ) : (
              <div className="absolute inset-0 flex items-center justify-center bg-[radial-gradient(80%_80%_at_30%_20%,rgba(179,245,59,0.12),transparent),linear-gradient(160deg,#1c2027,#0b0d10)]">
                <Film className="size-8 text-fg-subtle" aria-hidden="true" />
              </div>
            )}
            <span className="absolute bottom-2 right-2 rounded-xs bg-black/70 px-1.5 py-0.5 font-mono text-[11px] font-medium tabular-nums text-fg">
              {formatDuration(clip.durationSeconds)}
            </span>
            {ready ? (
              <button
                type="button"
                onClick={() => onPlay(clip)}
                className="group absolute inset-0 flex items-center justify-center bg-black/10 transition-colors duration-200 hover:bg-black/30 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-accent/70"
                aria-label={`Reproduzir ${clip.title}`}
              >
                <span className="flex size-12 items-center justify-center rounded-full bg-accent text-accent-fg shadow-glow transition-transform duration-200 group-hover:scale-105">
                  <Play className="ml-0.5 size-5" aria-hidden="true" />
                </span>
              </button>
            ) : (
              <Badge tone={CLIP_STATUS_TONES[clip.status]} pulse={clip.status === "rendering"} className="absolute left-2 top-2">
                {CLIP_STATUS_LABELS[clip.status]}
              </Badge>
            )}
          </>
        )}
      </div>
      <div className="flex flex-1 flex-col gap-2 p-3">
        <h3 className="line-clamp-2 text-sm font-semibold leading-snug">{clip.title}</h3>
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-fg-muted">
          {clip.category ? <Badge tone="accent">{categoryLabel(clip.category)}</Badge> : null}
          <span className="font-mono tabular-nums">{formatDuration(clip.durationSeconds)}</span>
          {typeof clip.score === "number" ? (
            <span>
              relevância estimada <strong className="font-semibold text-fg">{Math.round(clip.score)}</strong>
            </span>
          ) : null}
        </div>
        {noSubtitles ? (
          <p className="flex items-start gap-1.5 text-xs text-fg-muted">
            <Captions className="mt-0.5 size-3.5 shrink-0 text-fg-subtle" aria-hidden="true" />
            {NO_SPEECH_SUBTITLES_TEXT}
          </p>
        ) : null}
        <div className="mt-auto flex flex-wrap gap-2 pt-1">
          <Button
            size="sm"
            variant="secondary"
            onClick={startDownload}
            loading={download.isPending}
            disabled={!ready}
            leftIcon={<Download aria-hidden="true" />}
            aria-label={`Baixar ${clip.title}`}
          >
            Baixar
          </Button>
          <Link href={`/clips/${clip.id}`} className={buttonVariants({ variant: "ghost", size: "sm" })} aria-label={`Editar ${clip.title}`}>
            <Pencil aria-hidden="true" />
            Editar
          </Link>
        </div>
      </div>
    </article>
  );
}

export const ARCHIVE_EMPTY_TEXT = "Nenhum clipe pronto para baixar agora.";

export interface ArchiveDownloadLinkProps {
  projectId: string;
  /**
   * Runs right before the download: an API call (which refreshes an expired session on the way)
   * that says whether the project still has a ready clip. Throws the API error otherwise.
   */
  checkReady: () => Promise<boolean>;
}

/**
 * "Baixar todos (.zip)": `GET /api/projects/{id}/clips/archive`, streamed by the API as an
 * attachment. It stays a real link (middle-click, "abrir em nova aba"), but a plain click checks
 * with the API first: a top-level navigation cannot refresh the session (the access token lasts
 * 15 min and a finished results page makes no other request), so an expired token or a project
 * without ready clips would replace the app with the API's raw 401/404 JSON.
 */
export function ArchiveDownloadLink({ projectId, checkReady }: ArchiveDownloadLinkProps) {
  const [preparing, setPreparing] = useState(false);
  const href = clipsArchiveUrl(projectId);

  const onClick = async (event: MouseEvent<HTMLAnchorElement>) => {
    // New tab/window and the other modified clicks keep the browser's own behaviour.
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) return;
    event.preventDefault();
    if (preparing) return;
    setPreparing(true);
    try {
      if (await checkReady()) openDownload(href);
      else toast.error(ARCHIVE_EMPTY_TEXT);
    } catch (error) {
      toast.error(describeError(error));
    } finally {
      setPreparing(false);
    }
  };

  return (
    <a href={href} onClick={onClick} aria-busy={preparing || undefined} className={buttonVariants({ variant: "primary" })}>
      {preparing ? <Spinner className="size-4" /> : <FileArchive aria-hidden="true" />}
      Baixar todos (.zip)
    </a>
  );
}

/** Small banner above the results when the recording had no speech at all. */
export function NoSpeechBanner() {
  return (
    <p className="mb-4 flex items-start gap-2 rounded-md border border-line bg-surface px-3 py-2 text-sm text-fg-muted">
      <Sparkles className="mt-0.5 size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
      {NO_SPEECH_SUBTITLES_TEXT}: a análise não encontrou fala nesta gravação, então os clipes saem sem legendas.
    </p>
  );
}
