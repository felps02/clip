"use client";

import { AlertTriangle, Ban, Info, RefreshCw, XCircle } from "lucide-react";
import { useState } from "react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Progress } from "@/components/ui/progress";
import type { Job, JobEvent, JobSummary } from "@/lib/api/types";
import { formatDateTime, formatTime } from "@/lib/format";
import {
  isActiveJobStatus,
  isCancellableJobStatus,
  isTerminalJobStatus,
  JOB_EVENT_LEVEL_LABELS,
  JOB_KIND_LABELS,
  JOB_STATUS_TONES,
  jobStatusLabel,
} from "@/lib/labels";
import { cn } from "@/lib/utils";

export interface JobStatusProps {
  job: Job | JobSummary;
  onCancel?: () => void;
  cancelling?: boolean;
  onRetry?: () => void;
  retrying?: boolean;
  retryLabel?: string;
  className?: string;
  compact?: boolean;
}

function isFullJob(job: Job | JobSummary): job is Job {
  return "attempt" in job;
}

const levelIcon: Record<JobEvent["level"], typeof Info> = {
  info: Info,
  warning: AlertTriangle,
  error: XCircle,
};

export function JobStatusBadge({ status }: { status: Job["status"] }) {
  return (
    <Badge tone={JOB_STATUS_TONES[status] ?? "neutral"} pulse={isActiveJobStatus(status)}>
      {jobStatusLabel(status)}
    </Badge>
  );
}

export function JobStatus({ job, onCancel, cancelling, onRetry, retrying, retryLabel = "Analisar novamente", className, compact }: JobStatusProps) {
  const [eventsOpen, setEventsOpen] = useState(false);
  const active = isActiveJobStatus(job.status);
  const terminal = isTerminalJobStatus(job.status);
  const failed = job.status === "failed";
  const full = isFullJob(job) ? job : null;
  const events = full?.events ?? [];
  const indeterminate = active && (job.progress ?? 0) <= 0;
  const kindLabel = JOB_KIND_LABELS[job.kind] ?? job.kind;

  return (
    <section className={cn("rounded-lg border border-line bg-surface p-4 shadow-card sm:p-5", className)}>
      {/* Only status transitions are announced; progress, messages and timestamps change on every
          2 s poll and stay silent (the progressbar carries the percentage). */}
      <p className="sr-only" aria-live="polite" aria-atomic="true">
        {`${kindLabel}: ${jobStatusLabel(job.status)}`}
      </p>
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="text-sm font-semibold">{kindLabel}</h3>
          <JobStatusBadge status={job.status} />
          {full ? <span className="text-xs text-fg-subtle">tentativa {full.attempt}</span> : null}
        </div>
        <div className="flex items-center gap-2">
          {onCancel && isCancellableJobStatus(job.status) ? (
            <Button variant="danger" size="sm" onClick={onCancel} loading={cancelling} leftIcon={<Ban aria-hidden="true" />}>
              Cancelar
            </Button>
          ) : null}
          {onRetry && terminal ? (
            <Button variant="secondary" size="sm" onClick={onRetry} loading={retrying} leftIcon={<RefreshCw aria-hidden="true" />}>
              {retryLabel}
            </Button>
          ) : null}
        </div>
      </div>

      <div className="mt-4 space-y-2">
        <div className="flex items-center justify-between text-xs text-fg-muted">
          <span className="truncate">{job.statusMessage || (active ? "Processando…" : jobStatusLabel(job.status))}</span>
          <span className="font-mono tabular-nums">{Math.round(job.progress ?? 0)}%</span>
        </div>
        <Progress
          value={job.progress ?? 0}
          tone={failed ? "danger" : job.status === "cancelled" ? "warning" : job.status === "completed" ? "success" : "accent"}
          indeterminate={indeterminate}
          label="Progresso do trabalho"
        />
      </div>

      {failed ? (
        <div className="mt-4 rounded-md border border-danger/30 bg-danger/10 p-3 text-sm" role="alert">
          <p className="font-medium text-danger">O processamento falhou</p>
          <p className="mt-0.5 text-fg-muted">{full?.errorMessage || "Não foi possível concluir este trabalho."}</p>
          {full?.errorCode ? <p className="mt-1 font-mono text-xs text-fg-subtle">código: {full.errorCode}</p> : null}
        </div>
      ) : null}

      {!compact && full ? (
        <dl className="mt-4 grid grid-cols-2 gap-x-4 gap-y-2 text-xs sm:grid-cols-3">
          <div>
            <dt className="text-fg-subtle">Na fila em</dt>
            <dd className="text-fg-muted">{formatDateTime(full.queuedAt)}</dd>
          </div>
          <div>
            <dt className="text-fg-subtle">Início</dt>
            <dd className="text-fg-muted">{formatDateTime(full.startedAt)}</dd>
          </div>
          <div>
            <dt className="text-fg-subtle">Fim</dt>
            <dd className="text-fg-muted">{formatDateTime(full.finishedAt)}</dd>
          </div>
        </dl>
      ) : null}

      {!compact && events.length > 0 ? (
        <div className="mt-4 border-t border-line pt-3">
          <button
            type="button"
            className="flex w-full items-center justify-between text-left text-xs font-medium text-fg-muted hover:text-fg"
            onClick={() => setEventsOpen((open) => !open)}
            aria-expanded={eventsOpen}
          >
            <span>Eventos ({events.length})</span>
            <span>{eventsOpen ? "ocultar" : "mostrar"}</span>
          </button>
          {eventsOpen ? (
            <ol className="mt-2 max-h-64 space-y-1.5 overflow-y-auto text-xs scrollbar-thin">
              {events.map((event, index) => {
                const Icon = levelIcon[event.level] ?? Info;
                return (
                  <li key={event.id ?? `${event.createdAt}-${index}`} className="flex items-start gap-2">
                    <time dateTime={event.createdAt} className="shrink-0 font-mono text-fg-subtle tabular-nums">
                      {formatTime(event.createdAt)}
                    </time>
                    <Icon
                      className={cn(
                        "mt-0.5 size-3.5 shrink-0",
                        event.level === "error" ? "text-danger" : event.level === "warning" ? "text-warning" : "text-fg-subtle",
                      )}
                      aria-hidden="true"
                    />
                    <span className="text-fg-muted">
                      <span className="sr-only">{JOB_EVENT_LEVEL_LABELS[event.level] ?? event.level}: </span>
                      {event.message}
                      {event.toStatus ? <span className="text-fg-subtle"> → {jobStatusLabel(event.toStatus)}</span> : null}
                    </span>
                  </li>
                );
              })}
            </ol>
          ) : null}
        </div>
      ) : null}
    </section>
  );
}
