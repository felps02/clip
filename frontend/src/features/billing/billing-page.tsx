"use client";

import { CreditCard, ExternalLink, Loader2 } from "lucide-react";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useEffect, useMemo, useState } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { isFreePlan, PlanCard, planPriceText } from "@/components/marketing/plan-card";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { Pagination } from "@/components/ui/pagination";
import { UsageRow } from "@/components/ui/progress";
import { CardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { AssistantUsageRow } from "@/features/billing/assistant-usage-row";
import { describeError } from "@/lib/api/client";
import { useUsage } from "@/lib/api/hooks/use-account";
import { useCancelSubscription, useCheckout, usePayments, usePlans, usePortalSession, useSubscription } from "@/lib/api/hooks/use-billing";
import type { Plan, UsageResponse } from "@/lib/api/types";
import { formatBytes, formatCurrency, formatDate, formatDateTime, formatMinutes, formatNumber } from "@/lib/format";
import { useSessionStorageValue, writeSessionStorage } from "@/lib/hooks/use-session-storage";
import { PAYMENT_STATUS_LABELS, SUBSCRIPTION_STATUS_LABELS, SUBSCRIPTION_STATUS_TONES } from "@/lib/labels";
import type { PaymentEventStatus } from "@/lib/api/types";

const CHECKOUT_KEY = "cf.checkout";
const CHECKOUT_POLL_MS = 4000;
/** How long the page polls for the provider's confirmation before asking the user to come back later. */
export const CHECKOUT_WAIT_MS = 5 * 60_000;

interface PendingCheckout {
  planCode: string;
  previousPlanCode: string | null;
  /** Set once the subscription reported `planCode`: polling stops and the success message survives reloads. */
  confirmed?: boolean;
  /**
   * The API answered `planChanged: true`: the active subscription was switched in place by the
   * provider. There is no checkout page (and no `?checkout=success` return) to wait for.
   */
  inPlace?: boolean;
}

export function parsePending(raw: string | null): PendingCheckout | null {
  if (!raw) return null;
  try {
    const parsed = JSON.parse(raw) as Partial<PendingCheckout>;
    return typeof parsed.planCode === "string"
      ? {
          planCode: parsed.planCode,
          previousPlanCode: parsed.previousPlanCode ?? null,
          confirmed: parsed.confirmed === true,
          inPlace: parsed.inPlace === true,
        }
      : null;
  } catch {
    return null;
  }
}


function UsageSection({ usage }: { usage: UsageResponse }) {
  const limits = usage.plan.limits;
  return (
    <>
      <UsageRow label="Minutos de processamento" used={usage.used.processingSeconds} limit={limits.monthlyProcessingMinutes * 60} format={formatMinutes} />
      <UsageRow label="Renderizações" used={usage.used.renders} limit={limits.maxRendersPerMonth} format={(value) => formatNumber(value)} />
      <UsageRow label="Armazenamento" used={usage.used.storageBytes} limit={limits.storageQuotaGb * 1024 ** 3} format={formatBytes} />
      <UsageRow label="Projetos ativos" used={usage.used.activeProjects} limit={limits.maxActiveProjects} format={(value) => formatNumber(value)} />
      <AssistantUsageRow usage={usage} />
      <p className="text-xs text-fg-subtle">
        Período atual: {formatDate(usage.period.start)} – {formatDate(usage.period.end)}
      </p>
    </>
  );
}

export function BillingPage() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const checkoutParam = searchParams.get("checkout");
  const rawPending = useSessionStorageValue(CHECKOUT_KEY);
  const pending = useMemo(() => parsePending(rawPending), [rawPending]);
  const [waitTimedOut, setWaitTimedOut] = useState(false);
  const inPlace = !!pending?.inPlace;
  // A plan change is tracked after the provider's checkout sent us back (?checkout=success) or,
  // when the subscription was switched in place, right away on this page.
  const tracking = !!pending && (inPlace || checkoutParam === "success");
  // The provider has not confirmed the new plan yet.
  const awaitingConfirmation = tracking && !pending?.confirmed;
  const confirmed = tracking && !!pending?.confirmed;
  // Poll only while unconfirmed and inside the bounded wait; the webhook may never arrive.
  const waiting = awaitingConfirmation && !waitTimedOut;

  const subscription = useSubscription({ refetchInterval: waiting ? CHECKOUT_POLL_MS : false });
  const usage = useUsage();
  const plans = usePlans();
  const [paymentsPage, setPaymentsPage] = useState(1);
  const payments = usePayments({ page: paymentsPage, pageSize: 10 });
  const checkout = useCheckout();
  const portal = usePortalSession();
  const cancel = useCancelSubscription();
  const [cancelOpen, setCancelOpen] = useState(false);
  // Paid plan the person asked to switch to, waiting for the confirmation dialog.
  const [changeTarget, setChangeTarget] = useState<Plan | null>(null);

  const current = subscription.data;
  const currentPlanCode = current?.plan?.code ?? current?.subscription.planCode ?? null;
  const currentPlanName = current?.plan?.name ?? current?.subscription.planName ?? currentPlanCode;
  const paymentsConfigured = current?.paymentsConfigured ?? false;

  // The subscription now reports the plan we checked out: persist the confirmation so polling
  // stops (also after a reload) and the success message keeps showing until dismissed.
  useEffect(() => {
    if (!awaitingConfirmation || !pending || !currentPlanCode || currentPlanCode !== pending.planCode) return;
    writeSessionStorage(CHECKOUT_KEY, JSON.stringify({ ...pending, confirmed: true } satisfies PendingCheckout));
  }, [awaitingConfirmation, pending, currentPlanCode]);

  useEffect(() => {
    if (!waiting) return;
    const timer = window.setTimeout(() => setWaitTimedOut(true), CHECKOUT_WAIT_MS);
    return () => window.clearTimeout(timer);
  }, [waiting]);

  const dismissCheckout = () => {
    writeSessionStorage(CHECKOUT_KEY, null);
    router.replace(pathname);
  };

  const startCheckout = (plan: Plan, onSettled?: () => void) => {
    const origin = window.location.origin;
    checkout.mutate(
      { planCode: plan.code, successUrl: `${origin}/billing?checkout=success`, cancelUrl: `${origin}/billing?checkout=cancel` },
      {
        onSuccess: ({ checkoutUrl, planChanged }) => {
          if (planChanged) {
            // Switched in place by the provider (prorated): nothing to pay on a checkout page, so no
            // round trip; this page waits for the webhook that changes the entitlement.
            setWaitTimedOut(false);
            writeSessionStorage(
              CHECKOUT_KEY,
              JSON.stringify({ planCode: plan.code, previousPlanCode: currentPlanCode, inPlace: true } satisfies PendingCheckout),
            );
            void subscription.refetch();
            return;
          }
          writeSessionStorage(CHECKOUT_KEY, JSON.stringify({ planCode: plan.code, previousPlanCode: currentPlanCode } satisfies PendingCheckout));
          window.location.assign(checkoutUrl);
        },
        onError: (error) => toast.error(describeError(error)),
        onSettled,
      },
    );
  };

  const openPortal = () => {
    portal.mutate(undefined, {
      onSuccess: ({ url }) => {
        window.location.assign(url);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const confirmCancel = () => {
    cancel.mutate(undefined, {
      onSuccess: () => {
        toast.success("A assinatura será cancelada ao fim do período atual.");
        setCancelOpen(false);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const sub = current?.subscription;
  const currentIsFree = current?.plan ? isFreePlan(current.plan) : currentPlanCode === "free";
  const isPaid = !!currentPlanCode && !currentIsFree;
  const canCancel = isPaid && !!sub && (sub.status === "active" || sub.status === "trialing" || sub.status === "past_due") && !sub.cancelAtPeriodEnd;
  // An in-place change was sent to the provider and is not confirmed yet: the cards still show the
  // old plan, and another click would send a second change. "Parar de verificar" lifts this.
  const planChangeInFlight = awaitingConfirmation && inPlace;

  const planAction = (plan: Plan) => {
    if (plan.code === currentPlanCode) {
      return (
        <Button variant="secondary" className="w-full" disabled>
          Plano atual
        </Button>
      );
    }
    if (isFreePlan(plan)) {
      if (canCancel) {
        return (
          <Button variant="secondary" className="w-full" onClick={() => setCancelOpen(true)} disabled={planChangeInFlight}>
            Voltar para o plano {plan.name}
          </Button>
        );
      }
      // Never a silently disabled button: say why going back is not offered right now.
      return (
        <p className="rounded-md border border-line px-3 py-2 text-center text-sm text-fg-muted">
          {sub?.cancelAtPeriodEnd
            ? sub.currentPeriodEnd
              ? `Cancelamento já agendado: a conta volta para este plano em ${formatDate(sub.currentPeriodEnd)}.`
              : "Cancelamento já agendado: a conta volta para este plano ao fim do período."
            : sub
              ? `Indisponível agora: a assinatura está ${(SUBSCRIPTION_STATUS_LABELS[sub.status] ?? sub.status).toLowerCase()}.`
              : "Indisponível enquanto a assinatura não for carregada."}
        </p>
      );
    }
    // No price, or a price without a provider price id (`purchasable: false`): never sent to checkout.
    if (plan.priceMonthlyCents === null || plan.priceMonthlyCents === undefined || plan.purchasable === false) {
      return (
        <Button variant="secondary" className="w-full" disabled>
          Sob consulta
        </Button>
      );
    }
    return (
      <Button
        className="w-full"
        disabled={!paymentsConfigured || planChangeInFlight}
        loading={checkout.isPending}
        // A paid subscription is switched in place right away (prorated): confirm first. Without
        // one, the provider's checkout page is where the person confirms and pays.
        onClick={() => (isPaid ? setChangeTarget(plan) : startCheckout(plan))}
      >
        {planChangeInFlight && plan.code === pending?.planCode ? "Troca solicitada" : isPaid ? "Mudar de plano" : "Assinar"}
      </Button>
    );
  };

  return (
    <>
      <PageHeader title="Assinatura" description="Seu plano, o uso do período e o histórico de pagamentos." />

      {confirmed ? (
        <Alert
          variant="success"
          className="mb-6"
          title={inPlace ? "Plano alterado" : "Assinatura atualizada"}
          action={
            <Button size="sm" variant="secondary" onClick={dismissCheckout}>
              Entendi
            </Button>
          }
        >
          Seu plano agora é {currentPlanName ?? pending?.planCode}.
        </Alert>
      ) : awaitingConfirmation && waitTimedOut ? (
        <Alert
          variant="warning"
          className="mb-6"
          title="Ainda aguardando o provedor de pagamento"
          action={
            <div className="flex flex-wrap gap-2">
              <Button size="sm" variant="secondary" onClick={() => subscription.refetch()} loading={subscription.isFetching}>
                Verificar agora
              </Button>
              <Button size="sm" variant="ghost" onClick={dismissCheckout}>
                Fechar
              </Button>
            </div>
          }
        >
          {inPlace
            ? `A troca de plano foi enviada ao provedor, mas a confirmação não chegou em ${CHECKOUT_WAIT_MS / 60_000} minutos. O plano muda automaticamente quando ela chegar; atualize a página mais tarde ou confira os eventos em “Pagamentos”.`
            : `Não recebemos a confirmação em ${CHECKOUT_WAIT_MS / 60_000} minutos. A assinatura é atualizada automaticamente quando o pagamento for confirmado; atualize a página mais tarde ou confira os eventos em “Pagamentos”.`}
        </Alert>
      ) : awaitingConfirmation && inPlace ? (
        <Alert
          variant="info"
          className="mb-6"
          title="Troca de plano solicitada"
          action={
            <Button size="sm" variant="secondary" onClick={dismissCheckout}>
              Parar de verificar
            </Button>
          }
        >
          <span className="inline-flex items-center gap-2">
            <Loader2 className="size-3.5 shrink-0 animate-spin" aria-hidden="true" />
            O provedor de pagamento está aplicando a troca na sua assinatura atual, com cobrança proporcional. O novo plano vale assim que a
            confirmação chegar; isso pode levar alguns instantes.
          </span>
        </Alert>
      ) : awaitingConfirmation ? (
        <Alert
          variant="info"
          className="mb-6"
          title="Voltamos do checkout"
          action={
            <Button size="sm" variant="secondary" onClick={dismissCheckout}>
              Parar de verificar
            </Button>
          }
        >
          <span className="inline-flex items-center gap-2">
            <Loader2 className="size-3.5 animate-spin" aria-hidden="true" />
            Aguardando a confirmação do provedor de pagamento. A assinatura é atualizada automaticamente quando o pagamento for confirmado; isso
            pode levar alguns instantes.
          </span>
        </Alert>
      ) : checkoutParam === "success" ? (
        <Alert
          variant="info"
          className="mb-6"
          action={
            <Button size="sm" variant="secondary" onClick={dismissCheckout}>
              Fechar
            </Button>
          }
        >
          Voltamos do checkout. Confira abaixo o status atual da assinatura; mudanças só valem após a confirmação do provedor.
        </Alert>
      ) : checkoutParam === "cancel" ? (
        <Alert
          variant="info"
          className="mb-6"
          action={
            <Button size="sm" variant="secondary" onClick={dismissCheckout}>
              Fechar
            </Button>
          }
        >
          Checkout cancelado. Nenhuma alteração foi feita na sua assinatura.
        </Alert>
      ) : null}

      {current && !paymentsConfigured ? (
        <Alert variant="warning" className="mb-6">
          Pagamentos ainda não configurados neste ambiente. Os planos pagos ficam disponíveis assim que um provedor for ativado.
        </Alert>
      ) : null}

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle className="flex items-center gap-2">
              <CreditCard className="size-4 text-accent" aria-hidden="true" />
              Plano atual
            </CardTitle>
          </CardHeader>
          <CardContent>
            {subscription.isPending ? (
              <div className="space-y-3">
                <Skeleton className="h-7 w-40" />
                <Skeleton className="h-4 w-64" />
              </div>
            ) : subscription.isError ? (
              <Alert variant="danger">{describeError(subscription.error)}</Alert>
            ) : current ? (
              <div className="space-y-4">
                <div className="flex flex-wrap items-center gap-3">
                  <p className="text-2xl font-semibold">{current.plan?.name ?? current.subscription.planName ?? currentPlanCode}</p>
                  <Badge tone={SUBSCRIPTION_STATUS_TONES[current.subscription.status] ?? "neutral"}>
                    {SUBSCRIPTION_STATUS_LABELS[current.subscription.status] ?? current.subscription.status}
                  </Badge>
                </div>
                <p className="text-sm text-fg-muted">
                  {current.plan ? planPriceText(current.plan) : "Sem cobrança"}
                  {current.subscription.currentPeriodEnd
                    ? ` · ${current.subscription.cancelAtPeriodEnd ? "termina" : "renova"} em ${formatDate(current.subscription.currentPeriodEnd)}`
                    : ""}
                </p>
                {current.subscription.cancelAtPeriodEnd ? (
                  <Alert variant="warning">O cancelamento está agendado. Você mantém o plano até o fim do período e depois volta para o plano gratuito.</Alert>
                ) : null}
                <div className="flex flex-wrap gap-2">
                  {current.portalAvailable ? (
                    <Button variant="secondary" onClick={openPortal} loading={portal.isPending} rightIcon={<ExternalLink aria-hidden="true" />}>
                      Gerenciar pagamento
                    </Button>
                  ) : null}
                  {canCancel ? (
                    <Button variant="danger" onClick={() => setCancelOpen(true)} disabled={planChangeInFlight}>
                      Cancelar assinatura
                    </Button>
                  ) : null}
                </div>
              </div>
            ) : null}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Uso do período</CardTitle>
            <CardDescription>Limites do plano atual.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            {usage.isPending ? (
              <div className="space-y-4">
                <Skeleton className="h-8 w-full" />
                <Skeleton className="h-8 w-full" />
                <Skeleton className="h-8 w-full" />
              </div>
            ) : usage.isError ? (
              <Alert
                variant="danger"
                action={
                  <Button size="sm" variant="secondary" onClick={() => usage.refetch()} loading={usage.isFetching}>
                    Tentar novamente
                  </Button>
                }
              >
                {describeError(usage.error)}
              </Alert>
            ) : usage.data ? (
              <UsageSection usage={usage.data} />
            ) : null}
          </CardContent>
        </Card>
      </div>

      <section className="mt-8" aria-labelledby="planos-comparacao">
        <h2 id="planos-comparacao" className="mb-4 text-lg font-semibold">
          Planos
        </h2>
        {plans.isPending ? (
          <div className="grid grid-cols-1 gap-6 md:grid-cols-3">
            <CardSkeleton />
            <CardSkeleton />
            <CardSkeleton />
          </div>
        ) : plans.isError ? (
          <Alert variant="danger">{describeError(plans.error)}</Alert>
        ) : (
          <div className="grid grid-cols-1 gap-6 md:grid-cols-3">
            {(plans.data ?? []).map((plan) => (
              <PlanCard
                key={plan.code}
                plan={plan}
                current={plan.code === currentPlanCode}
                highlighted={plan.code === "creator" && plan.code !== currentPlanCode}
                action={planAction(plan)}
              />
            ))}
          </div>
        )}
      </section>

      <div className="mt-8 grid grid-cols-1 gap-6 lg:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Histórico de uso</CardTitle>
          </CardHeader>
          <CardContent>
            {usage.isPending ? (
              <div className="space-y-2" aria-busy="true">
                <Skeleton className="h-6 w-full" />
                <Skeleton className="h-6 w-full" />
                <Skeleton className="h-6 w-2/3" />
              </div>
            ) : usage.isError ? (
              <Alert
                variant="danger"
                action={
                  <Button size="sm" variant="secondary" onClick={() => usage.refetch()} loading={usage.isFetching}>
                    Tentar novamente
                  </Button>
                }
              >
                {describeError(usage.error)}
              </Alert>
            ) : usage.data && usage.data.history.length > 0 ? (
              <div className="-mx-1 overflow-x-auto px-1" role="region" aria-label="Histórico de uso" tabIndex={0}>
                <table className="w-full min-w-[17rem] text-sm">
                  <thead className="text-left text-xs uppercase tracking-wide text-fg-subtle">
                    <tr>
                      <th className="py-2 font-medium">Período</th>
                      <th className="py-2 pl-3 text-right font-medium">Minutos</th>
                      <th className="py-2 pl-3 text-right font-medium">Renderizações</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-line">
                    {usage.data.history.map((entry) => (
                      <tr key={`${entry.periodStart}-${entry.periodEnd}`}>
                        <td className="py-2 text-fg-muted">
                          {formatDate(entry.periodStart)} – {formatDate(entry.periodEnd)}
                        </td>
                        <td className="py-2 text-right tabular-nums">{formatMinutes(entry.processingSeconds)}</td>
                        <td className="py-2 text-right tabular-nums">{formatNumber(entry.renders)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="text-sm text-fg-muted">Nenhum período anterior registrado.</p>
            )}
          </CardContent>
        </Card>

        <Card>
          <CardHeader>
            <CardTitle>Pagamentos</CardTitle>
            <CardDescription>Eventos recebidos do provedor de pagamentos.</CardDescription>
          </CardHeader>
          <CardContent>
            {payments.isPending ? (
              <Skeleton className="h-20 w-full" />
            ) : payments.isError ? (
              <p className="text-sm text-fg-muted">Histórico indisponível: {describeError(payments.error)}</p>
            ) : payments.data && payments.data.items.length > 0 ? (
              <>
                <div className="-mx-1 overflow-x-auto px-1" role="region" aria-label="Pagamentos" tabIndex={0}>
                  <table className="w-full min-w-[30rem] text-sm">
                    <thead className="text-left text-xs uppercase tracking-wide text-fg-subtle">
                      <tr>
                        <th className="py-2 font-medium">Data</th>
                        <th className="py-2 font-medium">Evento</th>
                        <th className="py-2 text-right font-medium">Valor</th>
                        <th className="py-2 text-right font-medium">Status</th>
                      </tr>
                    </thead>
                    <tbody className="divide-y divide-line">
                      {payments.data.items.map((event) => (
                        <tr key={event.id}>
                          <td className="whitespace-nowrap py-2 pr-3 text-fg-muted">{formatDateTime(event.createdAt)}</td>
                          <td className="py-2 font-mono text-xs">{event.type}</td>
                          <td className="py-2 text-right tabular-nums">
                            {typeof event.amountCents === "number" ? formatCurrency(event.amountCents, event.currency ?? "BRL") : "—"}
                          </td>
                          <td className="py-2 text-right">
                            <Badge tone={event.status === "failed" ? "danger" : event.status === "processed" ? "success" : "neutral"}>
                              {PAYMENT_STATUS_LABELS[event.status as PaymentEventStatus] ?? event.status}
                            </Badge>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
                <Pagination
                  className="mt-3"
                  page={payments.data.page || paymentsPage}
                  pageSize={payments.data.pageSize || 10}
                  total={payments.data.total}
                  onPageChange={setPaymentsPage}
                  itemLabel="eventos"
                />
              </>
            ) : (
              <p className="text-sm text-fg-muted">Nenhum pagamento registrado.</p>
            )}
          </CardContent>
        </Card>
      </div>

      <ConfirmDialog
        open={cancelOpen}
        onOpenChange={setCancelOpen}
        title="Cancelar assinatura?"
        description="Você mantém o plano atual até o fim do período já pago. Depois disso a conta volta para o plano gratuito e novos trabalhos ficam sujeitos aos limites dele."
        confirmLabel="Cancelar assinatura"
        cancelLabel="Manter plano"
        destructive
        loading={cancel.isPending}
        onConfirm={confirmCancel}
      />

      <ConfirmDialog
        open={!!changeTarget}
        onOpenChange={(open) => {
          if (!open) setChangeTarget(null);
        }}
        title={changeTarget ? `Mudar para o plano ${changeTarget.name}?` : "Mudar de plano?"}
        description={
          <>
            A troca vale agora, na sua assinatura atual: o provedor de pagamento cobra ou credita a diferença proporcional ao tempo que
            resta do período.
            {typeof changeTarget?.priceMonthlyCents === "number"
              ? ` Novo valor: ${formatCurrency(changeTarget.priceMonthlyCents, changeTarget.currency ?? "BRL")}/mês.`
              : ""}
          </>
        }
        confirmLabel="Mudar de plano"
        cancelLabel="Voltar"
        loading={checkout.isPending}
        onConfirm={() => {
          if (changeTarget) startCheckout(changeTarget, () => setChangeTarget(null));
        }}
      >
        {sub?.cancelAtPeriodEnd ? (
          <Alert variant="warning" title="O cancelamento agendado será desfeito">
            {sub.currentPeriodEnd
              ? `Com a troca, a assinatura deixa de terminar e volta a ser renovada em ${formatDate(sub.currentPeriodEnd)}, com a cobrança do novo plano.`
              : "Com a troca, a assinatura deixa de terminar e volta a ser renovada ao fim do período, com a cobrança do novo plano."}
          </Alert>
        ) : null}
      </ConfirmDialog>
    </>
  );
}
