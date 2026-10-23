"use client";

import Link from "next/link";
import { isFreePlan, PlanCard } from "@/components/marketing/plan-card";
import { FALLBACK_PLANS } from "@/components/marketing/plans-fallback";
import { Alert } from "@/components/ui/alert";
import { Button, buttonVariants } from "@/components/ui/button";
import { Skeleton } from "@/components/ui/skeleton";
import { usePlans } from "@/lib/api/hooks/use-billing";
import type { Plan } from "@/lib/api/types";
import { cn } from "@/lib/utils";

function PlanAction({ plan }: { plan: Plan }) {
  const highlighted = plan.code === "creator";
  const className = cn(buttonVariants({ variant: highlighted ? "primary" : "secondary" }), "w-full");
  // Registration always creates a free account; a paid plan is subscribed afterwards in Assinatura.
  return isFreePlan(plan) ? (
    <Link href="/register" className={className}>
      Criar conta grátis
    </Link>
  ) : (
    <Link href="/register" className={className} aria-label={`Criar conta e assinar o plano ${plan.name}`}>
      Criar conta e assinar
    </Link>
  );
}

/**
 * Plans from `GET /api/plans`, or the static reference values (`reference`) when the API could
 * not answer. Reference cards only show the free plan's price ("Grátis"): paid prices are
 * configuration of each environment.
 */
export function PlansGrid({ plans, reference = false }: { plans: Plan[]; reference?: boolean }) {
  return (
    <div className="grid gap-6 md:grid-cols-3">
      {plans.map((plan) => (
        <PlanCard
          key={plan.code}
          plan={plan}
          highlighted={plan.code === "creator"}
          reference={reference}
          priceSlot={reference && !isFreePlan(plan) ? <p className="text-sm text-fg-muted">Preço indisponível no momento</p> : undefined}
          action={<PlanAction plan={plan} />}
        />
      ))}
    </div>
  );
}

/** Loading state (also the prerendered shell): no numbers until the API answers. */
export function PlansSkeletonGrid() {
  return (
    <div className="grid gap-6 md:grid-cols-3" role="status" aria-busy="true">
      <span className="sr-only">Carregando os planos…</span>
      {Array.from({ length: 3 }).map((_, index) => (
        <div key={index} className="rounded-lg border border-line bg-surface p-6 shadow-card" aria-hidden="true">
          <Skeleton className="h-6 w-28" />
          <Skeleton className="mt-2 h-4 w-48" />
          <Skeleton className="mt-6 h-9 w-32" />
          <Skeleton className="mt-4 h-10 w-full" />
          <div className="mt-6 space-y-3 border-t border-line pt-4">
            {Array.from({ length: 6 }).map((__, row) => (
              <Skeleton key={row} className="h-4 w-full" />
            ))}
          </div>
        </div>
      ))}
    </div>
  );
}

const ACCOUNT_NOTE = "Toda conta começa no plano gratuito; um plano pago é assinado em Assinatura, depois de entrar.";

export function PlansSection() {
  const plans = usePlans();

  if (plans.isPending) return <PlansSkeletonGrid />;

  if (plans.isSuccess && plans.data.length > 0) {
    return (
      <div className="space-y-6">
        <PlansGrid plans={plans.data} />
        <p className="text-center text-xs text-fg-subtle">
          {ACCOUNT_NOTE} Os limites de cada plano são configuráveis e podem mudar. Nenhum resultado de alcance é garantido.
        </p>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Alert
        variant="warning"
        title="Planos indisponíveis no momento: valores de referência"
        action={
          <Button size="sm" variant="secondary" onClick={() => plans.refetch()} loading={plans.isFetching}>
            Tentar novamente
          </Button>
        }
      >
        {plans.isError ? "Não foi possível carregar os planos do servidor." : "O servidor não informou nenhum plano."} Os limites abaixo são
        os valores de referência da configuração padrão e podem não ser os desta instalação. Os preços dos planos pagos não são exibidos.
      </Alert>
      <PlansGrid plans={FALLBACK_PLANS} reference />
      <p className="text-center text-xs text-fg-subtle">{ACCOUNT_NOTE}</p>
    </div>
  );
}
