"use client";

import { Ban, FolderOpen, MoreHorizontal, Plus, Trash2 } from "lucide-react";
import Image from "next/image";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { ConfirmDialog } from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { EmptyState } from "@/components/ui/empty-state";
import { Pagination } from "@/components/ui/pagination";
import { Progress } from "@/components/ui/progress";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Skeleton } from "@/components/ui/skeleton";
import { describeError } from "@/lib/api/client";
import { useCancelJob, useDeleteProject, useProjects } from "@/lib/api/hooks/use-projects";
import type { ProjectStatus, ProjectSummary } from "@/lib/api/types";
import { formatDate, formatDuration, formatNumber, pluralize } from "@/lib/format";
import {
  isCancellableJobStatus,
  jobStatusLabel,
  platformLabel,
  PROJECT_STATUS_LABELS,
  PROJECT_STATUS_TONES,
  SOURCE_KIND_LABELS,
} from "@/lib/labels";

const PAGE_SIZE = 20;
const STATUS_OPTIONS: Array<{ value: ProjectStatus | "all"; label: string }> = [
  { value: "all", label: "Todos os status" },
  ...(Object.keys(PROJECT_STATUS_LABELS) as ProjectStatus[]).map((status) => ({ value: status, label: PROJECT_STATUS_LABELS[status] })),
];

function JobCell({ project }: { project: ProjectSummary }) {
  const job = project.latestJob;
  if (!job) {
    return (
      <span className="text-xs text-fg-subtle">
        <span className="md:hidden">Último trabalho: </span>—
      </span>
    );
  }
  return (
    <div className="min-w-36 space-y-1">
      <p className="text-[10px] uppercase tracking-wide text-fg-subtle md:hidden">Último trabalho</p>
      <div className="flex items-center justify-between gap-2 text-xs">
        <span className="truncate text-fg-muted">{jobStatusLabel(job.status)}</span>
        <span className="font-mono text-fg-subtle tabular-nums">{Math.round(job.progress ?? 0)}%</span>
      </div>
      <Progress
        value={job.progress ?? 0}
        size="sm"
        tone={job.status === "failed" ? "danger" : job.status === "completed" ? "success" : "accent"}
        label={`Progresso de ${project.title}`}
      />
    </div>
  );
}

