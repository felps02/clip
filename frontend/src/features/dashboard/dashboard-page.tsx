"use client";

import { Activity, Clapperboard, Clock3, Film, Plus } from "lucide-react";
import Link from "next/link";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { EmptyState } from "@/components/ui/empty-state";
import { Progress, UsageRow } from "@/components/ui/progress";
import { ClipCardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { StatCard } from "@/components/ui/stat-card";
import { ClipCard } from "@/components/video/clip-card";
import { JobStatusBadge } from "@/components/video/job-status";
import { AssistantUsageRow } from "@/features/billing/assistant-usage-row";
import { describeError } from "@/lib/api/client";
import { useUsage } from "@/lib/api/hooks/use-account";
import { useDashboardSummary } from "@/lib/api/hooks/use-dashboard";
import type { DashboardSummary } from "@/lib/api/types";
import { formatBytes, formatMinutes, formatNumber } from "@/lib/format";
import { JOB_KIND_LABELS } from "@/lib/labels";


function ActiveJobs({ summary }: { summary: DashboardSummary }) {
  if (summary.activeJobs.length === 0) {
    return <p className="text-sm text-fg-muted">Nenhum trabalho em andamento no momento.</p>;
  }
  return (
    <ul className="space-y-3">
      {summary.activeJobs.map((job) => (
        <li key={job.id} className="rounded-md border border-line bg-canvas/40 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div className="min-w-0">
              {job.projectId ? (
                <Link href={`/projects/${job.projectId}`} className="truncate text-sm font-medium hover:text-accent">
                  {job.projectTitle || "Projeto"}
                </Link>
              ) : (
                <span className="truncate text-sm font-medium">{job.projectTitle || "Projeto"}</span>
              )}
              <p className="text-xs text-fg-subtle">{JOB_KIND_LABELS[job.kind] ?? job.kind}</p>
            </div>
            <JobStatusBadge status={job.status} />
          </div>
          <div className="mt-2 flex items-center gap-3">
            <Progress value={job.progress ?? 0} size="sm" indeterminate={(job.progress ?? 0) <= 0} label={`Progresso de ${job.projectTitle ?? "projeto"}`} />
            <span className="font-mono text-xs text-fg-muted tabular-nums">{Math.round(job.progress ?? 0)}%</span>
          </div>
          {job.statusMessage ? <p className="mt-1.5 truncate text-xs text-fg-muted">{job.statusMessage}</p> : null}
        </li>
      ))}
    </ul>
  );
}

export function DashboardPage() {
  const summary = useDashboardSummary();
  const data = summary.data;
  // The dashboard summary has no assistant counters: they come from the account usage.
  const usage = useUsage();

  return (
    <>
      <PageHeader
        title="Dashboard"
        description="Acompanhe os processamentos e os últimos clipes."
        actions={
          <Link href="/new" className={buttonVariants()}>
            <Plus aria-hidden="true" />
            Novo projeto
          </Link>
        }
      />

      {summary.isError ? (
        <Alert
          variant="danger"
          className="mb-6"
          title="Não foi possível carregar o resumo"
          action={
            <Button size="sm" variant="secondary" onClick={() => summary.refetch()} loading={summary.isFetching}>
              Tentar novamente
            </Button>
          }
        >
          {describeError(summary.error)}
        </Alert>
      ) : null}

      {data?.warnings?.length ? (
        <div className="mb-6 space-y-2">
          {data.warnings.map((warning) => (
            <Alert
              key={warning.code}
              variant="warning"
              action={
                <Link href="/billing" className={buttonVariants({ variant: "secondary", size: "sm" })}>
                  Ver planos
                </Link>
              }
            >
              {warning.message}
            </Alert>
          ))}
        </div>
      ) : null}

      <div className="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-4">
        <StatCard label="Clipes gerados" value={data ? formatNumber(data.clipsTotal) : "—"} loading={summary.isPending} icon={<Clapperboard />} accent />
        <StatCard
          label="Trabalhos em andamento"
          value={data ? formatNumber(data.activeJobs.length) : "—"}
          loading={summary.isPending}
          icon={<Activity />}
        />
        <StatCard
          label="Minutos processados no período"
          value={data ? formatNumber(Math.round(data.minutesProcessedThisPeriod)) : "—"}
          loading={summary.isPending}
          icon={<Clock3 />}
        />
        <StatCard label="Clipes no período" value={data ? formatNumber(data.clipsThisPeriod) : "—"} loading={summary.isPending} icon={<Film />} />
      </div>

      <div className="mt-6 grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">
        <Card>
          <CardHeader>
            <CardTitle>Em andamento</CardTitle>
            <CardDescription>Atualizado automaticamente enquanto houver trabalhos ativos.</CardDescription>
          </CardHeader>
          <CardContent>
            {summary.isPending ? (
              <div className="space-y-3">
                <Skeleton className="h-16 w-full" />
                <Skeleton className="h-16 w-full" />
              </div>
            ) : data ? (
              <ActiveJobs summary={data} />
            ) : null}
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Uso do período</CardTitle>
            <CardDescription>Limites do seu plano atual.</CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            {summary.isPending ? (
              <div className="space-y-4">
                <Skeleton className="h-8 w-full" />
                <Skeleton className="h-8 w-full" />
                <Skeleton className="h-8 w-full" />
              </div>
            ) : data ? (
              <>
                <UsageRow
                  label="Minutos de processamento"
                  used={data.usage.processingSecondsUsed}
                  limit={data.usage.processingSecondsLimit}
                  format={formatMinutes}
                />
                <UsageRow label="Renderizações" used={data.usage.rendersUsed} limit={data.usage.rendersLimit} format={(v) => formatNumber(v)} />
                <UsageRow label="Armazenamento" used={data.usage.storageBytesUsed} limit={data.usage.storageBytesLimit} format={formatBytes} />
                {usage.data ? <AssistantUsageRow usage={usage.data} /> : null}
                <Link href="/billing" className="inline-block text-sm text-accent hover:underline">
                  Ver detalhes da assinatura
                </Link>
              </>
            ) : null}
          </CardContent>
        </Card>
      </div>

      <section className="mt-8" aria-labelledby="ultimos-clipes">
        <div className="mb-4 flex items-center justify-between">
          <h2 id="ultimos-clipes" className="text-lg font-semibold">
            Últimos clipes
          </h2>
          <Link href="/library" className="text-sm text-fg-muted hover:text-fg">
            Ver biblioteca
          </Link>
        </div>
        {summary.isPending ? (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            <ClipCardSkeleton />
            <ClipCardSkeleton />
            <ClipCardSkeleton />
          </div>
        ) : data && data.recentClips.length > 0 ? (
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
            {data.recentClips.map((clip) => (
              <ClipCard key={clip.id} clip={clip} showProject />
            ))}
          </div>
        ) : data ? (
          <EmptyState
            icon={<Clapperboard />}
            title="Nenhum clipe ainda"
            description="Envie a gravação de uma live para a análise encontrar os melhores momentos."
            action={
              <Link href="/new" className={buttonVariants()}>
                <Plus aria-hidden="true" />
                Criar primeiro projeto
              </Link>
            }
          />
        ) : null}
      </section>
    </>
  );
}
