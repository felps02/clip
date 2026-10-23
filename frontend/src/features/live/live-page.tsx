"use client";

import { useQueryClient } from "@tanstack/react-query";
import { ChevronRight, Copy, MonitorPlay, Radio, RefreshCw, Trash2 } from "lucide-react";
import Link from "next/link";
import { Fragment, useEffect, useRef, useState, type FormEvent, type ReactNode } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Pagination } from "@/components/ui/pagination";
import { RelativeTime } from "@/components/ui/relative-time";
import { Skeleton } from "@/components/ui/skeleton";
import { CompanionSetupGuide } from "@/features/live/companion-setup-guide";
import { copyText, selectContents } from "@/features/live/copy-text";
import { CompanionStateChips, LiveSessionCounters, LiveStatusBadge, liveSessionTitle } from "@/features/live/live-session-ui";
import { describeError, isApiError } from "@/lib/api/client";
import {
  liveSessionsNeedPolling,
  useCompanionDevices,
  useCreateCompanionToken,
  useLiveSessions,
  useRevokeCompanionDevice,
} from "@/lib/api/hooks/use-live";
import { queryKeys } from "@/lib/api/query-keys";
import type { CompanionDevice, LiveSession } from "@/lib/api/types";
import { formatDateTime } from "@/lib/format";
import { isActiveLiveSessionStatus } from "@/lib/labels";
import { cn } from "@/lib/utils";

export const TOKEN_COPY_FAILED_TEXT = "Não foi possível copiar automaticamente. O token está selecionado: copie manualmente (Ctrl+C).";
/** Stays next to the token after a failed copy (e.g. an http LAN address without clipboard access). */
export const TOKEN_MANUAL_COPY_HINT = "Copie manualmente: o token já está selecionado (Ctrl+C, ou toque e segure e escolha Copiar).";
const DEFAULT_DEVICE_NAME = "Meu computador";

/** 404/501: this environment has no live capture endpoints. */
export function isLiveUnavailableError(error: unknown): boolean {
  return isApiError(error) && (error.status === 404 || error.status === 501);
}

