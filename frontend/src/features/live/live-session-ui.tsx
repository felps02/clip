"use client";

import { Badge } from "@/components/ui/badge";
import { ElapsedTime } from "@/components/ui/relative-time";
import type { LiveSession } from "@/lib/api/types";
import { formatDuration, formatNumber } from "@/lib/format";
import { isActiveLiveSessionStatus, LIVE_SESSION_STATUS_TONES, liveSessionStatusLabel } from "@/lib/labels";
import { cn } from "@/lib/utils";

export function liveSessionTitle(session: Pick<LiveSession, "title">): string {
  return session.title?.trim() || "Sessão ao vivo";
}

export function LiveStatusBadge({ status, className }: { status: LiveSession["status"]; className?: string }) {
  return (
    <Badge
      tone={LIVE_SESSION_STATUS_TONES[status] ?? "neutral"}
      pulse={status === "live" || status === "ending"}
      className={className}
    >
      <span className="sr-only">Estado: </span>
      {liveSessionStatusLabel(status)}
    </Badge>
  );
}

/**
 * The companion's last heartbeat (OBS conectado, gravando, enviando, fila N, último erro). Nothing is
 * shown as a state before the first heartbeat, and an old heartbeat is flagged instead of being
 * presented as the current state. The API reports `online: false` 30 s after the last heartbeat, so
 * the age of the last contact is counted in seconds (never "agora") and keeps counting between polls.
 */
export function CompanionStateChips({ session, className }: { session: LiveSession; className?: string }) {
  const state = session.companionState;
  if (!state || !session.lastHeartbeatAt) {
    return <p className={cn("text-xs text-fg-muted", className)}>O companion ainda não informou o estado desta sessão.</p>;
  }
  const active = isActiveLiveSessionStatus(session.status);
  return (
    <ul aria-label="Estado do companion" className={cn("flex min-w-0 flex-wrap gap-1.5", className)}>
      {active && !session.online ? (
        <li className="min-w-0 max-w-full">
          <Badge tone="warning" className="max-w-full whitespace-normal rounded-md text-left">
            {/* One text run, so the relative time wraps with the sentence instead of becoming a column. */}
            <span>
              Sem sinal do companion · último contato <ElapsedTime value={session.lastHeartbeatAt} />
            </span>
          </Badge>
        </li>
      ) : null}
      <li>
        <Badge tone={state.obsConnected ? "success" : "neutral"}>{state.obsConnected ? "OBS conectado" : "OBS desconectado"}</Badge>
      </li>
      <li>
        <Badge
          tone={state.recording ? "success" : session.status === "live" ? "warning" : "neutral"}
          pulse={state.recording && active && session.online}
        >
          {state.recording ? "Gravando" : "Sem gravação"}
        </Badge>
      </li>
      {state.streaming && !state.recording ? (
        <li>
          <Badge tone="warning">Transmitindo sem gravar</Badge>
        </li>
      ) : null}
      {state.uploading ? (
        <li>
          <Badge tone="accent">Enviando</Badge>
        </li>
      ) : null}
      <li>
        <Badge tone={state.queueLength > 0 ? "info" : "neutral"}>Fila {formatNumber(state.queueLength)}</Badge>
      </li>
      {active && session.stopRequested ? (
        <li>
          <Badge tone="warning">Encerramento solicitado</Badge>
        </li>
      ) : null}
      {state.lastError ? (
        <li className="min-w-0 max-w-full">
          <Badge tone="danger" className="max-w-full whitespace-normal rounded-md text-left [overflow-wrap:anywhere]">
            <span className="min-w-0">Último erro: {state.lastError}</span>
          </Badge>
        </li>
      ) : null}
    </ul>
  );
}

/** Counters of a session: segments received/analysed/failed, highlights and recorded time. */
export function LiveSessionCounters({ session, className }: { session: LiveSession; className?: string }) {
  const stats = session.stats;
  const value = (count: number | undefined) => (typeof count === "number" ? formatNumber(count) : "—");
  const items: Array<{ label: string; value: string; danger?: boolean }> = [
    { label: "Segmentos recebidos", value: value(stats?.segmentsReceived) },
    { label: "Segmentos analisados", value: value(stats?.segmentsAnalyzed) },
    { label: "Segmentos com falha", value: value(stats?.segmentsFailed), danger: (stats?.segmentsFailed ?? 0) > 0 },
    { label: "Destaques", value: value(stats?.highlights) },
    { label: "Tempo gravado", value: typeof stats?.recordedSeconds === "number" ? formatDuration(stats.recordedSeconds) : "—" },
  ];
  return (
    <dl className={cn("grid grid-cols-2 gap-x-4 gap-y-2 sm:grid-cols-3 xl:grid-cols-5", className)}>
      {items.map((item) => (
        <div key={item.label} className="min-w-0">
          <dt className="text-[11px] leading-4 text-fg-subtle">{item.label}</dt>
          <dd className={cn("text-sm font-semibold tabular-nums", item.danger ? "text-danger" : "text-fg")}>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}
