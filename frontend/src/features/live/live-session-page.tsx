"use client";

import { useQueryClient } from "@tanstack/react-query";
import { Clapperboard, FolderOpen, ListOrdered, Radio, RefreshCw, Square } from "lucide-react";
import Link from "next/link";
import { useParams } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { ElapsedTime, RelativeTime } from "@/components/ui/relative-time";
import { CardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { ClipCard } from "@/components/video/clip-card";
import { CompanionStateChips, LiveSessionCounters, LiveStatusBadge, liveSessionTitle } from "@/features/live/live-session-ui";
import { describeError, isApiError } from "@/lib/api/client";
import { useLiveSession, useStopLiveSession } from "@/lib/api/hooks/use-live";
import { queryKeys } from "@/lib/api/query-keys";
import type { LiveSegment } from "@/lib/api/types";
import { formatDateTime, formatDuration, formatNumber, formatSeconds, pluralize } from "@/lib/format";
import { isActiveLiveSessionStatus, LIVE_SEGMENT_STATUS_TONES, liveSegmentStatusLabel, type Tone } from "@/lib/labels";
import { cn } from "@/lib/utils";

/** The API returns at most this many segments and highlights per session (newest first). */
const DETAIL_LIST_LIMIT = 50;

const SEGMENT_DOT_TONES: Record<Tone, string> = {
  neutral: "border-line-strong bg-elevated text-fg-muted",
  accent: "border-accent/40 bg-accent/10 text-accent",
  success: "border-success/40 bg-success/10 text-success",
  warning: "border-warning/40 bg-warning/10 text-warning",
  danger: "border-danger/40 bg-danger/10 text-danger",
  info: "border-info/40 bg-info/10 text-info",
};

function SegmentRow({ segment, last }: { segment: LiveSegment; last: boolean }) {
  const tone = LIVE_SEGMENT_STATUS_TONES[segment.status] ?? "neutral";
  const end = segment.startOffsetSeconds + segment.durationSeconds;
  return (
    <li className="relative flex gap-3 pb-5 last:pb-0">
      {last ? null : <span className="absolute bottom-0 left-4 top-9 w-px -translate-x-1/2 bg-line" aria-hidden="true" />}
      <span
        className={cn(
          "flex h-8 min-w-8 shrink-0 items-center justify-center rounded-full border px-1.5 font-mono text-xs font-semibold tabular-nums",
          SEGMENT_DOT_TONES[tone],
        )}
        aria-hidden="true"
      >
        {segment.sequence}
      </span>
      <div className="min-w-0 flex-1 pt-1">
        <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
          <p className="text-sm font-medium">Segmento {segment.sequence}</p>
          <Badge tone={tone} pulse={segment.status === "analyzing"}>
            {liveSegmentStatusLabel(segment.status)}
          </Badge>
        </div>
        <p className="mt-0.5 text-xs tabular-nums text-fg-muted">
          {formatDuration(segment.startOffsetSeconds)}–{formatDuration(end)} da gravação · {formatSeconds(segment.durationSeconds)}
        </p>
        {segment.error ? <p className="mt-1 text-xs text-danger [overflow-wrap:anywhere]">Erro: {segment.error}</p> : null}
      </div>
    </li>
  );
}

export function LiveSessionPage() {
  const params = useParams<{ id: string }>();
  const sessionId = params.id;
  const queryClient = useQueryClient();
  const query = useLiveSession(sessionId);
  const stop = useStopLiveSession(sessionId);
  const [confirmStop, setConfirmStop] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  // Focus target after "Encerrar sessão" is confirmed: the button that opened the dialog goes away.
  const refreshButtonRef = useRef<HTMLButtonElement | null>(null);
  const status = query.data?.session.status;

  // A session that just reached a final state changes the list, the library and the dashboard.
  const previousStatus = useRef(status);
  useEffect(() => {
    const before = previousStatus.current;
    previousStatus.current = status;
    if (before && status && isActiveLiveSessionStatus(before) && !isActiveLiveSessionStatus(status)) {
      void queryClient.invalidateQueries({ queryKey: queryKeys.live.sessionLists });
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    }
  }, [status, queryClient]);

  const refresh = async () => {
    setRefreshing(true);
    try {
      await query.refetch();
    } finally {
      setRefreshing(false);
    }
  };

  if (query.isError && !query.data) {
    const httpStatus = isApiError(query.error) ? query.error.status : 0;
    const notFound = httpStatus === 404;
    const unavailable = httpStatus === 501;
    return (
      <>
        <PageHeader
          title={notFound ? "Sessão não encontrada" : unavailable ? "Captura ao vivo indisponível" : "Erro ao carregar a sessão"}
          backHref="/live"
          backLabel="Lives"
        />
        {unavailable ? (
          <Alert variant="info" title="Captura ao vivo ainda não disponível neste ambiente">
            O servidor ainda não oferece sessões ao vivo.
          </Alert>
        ) : (
          <Alert
            variant={notFound ? "warning" : "danger"}
            action={
              notFound ? undefined : (
                <Button size="sm" variant="secondary" onClick={() => query.refetch()} loading={query.isFetching}>
                  Tentar novamente
                </Button>
              )
            }
          >
            {notFound ? "Esta sessão não existe ou não pertence à sua conta." : describeError(query.error)}
          </Alert>
        )}
      </>
    );
  }

  if (query.isPending || !query.data) {
    return (
      <div className="space-y-6" role="status" aria-busy="true">
        <span className="sr-only">Carregando a sessão…</span>
        <Skeleton className="h-8 w-64 max-w-full" />
        <CardSkeleton />
        <CardSkeleton />
      </div>
    );
  }

  const { session, segments, highlights, candidateCount } = query.data;
  const active = isActiveLiveSessionStatus(session.status);
  const canStop = (session.status === "waiting" || session.status === "live") && !session.stopRequested;
  const projectHref = `/projects/${encodeURIComponent(session.projectId)}`;
  const title = liveSessionTitle(session);
  const segmentsReceived = session.stats?.segmentsReceived ?? segments.length;

  const confirm = () => {
    stop.mutate(undefined, {
      onSuccess: () => {
        toast.success("Encerramento solicitado");
        setConfirmStop(false);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  return (
    <>
      <PageHeader
        backHref="/live"
        backLabel="Lives"
        eyebrow="Sessão ao vivo"
        heading={
          // Up to two lines (titles come from the companion and can be long); the full title on hover.
          <h1 className="line-clamp-2 text-2xl font-semibold tracking-tight [overflow-wrap:anywhere] sm:text-3xl" title={title}>
            {title}
          </h1>
        }
        description={
          <span className="flex flex-wrap items-center gap-x-3 gap-y-1">
            <LiveStatusBadge status={session.status} />
            {session.deviceName ? <span className="[overflow-wrap:anywhere]">{session.deviceName}</span> : null}
            {session.startedAt ? <span>início {formatDateTime(session.startedAt)}</span> : null}
            {active && session.lastSegmentAt ? (
              <span>
                último segmento <RelativeTime value={session.lastSegmentAt} />
              </span>
            ) : null}
            {session.endedAt ? <span>fim {formatDateTime(session.endedAt)}</span> : null}
            {session.segmentSeconds ? <span>segmentos de {formatSeconds(session.segmentSeconds)}</span> : null}
          </span>
        }
        actions={
          <>
            <Button ref={refreshButtonRef} variant="ghost" onClick={refresh} loading={refreshing} leftIcon={<RefreshCw aria-hidden="true" />}>
              Atualizar
            </Button>
            <Link href={projectHref} className={cn(buttonVariants({ variant: "secondary" }))}>
              <FolderOpen aria-hidden="true" />
              Ver projeto
            </Link>
            {canStop ? (
              <Button variant="danger" onClick={() => setConfirmStop(true)} leftIcon={<Square aria-hidden="true" />}>
                Encerrar sessão
              </Button>
            ) : null}
          </>
        }
      />

      {query.isError ? (
        <Alert variant="warning" className="mb-6" title="Não foi possível atualizar a sessão">
          {describeError(query.error)} Os dados abaixo são da última consulta.
        </Alert>
      ) : null}
      {session.status === "error" ? (
        <Alert variant="danger" className="mb-6" title="A sessão terminou com erro">
          {session.endReason ? `Motivo: ${session.endReason}.` : "O motivo não foi informado."} Os segmentos já analisados e os destaques
          continuam disponíveis.
        </Alert>
      ) : null}
      {session.stopRequested && (session.status === "waiting" || session.status === "live") ? (
        <Alert variant="info" className="mb-6" title="Encerramento solicitado">
          O companion recebe o pedido no próximo heartbeat (a cada 10 s), envia o que falta e encerra a sessão.
        </Alert>
      ) : null}
      {session.status === "ended" && session.endReason ? (
        <p className="mb-6 text-sm text-fg-muted [overflow-wrap:anywhere]">Motivo do encerramento: {session.endReason}</p>
      ) : null}

      <section aria-labelledby="estado-da-sessao">
        <h2 id="estado-da-sessao" className="sr-only">
          Estado da sessão
        </h2>
        <div className="grid gap-6 lg:grid-cols-2">
          <Card className="min-w-0">
            <CardHeader>
              <CardTitle className="flex items-center gap-2">
                <Radio className="size-4 text-accent" aria-hidden="true" />
                Companion
              </CardTitle>
              <CardDescription>
                {active ? "Estado informado no último heartbeat (a cada 10 s)." : "Último estado informado pelo companion."}
                {session.lastHeartbeatAt ? (
                  <>
                    {" "}
                    Último contato <ElapsedTime value={session.lastHeartbeatAt} />.
                  </>
                ) : null}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <CompanionStateChips session={session} />
            </CardContent>
          </Card>
          <Card className="min-w-0">
            <CardHeader>
              <CardTitle>Contadores</CardTitle>
              <CardDescription>
                {active
                  ? "Atualizados a cada 5 s enquanto a sessão estiver ativa."
                  : "Sessão finalizada: a página não é mais atualizada automaticamente."}
              </CardDescription>
            </CardHeader>
            <CardContent>
              <LiveSessionCounters session={session} />
            </CardContent>
          </Card>
        </div>
      </section>

      <section className="mt-8" aria-labelledby="destaques-da-sessao">
        <div className="mb-2 flex flex-wrap items-center justify-between gap-3">
          <h2 id="destaques-da-sessao" className="flex items-center gap-2 text-xl font-semibold">
            <Clapperboard className="size-5 text-accent" aria-hidden="true" />
            Destaques
          </h2>
          <Link href={projectHref} className={cn(buttonVariants({ variant: "ghost", size: "sm" }))}>
            Ver todos no projeto
          </Link>
        </div>
        <p className="mb-4 text-sm text-fg-muted">
          {pluralize(candidateCount, "momento detectado", "momentos detectados")} · {pluralize(highlights.length, "clipe", "clipes")}. Um
          destaque aparece cerca de um segmento depois do momento, mais o tempo de processamento.
        </p>
        {highlights.length === 0 ? (
          <EmptyState
            compact
            icon={<Clapperboard />}
            title="Nenhum destaque ainda"
            description={
              active
                ? "Os clipes aparecem aqui conforme os segmentos são analisados."
                : "Nenhum clipe foi gerado nesta sessão."
            }
          />
        ) : (
          <>
            <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
              {highlights.map((clip) => (
                <ClipCard key={clip.id} clip={clip} />
              ))}
            </div>
            {highlights.length >= DETAIL_LIST_LIMIT ? (
              <p className="mt-3 text-xs text-fg-muted">
                Mostrando os {formatNumber(DETAIL_LIST_LIMIT)} clipes mais recentes.{" "}
                <Link href={projectHref} className="text-accent underline-offset-4 hover:underline">
                  Ver todos no projeto
                </Link>
              </p>
            ) : null}
          </>
        )}
      </section>

      <section className="mt-8" aria-labelledby="segmentos-da-sessao">
        <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
          <h2 id="segmentos-da-sessao" className="flex items-center gap-2 text-xl font-semibold">
            <ListOrdered className="size-5 text-accent" aria-hidden="true" />
            Segmentos
          </h2>
          {segmentsReceived > segments.length ? (
            <p className="text-xs text-fg-muted">
              Mostrando os {formatNumber(segments.length)} mais recentes de {formatNumber(segmentsReceived)}.
            </p>
          ) : null}
        </div>
        {segments.length === 0 ? (
          <EmptyState
            compact
            icon={<ListOrdered />}
            title="Nenhum segmento recebido ainda"
            description={
              active
                ? `O primeiro segmento chega cerca de ${formatSeconds(session.segmentSeconds || 60)} depois do início da gravação.`
                : "Esta sessão terminou sem receber segmentos."
            }
          />
        ) : (
          <Card>
            <CardContent className="pt-5">
              <ol aria-label="Linha do tempo dos segmentos, do mais recente ao mais antigo">
                {segments.map((segment, index) => (
                  <SegmentRow key={segment.id} segment={segment} last={index === segments.length - 1} />
                ))}
              </ol>
            </CardContent>
          </Card>
        )}
      </section>

      <ConfirmDialog
        open={confirmStop}
        onOpenChange={setConfirmStop}
        title="Encerrar esta sessão?"
        description="O companion recebe o pedido no próximo heartbeat (até cerca de 10 s), envia o segmento atual e a fila e encerra a sessão. O que já foi recebido continua sendo analisado."
        confirmLabel="Encerrar sessão"
        cancelLabel="Voltar"
        destructive
        loading={stop.isPending}
        onConfirm={confirm}
        // "Voltar" returns to "Encerrar sessão"; once the stop is requested that button is gone.
        returnFocus={{ afterConfirm: refreshButtonRef, fallback: refreshButtonRef }}
      />
    </>
  );
}
