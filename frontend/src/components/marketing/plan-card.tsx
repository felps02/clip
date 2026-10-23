import { Check } from "lucide-react";
import type { ReactNode } from "react";
import { Badge } from "@/components/ui/badge";
import type { Plan } from "@/lib/api/types";
import { formatBytes, formatCurrency } from "@/lib/format";
import { cn } from "@/lib/utils";

/** A plan with price 0 is free (never sent to checkout); the code is a fallback for older payloads. */
export function isFreePlan(plan: Pick<Plan, "code" | "priceMonthlyCents">): boolean {
  return plan.priceMonthlyCents === 0 || (plan.code === "free" && !plan.priceMonthlyCents);
}

/** Price 0 → "Grátis", no price → "Sob consulta", otherwise the monthly amount. */
export function planPriceLabel(plan: Pick<Plan, "code" | "priceMonthlyCents" | "currency">): string {
  if (plan.priceMonthlyCents === 0) return "Grátis";
  if (plan.priceMonthlyCents === null || plan.priceMonthlyCents === undefined) return "Sob consulta";
  return formatCurrency(plan.priceMonthlyCents, plan.currency ?? "BRL");
}

/** "Grátis", "Sob consulta" or "R$ 49,90/mês". */
export function planPriceText(plan: Pick<Plan, "code" | "priceMonthlyCents" | "currency">): string {
  const label = planPriceLabel(plan);
  return plan.priceMonthlyCents ? `${label}/mês` : label;
}

function limitValue(value: number, unit: string, unlimitedLabel = "ilimitado"): string {
  if (value === 0) return unlimitedLabel;
  return `${value.toLocaleString("pt-BR")} ${unit}`;
}

export function planLimitRows(plan: Plan): Array<{ label: string; value: string }> {
  const l = plan.limits;
  return [
    { label: "Processamento", value: `${l.monthlyProcessingMinutes.toLocaleString("pt-BR")} min/mês` },
    { label: "Duração máx. por vídeo", value: `${l.maxSourceDurationMinutes} min` },
    { label: "Tamanho máx. do arquivo", value: formatBytes(l.maxFileSizeMb * 1024 * 1024) },
    { label: "Projetos ativos", value: limitValue(l.maxActiveProjects, "projetos", "ilimitados") },
    { label: "Renderizações", value: l.maxRendersPerMonth === 0 ? "ilimitadas" : `${l.maxRendersPerMonth}/mês` },
    { label: "Resolução", value: `${l.maxRenderHeight}p` },
    { label: "Armazenamento", value: `${l.storageQuotaGb} GB` },
    { label: "Retenção", value: `${l.retentionDays} dias` },
    { label: "Layout webcam + gameplay", value: l.allowWebcamLayout ? "sim" : "não" },
    ...(typeof l.assistantMessagesPerMonth === "number"
      ? [{ label: "Mensagens do assistente", value: l.assistantMessagesPerMonth === 0 ? "ilimitadas" : `${l.assistantMessagesPerMonth.toLocaleString("pt-BR")}/mês` }]
      : []),
  ];
}

export interface PlanCardProps {
  plan: Plan;
  highlighted?: boolean;
  current?: boolean;
  showPrice?: boolean;
  priceSlot?: ReactNode;
  action?: ReactNode;
  className?: string;
  /** Static reference values (the API could not be reached): labelled as such on the card. */
  reference?: boolean;
}

export function PlanCard({ plan, highlighted, current, showPrice = true, priceSlot, action, className, reference }: PlanCardProps) {
  return (
    <article
      className={cn(
        "relative flex flex-col rounded-lg border bg-surface p-6 shadow-card",
        highlighted ? "border-accent/50 shadow-glow" : "border-line",
        className,
      )}
      aria-label={`Plano ${plan.name}`}
    >
      {highlighted ? (
        // An editorial recommendation, never a popularity claim: no API field backs "most chosen".
        <Badge tone="accent" className="absolute -top-3 left-6">
          Recomendado
        </Badge>
      ) : null}
      {current ? (
        <Badge tone="success" className="absolute -top-3 right-6">
          Plano atual
        </Badge>
      ) : null}
      <h3 className="text-lg font-semibold">{plan.name}</h3>
      {reference ? <p className="mt-1 text-xs font-medium uppercase tracking-wide text-warning">Valores de referência</p> : null}
      {plan.description ? <p className="mt-1 text-sm text-fg-muted">{plan.description}</p> : null}
      <div className="mt-5 min-h-12">
        {priceSlot ?? (
          showPrice ? (
            <p className="text-3xl font-semibold tracking-tight">
              {planPriceLabel(plan)}
              {plan.priceMonthlyCents ? <span className="text-sm font-normal text-fg-muted"> /mês</span> : null}
            </p>
          ) : null
        )}
      </div>
      {action ? <div className="mt-4">{action}</div> : null}
      <dl className="mt-6 space-y-2 border-t border-line pt-4 text-sm">
        {planLimitRows(plan).map((row) => (
          <div key={row.label} className="flex items-center justify-between gap-3">
            <dt className="text-fg-muted">{row.label}</dt>
            <dd className="text-right font-medium text-fg">{row.value}</dd>
          </div>
        ))}
      </dl>
      {plan.features && plan.features.length > 0 ? (
        <ul className="mt-5 space-y-1.5 border-t border-line pt-4 text-sm">
          {plan.features.map((feature) => (
            <li key={feature} className="flex items-start gap-2 text-fg-muted">
              <Check className="mt-0.5 size-4 shrink-0 text-accent" aria-hidden="true" />
              <span>{feature}</span>
            </li>
          ))}
        </ul>
      ) : null}
    </article>
  );
}
