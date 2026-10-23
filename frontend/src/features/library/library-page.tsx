"use client";

import { ArrowDownUp, Clapperboard, Download, MoreHorizontal, Pencil, Play, Plus, Search, Trash2 } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useCallback, useEffect, useState } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Button, buttonVariants } from "@/components/ui/button";
import { ConfirmDialog, Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { Pagination } from "@/components/ui/pagination";
import { NativeSelect } from "@/components/ui/select";
import { ClipCardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { ClipCard } from "@/components/video/clip-card";
import { VideoPlayer } from "@/components/video/video-player";
import { describeError } from "@/lib/api/client";
import { useClip, useClipDownload, useClips, useDeleteClip } from "@/lib/api/hooks/use-clips";
import { useProjectOptions } from "@/lib/api/hooks/use-projects";
import type { CandidateCategory, ClipStatus, ClipSummary, ClipsQuery } from "@/lib/api/types";
import { openDownload } from "@/lib/download";
import { formatNumber, pluralize } from "@/lib/format";
import { CANDIDATE_CATEGORY_LABELS, CLIP_STATUS_LABELS } from "@/lib/labels";

const PAGE_SIZE = 24;
const SORTS: Array<{ value: NonNullable<ClipsQuery["sort"]>; label: string }> = [
  { value: "createdAt", label: "Data de criação" },
  { value: "score", label: "Relevância estimada" },
  { value: "duration", label: "Duração" },
];

export { openDownload };

function PreviewDialog({ clipId, onClose }: { clipId: string | null; onClose: () => void }) {
  const clip = useClip(clipId ?? undefined);
  const streamUrl = clip.data?.currentRender?.status === "ready" ? clip.data.currentRender.streamUrl : null;
  return (
    <Dialog open={!!clipId} onOpenChange={(open) => !open && onClose()}>
      <DialogContent size="lg">
        <DialogHeader>
          <DialogTitle>{clip.data?.title ?? "Pré-visualização"}</DialogTitle>
          <DialogDescription>
            {clip.data
              ? `${clip.data.aspectRatio} · versão ${clip.data.currentRender?.version ?? "—"}`
              : "Carregando o clipe…"}
          </DialogDescription>
        </DialogHeader>
        {clip.isPending ? (
          <Skeleton className="aspect-video w-full" />
        ) : clip.isError ? (
          <Alert variant="danger">{describeError(clip.error)}</Alert>
        ) : (
          <VideoPlayer
            src={streamUrl}
            aspectRatio={clip.data?.aspectRatio ?? "auto"}
            autoPlay
            emptyMessage="Este clipe ainda não tem uma renderização pronta para reproduzir."
          />
        )}
        {clip.data ? (
          <div className="flex justify-end">
            <Link href={`/clips/${clip.data.id}`} className={buttonVariants({ variant: "secondary", size: "sm" })}>
              <Pencil aria-hidden="true" />
              Abrir no editor
            </Link>
          </div>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

export function LibraryPage() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();

  const page = Math.max(1, Number(searchParams.get("page")) || 1);
  const projectId = searchParams.get("projectId") ?? "";
  const category = (searchParams.get("category") ?? "") as CandidateCategory | "";
  const status = (searchParams.get("status") ?? "") as ClipStatus | "";
  const q = searchParams.get("q") ?? "";
  const sort = (searchParams.get("sort") as ClipsQuery["sort"]) || "createdAt";
  const order = (searchParams.get("order") as ClipsQuery["order"]) || "desc";

  const setParams = useCallback(
    (next: Record<string, string | number | undefined>) => {
      const params = new URLSearchParams(searchParams.toString());
      for (const [key, value] of Object.entries(next)) {
        if (value === undefined || value === "" || value === null) params.delete(key);
        else params.set(key, String(value));
      }
      if (!("page" in next)) params.delete("page");
      const qs = params.toString();
      router.replace(qs ? `${pathname}?${qs}` : pathname, { scroll: false });
    },
    [pathname, router, searchParams],
  );

  const [searchInput, setSearchInput] = useState(q);
  useEffect(() => {
    if (searchInput === q) return;
    const timer = window.setTimeout(() => setParams({ q: searchInput.trim() || undefined }), 400);
    return () => window.clearTimeout(timer);
  }, [searchInput, q, setParams]);

  const clips = useClips({ page, pageSize: PAGE_SIZE, projectId, category, status, q, sort, order });
  // Names for the filter only: no polling (the clip list itself refreshes while renders run).
  const projectOptions = useProjectOptions();
  const optionItems = projectOptions.data?.items ?? [];
  const optionsTruncated = !!projectOptions.data && projectOptions.data.total > optionItems.length;
  const selectedMissing = !!projectId && !!projectOptions.data && !optionItems.some((project) => project.id === projectId);
  const deleteClip = useDeleteClip();
  const download = useClipDownload();
  const [previewId, setPreviewId] = useState<string | null>(null);
  const [pendingDelete, setPendingDelete] = useState<ClipSummary | null>(null);

  const handleDownload = (clip: ClipSummary) => {
    download.mutate(
      { clipId: clip.id },
      {
        onSuccess: ({ url }) => openDownload(url),
        onError: (error) => toast.error(describeError(error)),
      },
    );
  };

  const confirmDelete = () => {
    if (!pendingDelete) return;
    deleteClip.mutate(pendingDelete.id, {
      onSuccess: () => {
        toast.success("Clipe excluído");
        setPendingDelete(null);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const items = clips.data?.items ?? [];
  const hasFilters = !!(projectId || category || status || q);

  return (
    <>
      <PageHeader
        title="Biblioteca"
        description="Todos os clipes gerados, prontos para revisar, baixar e publicar."
        actions={
          <Link href="/new" className={buttonVariants()}>
            <Plus aria-hidden="true" />
            Novo projeto
          </Link>
        }
      />

      <div className="mb-5 grid gap-3 md:grid-cols-[1.4fr_1fr_1fr_1fr_auto]">
        <div className="relative">
          <Search className="pointer-events-none absolute left-3 top-1/2 size-4 -translate-y-1/2 text-fg-subtle" aria-hidden="true" />
          <Input
            type="search"
            value={searchInput}
            onChange={(event) => setSearchInput(event.target.value)}
            placeholder="Buscar por título"
            aria-label="Buscar clipes"
            className="pl-9"
          />
        </div>
        <NativeSelect value={projectId} onChange={(event) => setParams({ projectId: event.target.value })} aria-label="Filtrar por projeto">
          <option value="">Todos os projetos</option>
          {selectedMissing ? <option value={projectId}>Projeto selecionado</option> : null}
          {optionItems.map((project) => (
            <option key={project.id} value={project.id}>
              {project.title}
            </option>
          ))}
          {optionsTruncated && projectOptions.data ? (
            <option value="" disabled>
              {`Mostrando ${formatNumber(optionItems.length)} de ${formatNumber(projectOptions.data.total)} projetos; filtre a partir da página do projeto`}
            </option>
          ) : null}
        </NativeSelect>
        <NativeSelect value={category} onChange={(event) => setParams({ category: event.target.value })} aria-label="Filtrar por categoria">
          <option value="">Todas as categorias</option>
          {(Object.keys(CANDIDATE_CATEGORY_LABELS) as CandidateCategory[]).map((key) => (
            <option key={key} value={key}>
              {CANDIDATE_CATEGORY_LABELS[key]}
            </option>
          ))}
        </NativeSelect>
        <NativeSelect value={status} onChange={(event) => setParams({ status: event.target.value })} aria-label="Filtrar por status">
          <option value="">Todos os status</option>
          {(Object.keys(CLIP_STATUS_LABELS) as ClipStatus[]).map((key) => (
            <option key={key} value={key}>
              {CLIP_STATUS_LABELS[key]}
            </option>
          ))}
        </NativeSelect>
        <div className="flex gap-2">
          <NativeSelect value={sort} onChange={(event) => setParams({ sort: event.target.value })} aria-label="Ordenar por" className="min-w-40">
            {SORTS.map((option) => (
              <option key={option.value} value={option.value}>
                {option.label}
              </option>
            ))}
          </NativeSelect>
          <Button
            variant="secondary"
            size="icon"
            onClick={() => setParams({ order: order === "desc" ? "asc" : "desc" })}
            aria-label={order === "desc" ? "Ordem decrescente (clique para crescente)" : "Ordem crescente (clique para decrescente)"}
          >
            <ArrowDownUp aria-hidden="true" />
          </Button>
        </div>
      </div>

      {clips.isError ? (
        <Alert
          variant="danger"
          title="Não foi possível carregar a biblioteca"
          action={
            <Button size="sm" variant="secondary" onClick={() => clips.refetch()} loading={clips.isFetching}>
              Tentar novamente
            </Button>
          }
        >
          {describeError(clips.error)}
        </Alert>
      ) : clips.isPending ? (
        <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4" aria-busy="true">
          {Array.from({ length: 8 }).map((_, index) => (
            <ClipCardSkeleton key={index} />
          ))}
        </div>
      ) : items.length === 0 ? (
        <EmptyState
          icon={<Clapperboard />}
          title={hasFilters ? "Nenhum clipe com esses filtros" : "Sua biblioteca está vazia"}
          description={
            hasFilters
              ? "Ajuste a busca ou limpe os filtros para ver todos os clipes."
              : "Os clipes gerados a partir das suas gravações aparecem aqui."
          }
          action={
            hasFilters ? (
              <Button variant="secondary" onClick={() => router.replace(pathname)}>
                Limpar filtros
              </Button>
            ) : (
              <Link href="/new" className={buttonVariants()}>
                <Plus aria-hidden="true" />
                Criar primeiro projeto
              </Link>
            )
          }
        />
      ) : (
        <>
          <p className="mb-3 text-sm text-fg-muted">{pluralize(clips.data.total, "clipe", "clipes")}</p>
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
            {items.map((clip) => (
              <ClipCard
                key={clip.id}
                clip={clip}
                showProject
                onPreview={(target) => setPreviewId(target.id)}
                actions={
                  <DropdownMenu>
                    <DropdownMenuTrigger asChild>
                      <Button variant="ghost" size="icon-sm" aria-label={`Ações para ${clip.title}`}>
                        <MoreHorizontal aria-hidden="true" />
                      </Button>
                    </DropdownMenuTrigger>
                    <DropdownMenuContent align="end">
                      <DropdownMenuItem onSelect={() => setPreviewId(clip.id)} disabled={clip.status !== "ready"}>
                        <Play aria-hidden="true" />
                        Pré-visualizar
                      </DropdownMenuItem>
                      <DropdownMenuItem asChild>
                        <Link href={`/clips/${clip.id}`}>
                          <Pencil aria-hidden="true" />
                          Editar
                        </Link>
                      </DropdownMenuItem>
                      <DropdownMenuItem onSelect={() => handleDownload(clip)} disabled={clip.status !== "ready"}>
                        <Download aria-hidden="true" />
                        Baixar
                      </DropdownMenuItem>
                      <DropdownMenuSeparator />
                      <DropdownMenuItem destructive onSelect={() => setPendingDelete(clip)}>
                        <Trash2 aria-hidden="true" />
                        Excluir
                      </DropdownMenuItem>
                    </DropdownMenuContent>
                  </DropdownMenu>
                }
              />
            ))}
          </div>
          <Pagination
            className="mt-6"
            page={clips.data.page || page}
            pageSize={clips.data.pageSize || PAGE_SIZE}
            total={clips.data.total}
            onPageChange={(next) => setParams({ page: next > 1 ? next : undefined })}
            itemLabel="clipes"
          />
        </>
      )}

      <PreviewDialog clipId={previewId} onClose={() => setPreviewId(null)} />
      <ConfirmDialog
        open={!!pendingDelete}
        onOpenChange={(open) => !open && setPendingDelete(null)}
        title="Excluir clipe?"
        description={pendingDelete ? `“${pendingDelete.title}” e todas as suas versões renderizadas serão removidos.` : undefined}
        confirmLabel="Excluir"
        destructive
        loading={deleteClip.isPending}
        onConfirm={confirmDelete}
      />
    </>
  );
}
