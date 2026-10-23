"use client";

import { Captions, ChevronDown, ChevronUp, Clapperboard, ExternalLink, Play } from "lucide-react";
import Link from "next/link";
import { useId, useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { VideoPlayer } from "@/components/video/video-player";
import type { Candidate } from "@/lib/api/types";
import { formatDuration } from "@/lib/format";
import { CANDIDATE_STATUS_LABELS, categoryLabel, contributorLabel } from "@/lib/labels";
import { cn } from "@/lib/utils";

export interface CandidateCardProps {
  candidate: Candidate;
  onGenerateClip?: (candidate: Candidate) => void;
  generating?: boolean;
  className?: string;
  /** Why the clip of this moment has no subtitles (e.g. "Nenhuma fala detectada — legendas não geradas"). */
  subtitleNote?: string | null;
}

export const SCORE_DISCLAIMER = "Estimativa interna (0–100) com base nos sinais disponíveis. Não é uma previsão de alcance.";

/**
 * The score reads as "relevância estimada N de 100" and is always described by the disclaimer
 * (`aria-describedby` to an sr-only copy), so assistive technology never gets a bare number.
 * The tooltip trigger is a real button, reachable by keyboard; hover/focus shows the same text.
 */
function ScoreMeter({ score }: { score: number }) {
  const value = Math.max(0, Math.min(100, Math.round(score)));
  const descriptionId = useId();
  return (
    <div className="flex items-center gap-2">
      <Tooltip>
        <TooltipTrigger asChild>
          <button
            type="button"
            aria-label={`Relevância estimada ${value} de 100`}
            aria-describedby={descriptionId}
            className="flex cursor-help items-center gap-2 rounded-xs text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
          >
            <span className="h-1.5 w-20 overflow-hidden rounded-full bg-line" aria-hidden="true">
              <span className="block h-full rounded-full bg-accent" style={{ width: `${value}%` }} />
            </span>
            <span className="text-xs text-fg-muted">
              relevância estimada <strong className="font-semibold text-fg tabular-nums">{value}</strong>
            </span>
          </button>
        </TooltipTrigger>
        <TooltipContent>{SCORE_DISCLAIMER}</TooltipContent>
      </Tooltip>
      <span id={descriptionId} className="sr-only">
        {SCORE_DISCLAIMER}
      </span>
    </div>
  );
}

export function CandidateCard({ candidate, onGenerateClip, generating, className, subtitleNote }: CandidateCardProps) {
  const [previewOpen, setPreviewOpen] = useState(false);
  const [transcriptOpen, setTranscriptOpen] = useState(false);
  const contributors = candidate.signals?.contributors ?? [];
  const transcript = candidate.transcript?.trim() ?? "";
  const longTranscript = transcript.length > 220;

  return (
    <article
      className={cn(
        "rounded-lg border border-line bg-surface p-4 shadow-card transition-colors duration-200 hover:border-line-strong sm:p-5",
        className,
      )}
    >
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="flex min-w-0 items-start gap-3">
          <span className="flex size-9 shrink-0 items-center justify-center rounded-md border border-line bg-elevated font-mono text-sm font-semibold text-fg-muted">
            #{candidate.rank}
          </span>
          <div className="min-w-0 space-y-1.5">
            <div className="flex flex-wrap items-center gap-2">
              <Badge tone="accent">{categoryLabel(candidate.category)}</Badge>
              <Badge tone={candidate.status === "rendered" ? "success" : candidate.status === "discarded" ? "neutral" : "outline"}>
                {CANDIDATE_STATUS_LABELS[candidate.status] ?? candidate.status}
              </Badge>
            </div>
            <ScoreMeter score={candidate.score} />
          </div>
        </div>
        <dl className="flex items-center gap-4 text-xs text-fg-muted">
          <div>
            <dt className="text-[10px] uppercase tracking-wide text-fg-subtle">Duração</dt>
            <dd className="font-mono text-sm text-fg tabular-nums">{formatDuration(candidate.durationSeconds)}</dd>
          </div>
          <div>
            <dt className="text-[10px] uppercase tracking-wide text-fg-subtle">Trecho</dt>
            <dd className="font-mono text-sm text-fg tabular-nums">
              {formatDuration(candidate.startSeconds)} – {formatDuration(candidate.endSeconds)}
            </dd>
          </div>
        </dl>
      </div>

      {candidate.reasoning ? <p className="mt-4 text-sm leading-relaxed text-fg">{candidate.reasoning}</p> : null}

      {transcript ? (
        <div className="mt-3 rounded-md border border-line bg-canvas/50 p-3">
          <p className="mb-1 text-[10px] font-medium uppercase tracking-wide text-fg-subtle">Transcrição</p>
          <p className={cn("text-sm italic leading-relaxed text-fg-muted", !transcriptOpen && longTranscript && "line-clamp-3")}>
            “{transcript}”
          </p>
          {longTranscript ? (
            <Button
              variant="link"
              size="sm"
              className="mt-1 h-auto text-xs"
              onClick={() => setTranscriptOpen((open) => !open)}
              rightIcon={transcriptOpen ? <ChevronUp aria-hidden="true" /> : <ChevronDown aria-hidden="true" />}
            >
              {transcriptOpen ? "Ver menos" : "Ver transcrição completa"}
            </Button>
          ) : null}
        </div>
      ) : null}

      {subtitleNote ? (
        <p className="mt-3 flex items-start gap-1.5 text-xs text-fg-muted">
          <Captions className="mt-0.5 size-3.5 shrink-0 text-fg-subtle" aria-hidden="true" />
          {subtitleNote}
        </p>
      ) : null}

      {contributors.length > 0 ? (
        <ul className="mt-3 flex flex-wrap gap-1.5" aria-label="Sinais que contribuíram">
          {contributors.map((id) => (
            <li key={id}>
              <Badge tone="outline">{contributorLabel(id)}</Badge>
            </li>
          ))}
        </ul>
      ) : null}

      {previewOpen ? (
        <div className="mt-4">
          {candidate.previewable && candidate.previewUrl ? (
            <VideoPlayer
              src={candidate.previewUrl}
              fragment={{ start: candidate.startSeconds, end: candidate.endSeconds }}
              clampEnd={candidate.endSeconds}
              aspectRatio="16:9"
            />
          ) : (
            <p className="rounded-md border border-dashed border-line-strong px-4 py-6 text-center text-sm text-fg-muted">
              Pré-visualização indisponível para este trecho. Gere o clipe para assistir ao resultado.
            </p>
          )}
        </div>
      ) : null}

      <div className="mt-4 flex flex-wrap items-center gap-2">
        <Button
          variant="secondary"
          size="sm"
          onClick={() => setPreviewOpen((open) => !open)}
          leftIcon={<Play aria-hidden="true" />}
          aria-expanded={previewOpen}
        >
          {previewOpen ? "Ocultar prévia" : "Pré-visualizar"}
        </Button>
        {candidate.clipId ? (
          <Link href={`/clips/${candidate.clipId}`} className={buttonVariants({ variant: "outline", size: "sm" })}>
            <ExternalLink aria-hidden="true" />
            Abrir clipe
          </Link>
        ) : null}
        {onGenerateClip ? (
          <Button
            size="sm"
            variant={candidate.clipId ? "ghost" : "primary"}
            onClick={() => onGenerateClip(candidate)}
            loading={generating}
            leftIcon={<Clapperboard aria-hidden="true" />}
          >
            {candidate.clipId ? "Gerar outro clipe" : "Gerar clipe"}
          </Button>
        ) : null}
      </div>
    </article>
  );
}
