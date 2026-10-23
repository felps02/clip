"use client";

import { useQueryClient } from "@tanstack/react-query";
import { AudioLines, Check, ChevronDown, Clapperboard, Eye, FileVideo, Link2, ListChecks, Pencil, RefreshCw, ScanSearch, Trash2, X } from "lucide-react";
import Link from "next/link";
import { useParams, useRouter } from "next/navigation";
import { useEffect, useState, type ReactNode } from "react";
import { toast } from "sonner";
import { PAGE_TITLE_CLASS, PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/dialog";
import { EmptyState } from "@/components/ui/empty-state";
import { Input } from "@/components/ui/input";
import { NativeSelect } from "@/components/ui/select";
import { CardSkeleton, ClipCardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { CandidateCard } from "@/components/video/candidate-card";
import { ClipCard } from "@/components/video/clip-card";
import { JobStatus, JobStatusBadge } from "@/components/video/job-status";
import { VideoPlayer } from "@/components/video/video-player";
import { pickAnalysisJob } from "@/features/projects/analysis-job";
import { settingsForProject } from "@/features/projects/analysis-settings";
import { GenerateClipDialog } from "@/features/projects/generate-clip-dialog";
import { ArchiveDownloadLink, NoSpeechBanner, ResultClipCard } from "@/features/projects/project-results";
import { ReanalyzeDialog } from "@/features/projects/reanalyze-dialog";
import { analysisFoundNoSpeech, NO_SPEECH_SUBTITLES_TEXT } from "@/features/projects/subtitle-notes";
import { describeError, isApiError } from "@/lib/api/client";
import { useMe } from "@/lib/api/hooks/use-auth";
import { useClips } from "@/lib/api/hooks/use-clips";

import { useCancelJob, useCandidates, useDeleteProject, useJob, useProject, useUpdateProject } from "@/lib/api/hooks/use-projects";
import { queryKeys } from "@/lib/api/query-keys";
import type { Candidate, CandidateCategory, ClipSummary, ProjectDetail, SignalsAvailability } from "@/lib/api/types";
import { formatBytes, formatDate, formatDuration, formatNumber, formatSeconds } from "@/lib/format";
import {
  CANDIDATE_CATEGORY_LABELS,
  isActiveJobStatus,
  isTerminalJobStatus,
  platformLabel,
  PROJECT_STATUS_LABELS,
  PROJECT_STATUS_TONES,
  SIGNAL_LABELS,
} from "@/lib/labels";
import { cn } from "@/lib/utils";

function SignalsPanel({ signals }: { signals?: Partial<SignalsAvailability> | null }) {
  const entries = (["audio", "transcript", "visual"] as const).map((key) => ({
    key,
    label: SIGNAL_LABELS[key],
    available: signals?.[key]?.available,
  }));
  return (
    <ul className="flex flex-wrap gap-2" aria-label="Sinais disponíveis">
      {entries.map((entry) => (
        <li key={entry.key}>
          <Badge tone={entry.available === true ? "success" : entry.available === false ? "warning" : "neutral"}>
            {entry.available === true ? <Check className="size-3" aria-hidden="true" /> : <X className="size-3" aria-hidden="true" />}
            {entry.label} {entry.available === true ? "disponível" : entry.available === false ? "indisponível" : "não informado"}
          </Badge>
        </li>
      ))}
    </ul>
  );
}

function SourceInfo({ detail }: { detail: ProjectDetail }) {
  const { project, source } = detail;
  const asset = source.asset;
  const rows: Array<[string, string]> = [];
  if (asset) {
    rows.push(["Arquivo", asset.originalFileName]);
    rows.push(["Tamanho", formatBytes(asset.sizeBytes)]);
    if (asset.width && asset.height) rows.push(["Resolução", `${asset.width}×${asset.height}${asset.fps ? ` · ${Math.round(asset.fps)} fps` : ""}`]);
    if (asset.videoCodec || asset.audioCodec) rows.push(["Codecs", [asset.videoCodec, asset.audioCodec].filter(Boolean).join(" / ")]);
  }
  if (project.sourceKind === "link" && project.sourceUrl) rows.push(["Link", project.sourceUrl]);
  return (
    <dl className="grid gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
      {rows.map(([label, value]) => (
        <div key={label} className="min-w-0">
          <dt className="text-xs text-fg-subtle">{label}</dt>
          <dd className="truncate text-fg-muted" title={value}>
            {value}
          </dd>
        </div>
      ))}
    </dl>
  );
}

/** Page heading with an inline editor; the form never lives inside the `<h1>`. */
function EditableTitle({ title, projectId }: { title: string; projectId: string }) {
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(title);
  const [error, setError] = useState<string | null>(null);
  const update = useUpdateProject(projectId);

  const save = () => {
    const next = draft.trim();
    if (!next) {
      setError("Informe um título para o projeto.");
      return;
    }
    if (next === title) {
      setEditing(false);
      return;
    }
    update.mutate(
      { title: next },
      {
        onSuccess: () => {
          toast.success("Título atualizado");
          setEditing(false);
        },
        onError: (mutationError) => setError(describeError(mutationError)),
      },
    );
  };

  if (!editing) {
    return (
      <div className="flex min-w-0 items-center gap-2">
        <h1 className={PAGE_TITLE_CLASS}>{title}</h1>
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label="Editar título"
          onClick={() => {
            setDraft(title);
            setError(null);
            setEditing(true);
          }}
        >
          <Pencil aria-hidden="true" />
        </Button>
      </div>
    );
  }

  return (
    <div className="min-w-0">
      <h1 className="sr-only">{title}</h1>
      <form
        className="flex items-center gap-2"
        noValidate
        onSubmit={(event) => {
          event.preventDefault();
          save();
        }}
      >
        <Input
          value={draft}
          onChange={(event) => {
            setDraft(event.target.value);
            setError(null);
          }}
          maxLength={120}
          aria-label="Título do projeto"
          className="h-9 min-w-0 flex-1 text-base sm:w-80 sm:flex-none"
          invalid={!!error}
          aria-describedby={error ? "project-title-error" : undefined}
          autoFocus
        />
        <Button type="submit" size="icon-sm" aria-label="Salvar título" loading={update.isPending}>
          <Check aria-hidden="true" />
        </Button>
        <Button
          type="button"
          variant="ghost"
          size="icon-sm"
          aria-label="Cancelar edição"
          onClick={() => {
            setEditing(false);
            setError(null);
          }}
        >
          <X aria-hidden="true" />
        </Button>
      </form>
      {error ? (
        <p id="project-title-error" className="mt-1 text-xs text-danger" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}

/** Results-screen section that starts collapsed; its heading (with the id) always stays visible. */
function CollapsibleSection({
  id,
  icon,
  title,
  count,
  children,
  actions,
  badge,
}: {
  id: string;
  icon: ReactNode;
  title: string;
  count?: number | null;
  children: ReactNode;
  actions?: ReactNode;
  /** Shown next to the title even while collapsed (e.g. the analysis status). */
  badge?: ReactNode;
}) {
  const [open, setOpen] = useState(false);
  const contentId = `${id}-conteudo`;
  return (
    <section className="mt-8" aria-labelledby={id}>
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-line pb-3">
        <h2 id={id} className="text-lg font-semibold">
          <button
            type="button"
            onClick={() => setOpen((value) => !value)}
            aria-expanded={open}
            aria-controls={contentId}
            className="flex items-center gap-2 rounded-sm text-left focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
          >
            <span className="text-accent [&_svg]:size-5">{icon}</span>
            {title}
            {typeof count === "number" ? <span className="text-sm font-normal text-fg-muted">({formatNumber(count)})</span> : null}
            <ChevronDown className={cn("size-4 text-fg-subtle transition-transform duration-200", open && "rotate-180")} aria-hidden="true" />
          </button>
        </h2>
        {badge ? <div className="mr-auto">{badge}</div> : null}
        {open ? actions : null}
      </div>
      <div id={contentId} hidden={!open} className="pt-4">
        {open ? children : null}
      </div>
    </section>
  );
}

/** Short recordings: the reason no moment was found is the clip minimum, never the sensitivity. */
function shortSourceInfo(detail: ProjectDetail): { durationSeconds: number; minClipSeconds: number } | null {
  const durationSeconds = detail.source.asset?.durationSeconds ?? detail.project.durationSeconds ?? null;
  const minClipSeconds = detail.project.settings?.minClipSeconds ?? null;
  if (typeof durationSeconds !== "number" || typeof minClipSeconds !== "number" || durationSeconds <= 0) return null;
  return durationSeconds < minClipSeconds ? { durationSeconds, minClipSeconds } : null;
}

export function ProjectDetailPage() {
  const params = useParams<{ id: string }>();
  const projectId = params.id;
  const router = useRouter();
  const queryClient = useQueryClient();
  const me = useMe();
  const project = useProject(projectId);
  const detail = project.data;
  const analysisJob = detail ? pickAnalysisJob(detail.jobs) : undefined;
  const job = useJob(projectId, analysisJob?.id);
  const jobStatus = job.data?.status ?? analysisJob?.status;

  const [sort, setSort] = useState<"score" | "time">("score");
  const [category, setCategory] = useState<CandidateCategory | "">("");
  const candidates = useCandidates(projectId, { sort, category }, { enabled: !!detail && (detail.candidateCount > 0 || isTerminalJobStatus(jobStatus)) });
  // Page size cap of the API: the results screen lists every clip of the analysis at once.
  const clips = useClips({ projectId, pageSize: 100 }, { enabled: !!detail });
  const [playingClipId, setPlayingClipId] = useState<string | null>(null);

  const cancelJob = useCancelJob();
  const deleteProject = useDeleteProject();
  const [generateFor, setGenerateFor] = useState<Candidate | null>(null);
  const [reanalyzeOpen, setReanalyzeOpen] = useState(false);
  const [deleteOpen, setDeleteOpen] = useState(false);
  const [cancelOpen, setCancelOpen] = useState(false);
  const [previewOpen, setPreviewOpen] = useState(false);

  // When the analysis finishes, refresh the dependent lists.
  useEffect(() => {
    if (jobStatus === "completed") {
      void queryClient.invalidateQueries({ queryKey: queryKeys.projects.detail(projectId) });
      void queryClient.invalidateQueries({ queryKey: queryKeys.clips.all });
      void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    }
  }, [jobStatus, projectId, queryClient]);

  if (project.isError) {
    const notFound = isApiError(project.error) && project.error.status === 404;
    return (
      <>
        <PageHeader title={notFound ? "Projeto não encontrado" : "Erro ao carregar o projeto"} backHref="/projects" backLabel="Projetos" />
        <Alert
          variant={notFound ? "warning" : "danger"}
          action={
            notFound ? undefined : (
              <Button size="sm" variant="secondary" onClick={() => project.refetch()} loading={project.isFetching}>
                Tentar novamente
              </Button>
            )
          }
        >
          {notFound ? "Este projeto não existe ou foi excluído." : describeError(project.error)}
        </Alert>
      </>
    );
  }

  if (project.isPending || !detail) {
    return (
      <div className="space-y-6" aria-busy="true">
        <Skeleton className="h-8 w-64" />
        <CardSkeleton />
        <CardSkeleton />
      </div>
    );
  }

  // Re-analysis and new clips start from this project's own settings (capped by the plan).
  const settingsForClips = settingsForProject(detail.project.settings, me.data?.profile.editingPreferences, me.data?.limits);
  const signals = candidates.data?.signals ?? job.data?.result?.signals ?? null;
  const candidateItems = candidates.data?.items ?? [];
  const activeAnalysis = isActiveJobStatus(jobStatus);
  const clipItems = clips.data?.items ?? [];
  const clipTotal = clips.data?.total ?? detail.clipCount;
  const readyClips = clipItems.filter((clip) => clip.status === "ready").length;
  // Vertical clips get a denser grid, so a row of 9:16 thumbnails does not fill the whole screen.
  const portraitResults = clipItems.filter((clip) => clip.aspectRatio === "9:16").length * 2 >= clipItems.length;
  // Results first once the latest analysis finished with clips; the progress layout while it runs.
  const showResults = jobStatus === "completed" && (clips.data ? clips.data.total > 0 : detail.clipCount > 0);
  const subtitlesRequested = detail.project.settings?.subtitles?.enabled ?? settingsForClips.subtitles.enabled;
  const noSpeech = analysisFoundNoSpeech(signals);
  const shortSource = shortSourceInfo(detail);
  const sourceDuration = detail.source.asset?.durationSeconds ?? detail.project.durationSeconds ?? undefined;
  const assistantContext = {
    ...(detail.source.asset?.originalFileName ? { fileName: detail.source.asset.originalFileName } : {}),
    ...(typeof sourceDuration === "number" && sourceDuration > 0 ? { durationSeconds: Math.round(sourceDuration) } : {}),
  };
  const playClip = (clip: ClipSummary) => setPlayingClipId(clip.id);
  // Before "Baixar todos": refreshes an expired session (the API client does it on 401) and
  // confirms the project still has a ready clip, with the list the page shows.
  const archiveHasReadyClip = async () => {
    const latest = await clips.refetch({ throwOnError: true });
    return (latest.data?.items ?? []).some((clip) => clip.status === "ready");
  };

  const confirmCancel = () => {
    if (!analysisJob) return;
    cancelJob.mutate(
      { projectId, jobId: analysisJob.id },
      {
        onSuccess: () => {
          toast.success("Cancelamento solicitado");
          setCancelOpen(false);
        },
        onError: (error) => toast.error(describeError(error)),
      },
    );
  };

  const confirmDelete = () => {
    deleteProject.mutate(projectId, {
      onSuccess: () => {
        toast.success("Projeto excluído");
        router.push("/projects");
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  return (
    <>
      <PageHeader
        backHref="/projects"
        backLabel="Projetos"
        heading={<EditableTitle title={detail.project.title} projectId={projectId} />}
        description={
          <span className="inline-flex flex-wrap items-center gap-2">
            <Badge tone={PROJECT_STATUS_TONES[detail.project.status] ?? "neutral"} pulse={detail.project.status === "processing"}>
              {PROJECT_STATUS_LABELS[detail.project.status] ?? detail.project.status}
            </Badge>
            <span className="inline-flex items-center gap-1 text-fg-muted">
              {detail.project.sourceKind === "link" ? <Link2 className="size-3.5" aria-hidden="true" /> : <FileVideo className="size-3.5" aria-hidden="true" />}
              {detail.project.sourceKind === "upload" ? "Upload" : platformLabel(detail.project.sourcePlatform)}
            </span>
            <span className="text-fg-muted">
              duração {formatDuration(detail.project.durationSeconds ?? detail.source.asset?.durationSeconds ?? null)}
            </span>
            <span className="text-fg-subtle">criado em {formatDate(detail.project.createdAt)}</span>
          </span>
        }
        actions={
          <>
            {detail.source.previewable && detail.source.previewUrl ? (
              <Button variant="secondary" onClick={() => setPreviewOpen((open) => !open)} leftIcon={<Eye aria-hidden="true" />}>
                {previewOpen ? "Ocultar gravação" : "Ver gravação"}
              </Button>
            ) : null}
            <Button
              variant="secondary"
              onClick={() => setReanalyzeOpen(true)}
              disabled={activeAnalysis}
              leftIcon={<RefreshCw aria-hidden="true" />}
            >
              Analisar novamente
            </Button>
            <Button variant="danger" onClick={() => setDeleteOpen(true)} leftIcon={<Trash2 aria-hidden="true" />}>
              Excluir
            </Button>
          </>
        }
      />

      {previewOpen && detail.source.previewUrl ? (
        <VideoPlayer src={detail.source.previewUrl} aspectRatio="16:9" className="mb-6" />
      ) : null}

      {showResults ? (
        <>
          <section aria-labelledby="clipes-prontos">
            <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
              <h2 id="clipes-prontos" className="flex items-center gap-2 text-xl font-semibold">
                <Clapperboard className="size-5 text-accent" aria-hidden="true" />
                Clipes prontos <span className="text-base font-normal text-fg-muted">({formatNumber(readyClips)})</span>
              </h2>
              <div className="flex flex-wrap items-center gap-2">
                {readyClips > 0 ? <ArchiveDownloadLink projectId={projectId} checkReady={archiveHasReadyClip} /> : null}
                <Link href={`/library?projectId=${projectId}`} className={cn(buttonVariants({ variant: "ghost", size: "sm" }))}>
                  Ver na biblioteca
                </Link>
              </div>
            </div>
            {subtitlesRequested && noSpeech ? <NoSpeechBanner /> : null}
            {clips.isError ? (
              <Alert
                variant="danger"
                title="Não foi possível carregar os clipes"
                action={
                  <Button size="sm" variant="secondary" onClick={() => clips.refetch()} loading={clips.isFetching}>
                    Tentar novamente
                  </Button>
                }
              >
                {describeError(clips.error)}
              </Alert>
            ) : clips.isPending ? (
              <div className="grid grid-cols-2 gap-4 md:grid-cols-3 xl:grid-cols-4">
                <ClipCardSkeleton />
                <ClipCardSkeleton />
                <ClipCardSkeleton />
              </div>
            ) : (
              <>
                <div
                  className={cn(
                    "grid items-start gap-3 sm:gap-4",
                    portraitResults ? "grid-cols-2 sm:grid-cols-3 lg:grid-cols-4 xl:grid-cols-5" : "grid-cols-1 sm:grid-cols-2 xl:grid-cols-3",
                  )}
                >
                  {clipItems.map((clip) => (
                    <ResultClipCard
                      key={clip.id}
                      clip={clip}
                      playing={playingClipId === clip.id}
                      onPlay={playClip}
                      subtitlesRequested={subtitlesRequested}
                    />
                  ))}
                </div>
                {clipTotal > clipItems.length ? (
                  <p className="mt-3 text-xs text-fg-muted">
                    Mostrando {formatNumber(clipItems.length)} de {formatNumber(clipTotal)} clipes.{" "}
                    <Link href={`/library?projectId=${projectId}`} className="text-accent underline-offset-4 hover:underline">
                      Ver todos na biblioteca
                    </Link>
                  </p>
                ) : null}
              </>
            )}
          </section>

          <CollapsibleSection
            id="detalhes-analise"
            icon={<ListChecks />}
            title="Detalhes da análise"
            badge={jobStatus ? <JobStatusBadge status={jobStatus} /> : null}
          >
            <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
              <div className="min-w-0 space-y-6">
                {analysisJob ? (
                  <JobStatus
                    job={job.data ?? analysisJob}
                    onCancel={() => setCancelOpen(true)}
                    cancelling={cancelJob.isPending}
                    onRetry={() => setReanalyzeOpen(true)}
                  />
                ) : (
                  <Alert variant="info">Nenhum trabalho de análise registrado para este projeto.</Alert>
                )}
              </div>
              <div className="min-w-0 space-y-6">
                <Card>
                  <CardHeader>
                    <CardTitle className="flex items-center gap-2">
                      <AudioLines className="size-4 text-accent" aria-hidden="true" />
                      Sinais usados
                    </CardTitle>
                  </CardHeader>
                  <CardContent className="space-y-3">
                    <SignalsPanel signals={signals} />
                    {signals?.transcript?.available === false ? (
                      <p className="text-xs text-fg-muted">Transcrição indisponível neste trabalho: os momentos foram encontrados só com áudio e imagem.</p>
                    ) : null}
                    {signals?.visual?.available === false ? (
                      <p className="text-xs text-fg-muted">Sinais visuais indisponíveis (fonte sem vídeo ou análise visual desativada).</p>
                    ) : null}
                    {analysisFoundNoSpeech(signals) ? (
                      <p className="text-xs text-fg-muted">Nenhuma fala detectada na gravação: os clipes saem sem legendas.</p>
                    ) : null}
                    {!signals ? <p className="text-xs text-fg-muted">A disponibilidade dos sinais aparece quando a análise terminar.</p> : null}
                  </CardContent>
                </Card>
                <Card>
                  <CardHeader>
                    <CardTitle>Fonte</CardTitle>
                  </CardHeader>
                  <CardContent>
                    <SourceInfo detail={detail} />
                  </CardContent>
                </Card>
              </div>
            </div>
          </CollapsibleSection>

          <CollapsibleSection
            id="candidatos"
            icon={<ScanSearch />}
            title="Momentos encontrados"
            count={candidates.data ? candidateItems.length : detail.candidateCount}
            actions={
              <div className="flex flex-wrap items-center gap-2">
                <NativeSelect value={sort} onChange={(event) => setSort(event.target.value as "score" | "time")} aria-label="Ordenar momentos" className="h-9 w-44">
                  <option value="score">Maior relevância</option>
                  <option value="time">Ordem na gravação</option>
                </NativeSelect>
                <NativeSelect
                  value={category}
                  onChange={(event) => setCategory(event.target.value as CandidateCategory | "")}
                  aria-label="Filtrar por categoria"
                  className="h-9 w-44"
                >
                  <option value="">Todas as categorias</option>
                  {(Object.keys(CANDIDATE_CATEGORY_LABELS) as CandidateCategory[]).map((key) => (
                    <option key={key} value={key}>
                      {CANDIDATE_CATEGORY_LABELS[key]}
                    </option>
                  ))}
                </NativeSelect>
              </div>
            }
          >
            <p className="mb-4 text-xs text-fg-subtle">
              A relevância estimada é uma pontuação interna de 0 a 100 que ordena os momentos desta gravação. Não é uma previsão de alcance
              nem de visualizações.
            </p>

            {candidates.isError ? (
              <Alert variant="danger">{describeError(candidates.error)}</Alert>
            ) : !candidates.isEnabled && activeAnalysis ? (
              <Alert variant="info">Os momentos encontrados aparecem aqui assim que a etapa de detecção terminar.</Alert>
            ) : candidates.isPending && candidates.isEnabled ? (
              <div className="space-y-3" aria-busy="true">
                <CardSkeleton />
                <CardSkeleton />
              </div>
            ) : candidateItems.length === 0 ? (
              activeAnalysis ? (
                <EmptyState icon={<ScanSearch />} title="Análise em andamento" description="A lista é preenchida ao final da detecção de momentos." compact />
              ) : shortSource ? (
                // Too short for the clip minimum: suggest a smaller minimum, never the sensitivity.
                <EmptyState
                  icon={<ScanSearch />}
                  title="Nenhum momento encontrado"
                  description={`O vídeo (${formatSeconds(shortSource.durationSeconds)}) é mais curto que a duração mínima dos clipes (${formatSeconds(shortSource.minClipSeconds)}). Use “Analisar novamente” e diminua a “Duração dos clipes” para uma mínima de até ${Math.max(5, Math.floor(shortSource.durationSeconds))} s.`}
                  action={
                    <Button variant="secondary" onClick={() => setReanalyzeOpen(true)} leftIcon={<RefreshCw aria-hidden="true" />}>
                      Analisar novamente
                    </Button>
                  }
                  compact
                />
              ) : (
                <EmptyState
                  icon={<ScanSearch />}
                  title="Nenhum momento encontrado"
                  description="A análise não encontrou trechos acima do limiar. Tente uma sensibilidade mais alta em “Analisar novamente”."
                  compact
                />
              )
            ) : (
              <div className="grid gap-4 xl:grid-cols-2">
                {candidateItems.map((candidate) => (
                  <CandidateCard
                    key={candidate.id}
                    candidate={candidate}
                    onGenerateClip={setGenerateFor}
                    subtitleNote={subtitlesRequested && noSpeech ? NO_SPEECH_SUBTITLES_TEXT : null}
                  />
                ))}
              </div>
            )}
          </CollapsibleSection>
        </>
      ) : (
        <>
          <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
            <div className="min-w-0 space-y-6">
              {analysisJob ? (
                <JobStatus
                  job={job.data ?? analysisJob}
                  onCancel={() => setCancelOpen(true)}
                  cancelling={cancelJob.isPending}
                  onRetry={() => setReanalyzeOpen(true)}
                />
              ) : (
                <Alert variant="info">Nenhum trabalho de análise registrado para este projeto.</Alert>
              )}
            </div>
            <div className="min-w-0 space-y-6">
              <Card>
                <CardHeader>
                  <CardTitle className="flex items-center gap-2">
                    <AudioLines className="size-4 text-accent" aria-hidden="true" />
                    Sinais usados
                  </CardTitle>
                </CardHeader>
                <CardContent className="space-y-3">
                  <SignalsPanel signals={signals} />
                  {signals?.transcript?.available === false ? (
                    <p className="text-xs text-fg-muted">Transcrição indisponível neste trabalho: os momentos foram encontrados só com áudio e imagem.</p>
                  ) : null}
                  {signals?.visual?.available === false ? (
                    <p className="text-xs text-fg-muted">Sinais visuais indisponíveis (fonte sem vídeo ou análise visual desativada).</p>
                  ) : null}
                  {analysisFoundNoSpeech(signals) ? (
                    <p className="text-xs text-fg-muted">Nenhuma fala detectada na gravação: os clipes saem sem legendas.</p>
                  ) : null}
                  {!signals ? <p className="text-xs text-fg-muted">A disponibilidade dos sinais aparece quando a análise terminar.</p> : null}
                </CardContent>
              </Card>
              <Card>
                <CardHeader>
                  <CardTitle>Fonte</CardTitle>
                </CardHeader>
                <CardContent>
                  <SourceInfo detail={detail} />
                </CardContent>
              </Card>
            </div>
          </div>


          <section className="mt-8" aria-labelledby="candidatos">
            <div className="mb-4 flex flex-wrap items-center justify-between gap-3">
              <h2 id="candidatos" className="flex items-center gap-2 text-lg font-semibold">
                <ScanSearch className="size-5 text-accent" aria-hidden="true" />
                Momentos encontrados
                {candidates.data ? <span className="text-sm font-normal text-fg-muted">({formatNumber(candidateItems.length)})</span> : null}
              </h2>
              <div className="flex flex-wrap items-center gap-2">
                <NativeSelect value={sort} onChange={(event) => setSort(event.target.value as "score" | "time")} aria-label="Ordenar momentos" className="h-9 w-44">
                  <option value="score">Maior relevância</option>
                  <option value="time">Ordem na gravação</option>
                </NativeSelect>
                <NativeSelect
                  value={category}
                  onChange={(event) => setCategory(event.target.value as CandidateCategory | "")}
                  aria-label="Filtrar por categoria"
                  className="h-9 w-44"
                >
                  <option value="">Todas as categorias</option>
                  {(Object.keys(CANDIDATE_CATEGORY_LABELS) as CandidateCategory[]).map((key) => (
                    <option key={key} value={key}>
                      {CANDIDATE_CATEGORY_LABELS[key]}
                    </option>
                  ))}
                </NativeSelect>
              </div>
            </div>
            <p className="mb-4 text-xs text-fg-subtle">
              A relevância estimada é uma pontuação interna de 0 a 100 que ordena os momentos desta gravação. Não é uma previsão de alcance
              nem de visualizações.
            </p>

            {candidates.isError ? (
              <Alert variant="danger">{describeError(candidates.error)}</Alert>
            ) : !candidates.isEnabled && activeAnalysis ? (
              <Alert variant="info">Os momentos encontrados aparecem aqui assim que a etapa de detecção terminar.</Alert>
            ) : candidates.isPending && candidates.isEnabled ? (
              <div className="space-y-3" aria-busy="true">
                <CardSkeleton />
                <CardSkeleton />
              </div>
            ) : candidateItems.length === 0 ? (
              activeAnalysis ? (
                <EmptyState icon={<ScanSearch />} title="Análise em andamento" description="A lista é preenchida ao final da detecção de momentos." compact />
              ) : shortSource ? (
                // Too short for the clip minimum: suggest a smaller minimum, never the sensitivity.
                <EmptyState
                  icon={<ScanSearch />}
                  title="Nenhum momento encontrado"
                  description={`O vídeo (${formatSeconds(shortSource.durationSeconds)}) é mais curto que a duração mínima dos clipes (${formatSeconds(shortSource.minClipSeconds)}). Use “Analisar novamente” e diminua a “Duração dos clipes” para uma mínima de até ${Math.max(5, Math.floor(shortSource.durationSeconds))} s.`}
                  action={
                    <Button variant="secondary" onClick={() => setReanalyzeOpen(true)} leftIcon={<RefreshCw aria-hidden="true" />}>
                      Analisar novamente
                    </Button>
                  }
                  compact
                />
              ) : (
                <EmptyState
                  icon={<ScanSearch />}
                  title="Nenhum momento encontrado"
                  description="A análise não encontrou trechos acima do limiar. Tente uma sensibilidade mais alta em “Analisar novamente”."
                  compact
                />
              )
            ) : (
              <div className="grid gap-4 xl:grid-cols-2">
                {candidateItems.map((candidate) => (
                  <CandidateCard
                    key={candidate.id}
                    candidate={candidate}
                    onGenerateClip={setGenerateFor}
                    subtitleNote={subtitlesRequested && noSpeech ? NO_SPEECH_SUBTITLES_TEXT : null}
                  />
                ))}
              </div>
            )}
          </section>

          <section className="mt-8" aria-labelledby="clipes-projeto">
            <div className="mb-4 flex items-center justify-between">
              <h2 id="clipes-projeto" className="text-lg font-semibold">
                Clipes {clips.data ? <span className="text-sm font-normal text-fg-muted">({formatNumber(clips.data.total)})</span> : null}
              </h2>
              <Link href={`/library?projectId=${projectId}`} className={cn(buttonVariants({ variant: "ghost", size: "sm" }))}>
                Ver na biblioteca
              </Link>
            </div>
            {clips.isError ? (
              <Alert
                variant="danger"
                title="Não foi possível carregar os clipes"
                action={
                  <Button size="sm" variant="secondary" onClick={() => clips.refetch()} loading={clips.isFetching}>
                    Tentar novamente
                  </Button>
                }
              >
                {describeError(clips.error)}
              </Alert>
            ) : clips.isPending ? (
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                <ClipCardSkeleton />
                <ClipCardSkeleton />
              </div>
            ) : clips.data && clips.data.items.length > 0 ? (
              <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-3">
                {clips.data.items.map((clip) => (
                  <ClipCard key={clip.id} clip={clip} />
                ))}
              </div>
            ) : (
              <EmptyState
                title="Nenhum clipe gerado"
                description="Escolha um momento acima e clique em “Gerar clipe”, ou use “Todos os momentos encontrados” em “Clipes prontos automaticamente” na próxima análise."
                compact
              />
            )}
          </section>

        </>
      )}

      <GenerateClipDialog
        projectId={projectId}
        projectTitle={detail.project.title}
        candidate={generateFor}
        onOpenChange={(open) => !open && setGenerateFor(null)}
        defaults={settingsForClips}
        limits={me.data?.limits}
      />
      <ReanalyzeDialog
        projectId={projectId}
        open={reanalyzeOpen}
        onOpenChange={setReanalyzeOpen}
        initialSettings={settingsForClips}
        limits={me.data?.limits}
        context={assistantContext}
        sourceDurationSeconds={sourceDuration ?? null}
      />
      <ConfirmDialog
        open={deleteOpen}
        onOpenChange={setDeleteOpen}
        title="Excluir projeto?"
        description="A gravação, os momentos encontrados e todos os clipes deste projeto serão removidos. Esta ação não pode ser desfeita."
        confirmLabel="Excluir"
        destructive
        loading={deleteProject.isPending}
        onConfirm={confirmDelete}
      />
      <ConfirmDialog
        open={cancelOpen}
        onOpenChange={setCancelOpen}
        title="Cancelar processamento?"
        description="O progresso desta análise é perdido e os minutos já processados continuam contando na cota do período."
        confirmLabel="Cancelar processamento"
        cancelLabel="Continuar processando"
        destructive
        loading={cancelJob.isPending}
        onConfirm={confirmCancel}
      />
    </>
  );
}