export function ProjectsPage() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const page = Math.max(1, Number(searchParams.get("page")) || 1);
  const statusParam = searchParams.get("status") ?? "";
  const status = (Object.keys(PROJECT_STATUS_LABELS) as string[]).includes(statusParam) ? (statusParam as ProjectStatus) : "";

  const projects = useProjects({ page, pageSize: PAGE_SIZE, status });
  const cancelJob = useCancelJob();
  const deleteProject = useDeleteProject();
  const [pendingDelete, setPendingDelete] = useState<ProjectSummary | null>(null);
  const [pendingCancel, setPendingCancel] = useState<ProjectSummary | null>(null);

  const setParams = (next: { page?: number; status?: string }) => {
    const params = new URLSearchParams(searchParams.toString());
    if (next.status !== undefined) {
      if (next.status) params.set("status", next.status);
      else params.delete("status");
      params.delete("page");
    }
    if (next.page !== undefined) {
      if (next.page > 1) params.set("page", String(next.page));
      else params.delete("page");
    }
    const qs = params.toString();
    router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
  };

  const confirmCancel = () => {
    const job = pendingCancel?.latestJob;
    if (!pendingCancel || !job) return;
    cancelJob.mutate(
      { projectId: pendingCancel.id, jobId: job.id },
      {
        onSuccess: () => {
          toast.success("Cancelamento solicitado");
          setPendingCancel(null);
        },
        onError: (error) => toast.error(describeError(error)),
      },
    );
  };

  const confirmDelete = () => {
    if (!pendingDelete) return;
    deleteProject.mutate(pendingDelete.id, {
      onSuccess: () => {
        toast.success("Projeto excluído");
        setPendingDelete(null);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const items = projects.data?.items ?? [];

  return (
    <>
      <PageHeader
        title="Projetos"
        description="Cada projeto é uma gravação analisada, com seus candidatos e clipes."
        actions={
          <Link href="/new" className={buttonVariants()}>
            <Plus aria-hidden="true" />
            Novo projeto
          </Link>
        }
      />

      <div className="mb-4 flex flex-wrap items-center gap-3">
        <div className="w-full sm:w-56">
          <Select value={status || "all"} onValueChange={(value) => setParams({ status: value === "all" ? "" : value })}>
            <SelectTrigger aria-label="Filtrar por status">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              {STATUS_OPTIONS.map((option) => (
                <SelectItem key={option.value} value={option.value}>
                  {option.label}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        </div>
        {projects.data ? (
          <p className="text-sm text-fg-muted">{pluralize(projects.data.total, "projeto", "projetos")}</p>
        ) : null}
      </div>

      {projects.isError ? (
        <Alert
          variant="danger"
          title="Não foi possível carregar os projetos"
          action={
            <Button size="sm" variant="secondary" onClick={() => projects.refetch()} loading={projects.isFetching}>
              Tentar novamente
            </Button>
          }
        >
          {describeError(projects.error)}
        </Alert>
      ) : projects.isPending ? (
        <div className="space-y-2" aria-busy="true">
          {Array.from({ length: 5 }).map((_, index) => (
            <Skeleton key={index} className="h-16 w-full" />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState
          icon={<FolderOpen />}
          title={status ? "Nenhum projeto com este status" : "Nenhum projeto ainda"}
          description={
            status
              ? "Tente outro filtro ou crie um novo projeto."
              : "Envie a gravação de uma live para começar. A análise roda em segundo plano."
          }
          action={
            <Link href="/new" className={buttonVariants()}>
              <Plus aria-hidden="true" />
              Novo projeto
            </Link>
          }
        />
      ) : (
        <div className="overflow-hidden rounded-lg border border-line bg-surface">
          {/* Explicit roles keep the table semantics below `md`, where rows become CSS grids and the
              header is hidden; every cell then carries its own visible label. */}
          <table role="table" className="w-full text-sm">
            <thead role="rowgroup" className="hidden bg-elevated/50 text-left text-xs uppercase tracking-wide text-fg-subtle md:table-header-group">
              <tr role="row">
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  Projeto
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  Fonte
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  Status
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  Último trabalho
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium text-right">
                  Clipes
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  Data
                </th>
                <th role="columnheader" scope="col" className="px-4 py-3 font-medium">
                  <span className="sr-only">Ações</span>
                </th>
              </tr>
            </thead>
            <tbody role="rowgroup" className="divide-y divide-line">
              {items.map((project) => {
                const cancellable = isCancellableJobStatus(project.latestJob?.status);
                return (
                  <tr key={project.id} role="row" className="grid grid-cols-[1fr_auto] gap-x-3 gap-y-2 p-4 md:table-row md:p-0">
                    <td role="cell" className="col-span-2 md:px-4 md:py-3">
                      <div className="flex items-center gap-3">
                        <div className="relative hidden h-10 w-16 shrink-0 overflow-hidden rounded-xs bg-elevated sm:block">
                          {project.thumbnailUrl ? (
                            <Image src={project.thumbnailUrl} alt="" fill unoptimized sizes="64px" className="object-cover" />
                          ) : null}
                        </div>
                        <div className="min-w-0">
                          <Link href={`/projects/${project.id}`} className="block truncate font-medium hover:text-accent">
                            {project.title}
                          </Link>
                          <p className="text-xs text-fg-subtle">
                            {project.durationSeconds ? formatDuration(project.durationSeconds) : "duração desconhecida"}
                            {" · "}
                            {pluralize(project.candidateCount, "candidato", "candidatos")}
                          </p>
                        </div>
                      </div>
                    </td>
                    <td role="cell" className="text-xs text-fg-muted md:px-4 md:py-3 md:text-sm">
                      <span className="text-fg-subtle md:hidden">Fonte: </span>
                      {SOURCE_KIND_LABELS[project.sourceKind] ?? project.sourceKind}
                      {project.sourceKind !== "upload" ? <span className="text-fg-subtle"> · {platformLabel(project.sourcePlatform)}</span> : null}
                    </td>
                    <td role="cell" className="md:px-4 md:py-3">
                      <span className="text-xs text-fg-subtle md:hidden">Status: </span>
                      <Badge tone={PROJECT_STATUS_TONES[project.status] ?? "neutral"} pulse={project.status === "processing"}>
                        {PROJECT_STATUS_LABELS[project.status] ?? project.status}
                      </Badge>
                    </td>
                    <td role="cell" className="col-span-2 md:px-4 md:py-3">
                      <JobCell project={project} />
                    </td>
                    <td role="cell" className="text-xs text-fg-muted md:px-4 md:py-3 md:text-right md:text-sm">
                      <span className="text-fg-subtle md:hidden">Clipes: </span>
                      {formatNumber(project.clipCount)}
                    </td>
                    <td role="cell" className="text-xs text-fg-subtle md:px-4 md:py-3 md:text-sm">
                      <span className="md:hidden">Criado em: </span>
                      {formatDate(project.createdAt)}
                    </td>
                    <td role="cell" className="col-span-2 flex justify-end md:table-cell md:px-2 md:py-3 md:text-right">
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button variant="ghost" size="icon-sm" aria-label={`Ações para ${project.title}`}>
                            <MoreHorizontal aria-hidden="true" />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          <DropdownMenuItem asChild>
                            <Link href={`/projects/${project.id}`}>
                              <FolderOpen aria-hidden="true" />
                              Abrir
                            </Link>
                          </DropdownMenuItem>
                          {cancellable ? (
                            <DropdownMenuItem onSelect={() => setPendingCancel(project)}>
                              <Ban aria-hidden="true" />
                              Cancelar processamento
                            </DropdownMenuItem>
                          ) : null}
                          <DropdownMenuSeparator />
                          <DropdownMenuItem destructive onSelect={() => setPendingDelete(project)}>
                            <Trash2 aria-hidden="true" />
                            Excluir
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      {projects.data ? (
        <Pagination
          className="mt-4"
          page={projects.data.page || page}
          pageSize={projects.data.pageSize || PAGE_SIZE}
          total={projects.data.total}
          onPageChange={(next) => setParams({ page: next })}
          itemLabel="projetos"
        />
      ) : null}

      <ConfirmDialog
        open={!!pendingDelete}
        onOpenChange={(open) => !open && setPendingDelete(null)}
        title="Excluir projeto?"
        description={
          pendingDelete
            ? `“${pendingDelete.title}” e todos os seus clipes serão removidos. Trabalhos em andamento são cancelados. Esta ação não pode ser desfeita.`
            : undefined
        }
        confirmLabel="Excluir"
        destructive
        loading={deleteProject.isPending}
        onConfirm={confirmDelete}
      />
      <ConfirmDialog
        open={!!pendingCancel}
        onOpenChange={(open) => !open && setPendingCancel(null)}
        title="Cancelar processamento?"
        description={
          pendingCancel
            ? `O progresso da análise de “${pendingCancel.title}” é perdido e os minutos já processados continuam contando na cota do período.`
            : undefined
        }
        confirmLabel="Cancelar processamento"
        cancelLabel="Continuar processando"
        destructive
        loading={cancelJob.isPending}
        onConfirm={confirmCancel}
      />
    </>
  );
}