/** Creates a device token and shows it once, selected, with a copy button. */
function TokenIssuer() {
  const [name, setName] = useState("");
  const [issued, setIssued] = useState<{ token: string; deviceName: string } | null>(null);
  const [copied, setCopied] = useState(false);
  const [copyFailed, setCopyFailed] = useState(false);
  const [confirmHide, setConfirmHide] = useState(false);
  const tokenRef = useRef<HTMLInputElement | null>(null);
  const nameRef = useRef<HTMLInputElement | null>(null);
  const focusNameAfterHide = useRef(false);
  const create = useCreateCompanionToken();

  // A new token arrives selected: Ctrl+C works right away, even where copying from code is not allowed.
  // Once it is hidden, focus returns to the name field instead of being lost.
  useEffect(() => {
    if (issued) {
      selectContents(tokenRef.current);
    } else if (focusNameAfterHide.current) {
      focusNameAfterHide.current = false;
      nameRef.current?.focus();
    }
  }, [issued]);

  const generate = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    if (create.isPending) return;
    const deviceName = name.trim() || DEFAULT_DEVICE_NAME;
    create.mutate(deviceName, {
      onSuccess: (result) => {
        setIssued({ token: result.token, deviceName: result.device?.name || deviceName });
        // The token lives only in this component's state until it is hidden: the finished mutation
        // (whose data holds it) leaves the query client's mutation cache now (gcTime 0).
        create.reset();
        setCopied(false);
        setCopyFailed(false);
        setName("");
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const copy = async () => {
    if (!issued) return;
    const result = await copyText(issued.token, tokenRef.current);
    if (result === "failed") {
      selectContents(tokenRef.current);
      setCopyFailed(true);
      toast.error(TOKEN_COPY_FAILED_TEXT);
      return;
    }
    setCopied(true);
    setCopyFailed(false);
    toast.success("Token copiado");
  };

  const hideNow = () => {
    focusNameAfterHide.current = true;
    setConfirmHide(false);
    setIssued(null);
  };

  // The token is shown only once: hiding it before copying asks for confirmation.
  const hide = () => {
    if (copied) hideNow();
    else setConfirmHide(true);
  };

  if (issued) {
    return (
      <>
        <Alert variant="warning" title="Token gerado — copie agora">
          <p>
            Token de “{issued.deviceName}”. Ele aparece só desta vez: guarde-o como uma senha, porque quem tiver o token pode enviar
            gravações para a sua conta. Se perder, gere outro e revogue este dispositivo.
          </p>
          <Input
            ref={tokenRef}
            readOnly
            value={issued.token}
            aria-label="Token do dispositivo"
            onFocus={(event) => event.currentTarget.select()}
            // A manual copy (Ctrl+C, context menu) counts as copied too.
            onCopy={() => {
              setCopied(true);
              setCopyFailed(false);
            }}
            spellCheck={false}
            autoComplete="off"
            className="mt-2 font-mono text-xs"
          />
          {copyFailed ? (
            <p role="status" className="mt-2 text-xs text-warning">
              {TOKEN_MANUAL_COPY_HINT}
            </p>
          ) : null}
          <div className="mt-2 flex flex-wrap gap-2">
            <Button size="sm" variant="secondary" onClick={copy} leftIcon={<Copy aria-hidden="true" />}>
              Copiar
            </Button>
            <Button size="sm" variant="ghost" onClick={hide}>
              Ocultar
            </Button>
          </div>
        </Alert>
        <ConfirmDialog
          open={confirmHide}
          onOpenChange={setConfirmHide}
          title="Ocultar o token sem copiar?"
          description="O token é exibido uma única vez. Se ele não foi copiado, será preciso gerar outro token e revogar este dispositivo."
          confirmLabel="Ocultar mesmo assim"
          cancelLabel="Voltar ao token"
          destructive
          onConfirm={hideNow}
          // Back to the token: the field gets focus and selects it, ready for Ctrl+C. Hidden: the name field.
          returnFocus={{ afterCancel: tokenRef, afterConfirm: nameRef }}
        />
      </>
    );
  }

  return (
    <form className="flex flex-col gap-2 sm:flex-row sm:items-end" onSubmit={generate} noValidate>
      <div className="min-w-0 flex-1 space-y-1.5">
        <Label htmlFor="device-name">Nome do dispositivo</Label>
        <Input
          ref={nameRef}
          id="device-name"
          value={name}
          onChange={(event) => setName(event.target.value)}
          placeholder="Ex.: PC da sala"
          maxLength={60}
          autoComplete="off"
        />
      </div>
      <Button type="submit" loading={create.isPending}>
        Gerar token
      </Button>
    </form>
  );
}

function DeviceStateBadge({ device }: { device: CompanionDevice }) {
  if (device.revokedAt) return <Badge tone="danger">Revogado</Badge>;
  if (device.online) {
    return (
      <Badge tone="success" pulse>
        Online
      </Badge>
    );
  }
  return <Badge tone="neutral">Offline</Badge>;
}

function DeviceRow({ device, onRevoke }: { device: CompanionDevice; onRevoke: (device: CompanionDevice) => void }) {
  const revoked = !!device.revokedAt;
  return (
    <li className={cn("flex items-start justify-between gap-3 py-3 text-sm first:pt-0 last:pb-0", revoked && "opacity-70")}>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <p className="min-w-0 font-medium [overflow-wrap:anywhere]">{device.name}</p>
          <DeviceStateBadge device={device} />
        </div>
        <p className="mt-0.5 text-xs text-fg-subtle">
          {device.tokenPrefix ? <span className="font-mono">cfd_{device.tokenPrefix}…</span> : null}
          {device.tokenPrefix ? " · " : null}
          {revoked ? (
            <>revogado em {formatDateTime(device.revokedAt)}</>
          ) : device.lastSeenAt ? (
            <>
              última atividade <RelativeTime value={device.lastSeenAt} />
            </>
          ) : (
            "nunca conectou"
          )}
          {" · "}criado em {formatDateTime(device.createdAt)}
        </p>
      </div>
      {revoked ? null : (
        <Button variant="ghost" size="icon-sm" aria-label={`Revogar ${device.name}`} onClick={() => onRevoke(device)}>
          <Trash2 aria-hidden="true" />
        </Button>
      )}
    </li>
  );
}

/** "Dispositivos": token creation and the device list, shown once the live API answered. */
function DevicesCard({ poll }: { poll: boolean }) {
  const devices = useCompanionDevices({ poll });
  const revoke = useRevokeCompanionDevice();
  const [pending, setPending] = useState<CompanionDevice | null>(null);
  // Focus target after a revoke: the device's "Revogar" button goes away with the next list refresh.
  const listRef = useRef<HTMLUListElement | null>(null);

  const confirmRevoke = () => {
    if (!pending) return;
    revoke.mutate(pending.id, {
      onSuccess: () => {
        toast.success("Dispositivo revogado");
        setPending(null);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const items = devices.data ?? [];
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <MonitorPlay className="size-4 text-accent" aria-hidden="true" />
          Dispositivos
        </CardTitle>
        <CardDescription>
          Cada computador com o companion usa o próprio token, exibido uma única vez. Estado e última atividade vêm das requisições do
          companion; nada aqui é simulado.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <TokenIssuer />
        <div className="border-t border-line pt-4">
          {devices.isPending ? (
            <div role="status" aria-busy="true">
              <span className="sr-only">Carregando dispositivos…</span>
              <Skeleton className="h-12 w-full" />
            </div>
          ) : devices.isError && !devices.data ? (
            <div className="space-y-2">
              <p className="text-sm text-fg-muted">Lista de dispositivos indisponível: {describeError(devices.error)}</p>
              <Button size="sm" variant="secondary" onClick={() => devices.refetch()} loading={devices.isFetching}>
                Tentar novamente
              </Button>
            </div>
          ) : items.length === 0 ? (
            <EmptyState compact icon={<Radio />} title="Nenhum dispositivo conectado" description="Gere um token e configure o companion para aparecer aqui." />
          ) : (
            <>
              {devices.isError ? <p className="mb-2 text-xs text-warning">Não foi possível atualizar a lista agora; ela mostra a última consulta.</p> : null}
              <ul
                ref={listRef}
                tabIndex={-1}
                className="divide-y divide-line rounded-sm focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
                aria-label="Dispositivos"
              >
                {items.map((device) => (
                  <DeviceRow key={device.id} device={device} onRevoke={setPending} />
                ))}
              </ul>
            </>
          )}
        </div>
      </CardContent>
      <ConfirmDialog
        open={!!pending}
        onOpenChange={(open) => !open && setPending(null)}
        title={pending ? `Revogar “${pending.name}”?` : "Revogar dispositivo?"}
        description="O companion deste computador deixa de enviar segmentos na hora e as sessões abertas dele são encerradas com erro. Para voltar a usar o computador, gere um novo token."
        confirmLabel="Revogar"
        destructive
        loading={revoke.isPending}
        onConfirm={confirmRevoke}
        // "Cancelar" returns to the device's "Revogar" button; after a revoke (or if it is gone) the list.
        returnFocus={{ afterConfirm: listRef, fallback: listRef }}
      />
    </Card>
  );
}

/** Device · start · last segment (active) or end (final), only with what the API reported. */
function SessionMeta({ session, active }: { session: LiveSession; active: boolean }) {
  const parts: ReactNode[] = [];
  if (session.deviceName) parts.push(session.deviceName);
  if (session.startedAt) parts.push(`início ${formatDateTime(session.startedAt)}`);
  if (active) {
    parts.push(
      session.lastSegmentAt ? (
        <>
          último segmento <RelativeTime value={session.lastSegmentAt} />
        </>
      ) : (
        "nenhum segmento recebido ainda"
      ),
    );
  } else if (session.endedAt) {
    parts.push(`fim ${formatDateTime(session.endedAt)}`);
  }
  return (
    <p className="mt-0.5 text-xs text-fg-subtle [overflow-wrap:anywhere]">
      {parts.map((part, index) => (
        <Fragment key={index}>
          {index > 0 ? " · " : null}
          {part}
        </Fragment>
      ))}
    </p>
  );
}

function SessionRow({ session }: { session: LiveSession }) {
  const active = isActiveLiveSessionStatus(session.status);
  const title = liveSessionTitle(session);
  return (
    <li className="py-4 first:pt-0 last:pb-0">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <Link
            href={`/live/${encodeURIComponent(session.id)}`}
            className="group inline-flex max-w-full items-start gap-1 rounded-sm text-sm font-medium hover:text-accent focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
          >
            <span className="min-w-0 [overflow-wrap:anywhere]">{title}</span>
            <ChevronRight className="mt-0.5 size-4 shrink-0 text-fg-subtle group-hover:text-accent" aria-hidden="true" />
          </Link>
          <SessionMeta session={session} active={active} />
        </div>
        <LiveStatusBadge status={session.status} className="shrink-0" />
      </div>
      {active ? <CompanionStateChips session={session} className="mt-3" /> : null}
      {!active && session.endReason ? (
        <p className={cn("mt-2 text-xs [overflow-wrap:anywhere]", session.status === "error" ? "text-danger" : "text-fg-muted")}>
          Motivo: {session.endReason}
        </p>
      ) : null}
      <LiveSessionCounters session={session} className="mt-3" />
    </li>
  );
}

export function LivePage() {
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const [refreshing, setRefreshing] = useState(false);
  const sessions = useLiveSessions(page > 1 ? { page } : {});
  const data = sessions.data;
  // Data from an earlier answer stays on screen when a later refresh fails.
  const unavailable = sessions.isError && !data && isLiveUnavailableError(sessions.error);
  const failed = sessions.isError && !data && !unavailable;
  const items = data?.items ?? [];
  const polling = liveSessionsNeedPolling(items);

  const refresh = async () => {
    setRefreshing(true);
    try {
      await queryClient.invalidateQueries({ queryKey: queryKeys.live.all });
    } finally {
      setRefreshing(false);
    }
  };

  return (
    <>
      <PageHeader
        title="Lives"
        description="Detecção de destaques durante a transmissão, a partir da sua própria gravação no OBS."
        eyebrow="Captura ao vivo"
      />

      {unavailable ? (
        <Alert variant="info" className="mb-6" title="Captura ao vivo ainda não disponível neste ambiente">
          O servidor ainda não oferece sessões ao vivo. Enquanto isso, envie a gravação da live em “Novo projeto”.
        </Alert>
      ) : failed ? (
        <Alert
          variant="danger"
          className="mb-6"
          title="Não foi possível consultar as sessões"
          action={
            <Button size="sm" variant="secondary" onClick={() => sessions.refetch()} loading={sessions.isFetching}>
              Tentar novamente
            </Button>
          }
        >
          {describeError(sessions.error)}
        </Alert>
      ) : null}

      {/* DOM order = phone order (sessions, devices, setup); large screens place the devices column on the right. */}
      <div className="grid gap-6 lg:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)] lg:items-start">
        <div className="min-w-0 lg:col-start-1 lg:row-start-1">
          <Card>
            <CardHeader>
              <div className="flex items-start justify-between gap-3">
                <div className="min-w-0 space-y-1.5">
                  <CardTitle className="flex items-center gap-2">
                    <Radio className="size-4 text-accent" aria-hidden="true" />
                    Sessões
                  </CardTitle>
                  <CardDescription>Somente estados reais, informados pelo companion.</CardDescription>
                </div>
                {data ? (
                  <Button size="sm" variant="ghost" onClick={refresh} loading={refreshing} leftIcon={<RefreshCw aria-hidden="true" />}>
                    Atualizar
                  </Button>
                ) : null}
              </div>
            </CardHeader>
            <CardContent>
              {sessions.isPending ? (
                <div role="status" aria-busy="true">
                  <span className="sr-only">Carregando sessões…</span>
                  <Skeleton className="h-16 w-full" />
                </div>
              ) : unavailable ? (
                <p className="text-sm text-fg-muted">A captura ao vivo não está habilitada neste servidor, então não há sessões para mostrar.</p>
              ) : failed ? (
                <p className="text-sm text-fg-muted">As sessões não puderam ser consultadas agora (veja o aviso acima). Nada é exibido até o servidor responder.</p>
              ) : (
                <>
                  {sessions.isError ? (
                    <Alert variant="warning" className="mb-4" title="Não foi possível atualizar as sessões">
                      {describeError(sessions.error)} A lista abaixo mostra a última consulta.
                    </Alert>
                  ) : null}
                  {items.length === 0 ? (
                    <EmptyState
                      compact
                      icon={<Radio />}
                      title="Nenhuma sessão ao vivo"
                      description="Com o companion rodando, a sessão aparece aqui quando o OBS começa a gravar."
                    />
                  ) : (
                    <ul className="divide-y divide-line" aria-label="Sessões ao vivo">
                      {items.map((session) => (
                        <SessionRow key={session.id} session={session} />
                      ))}
                    </ul>
                  )}
                  <p className="mt-4 flex items-start gap-2 text-xs text-fg-subtle">
                    {polling ? (
                      <>
                        <span className="mt-1 size-1.5 shrink-0 rounded-full bg-success animate-pulse-soft" aria-hidden="true" />
                        Atualizando a cada 5 s enquanto houver sessão ativa.
                      </>
                    ) : (
                      "Sem sessão ativa, a lista não se atualiza sozinha: depois de iniciar a gravação no OBS, use “Atualizar”."
                    )}
                  </p>
                  {data ? (
                    <Pagination
                      className="mt-4"
                      page={data.page}
                      pageSize={data.pageSize}
                      total={data.total}
                      onPageChange={setPage}
                      itemLabel="sessões"
                    />
                  ) : null}
                </>
              )}
            </CardContent>
          </Card>
        </div>

        <div className="min-w-0 space-y-6 lg:col-start-2 lg:row-span-2 lg:row-start-1">
          {data ? (
            <DevicesCard poll={polling} />
          ) : (
            <Card>
              <CardHeader>
                <CardTitle className="flex items-center gap-2">
                  <MonitorPlay className="size-4 text-accent" aria-hidden="true" />
                  Dispositivos
                </CardTitle>
              </CardHeader>
              <CardContent>
                {sessions.isPending ? (
                  <div role="status" aria-busy="true">
                    <span className="sr-only">Consultando o servidor…</span>
                    <Skeleton className="h-24 w-full" />
                  </div>
                ) : unavailable ? (
                  <p className="text-sm text-fg-muted">A geração de tokens fica disponível quando a captura ao vivo for habilitada no servidor.</p>
                ) : (
                  <div className="space-y-3">
                    <p className="text-sm text-fg-muted">A consulta ao servidor falhou, então tokens e dispositivos não podem ser exibidos agora.</p>
                    <Button size="sm" variant="secondary" onClick={() => sessions.refetch()} loading={sessions.isFetching}>
                      Tentar novamente
                    </Button>
                  </div>
                )}
              </CardContent>
            </Card>
          )}
          <Alert variant="info" title="Limites honestos">
            Botões “conectar minha live” da Twitch ou do YouTube não são oferecidos enquanto não houver um caminho de ingestão autorizado.
            O chat não é lido sem o seu consentimento via OAuth.
          </Alert>
        </div>

        <div className="min-w-0 lg:col-start-1 lg:row-start-2">
          <CompanionSetupGuide />
        </div>
      </div>
    </>
  );
}
