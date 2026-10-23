"use client";

import { Captions, Download, Film, RefreshCw, Save, SkipBack, Trash2 } from "lucide-react";
import { useParams, useRouter } from "next/navigation";
import { useRef, useState } from "react";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { ChoiceGroup } from "@/components/ui/choice-group";
import { ConfirmDialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { CardSkeleton, Skeleton } from "@/components/ui/skeleton";
import { Switch } from "@/components/ui/switch";
import { JobStatus } from "@/components/video/job-status";
import { TrimSlider } from "@/components/video/trim-slider";
import { VideoPlayer, type VideoPlayerHandle } from "@/components/video/video-player";
import {
  DEFAULT_SUBTITLE_POSITION,
  DEFAULT_SUBTITLE_STYLE,
  isFramingAllowed,
  isSubtitlePositionAllowed,
  isSubtitleStyleAllowed,
} from "@/features/projects/analysis-settings";
import { AspectRatioIcon, PLAN_LOCKED_SUFFIX, SubtitleLockNote, WebcamLockNote } from "@/features/projects/analysis-settings-form";
import { SubtitlePreview } from "@/features/projects/subtitle-preview";
import { NO_SPEECH_SUBTITLES_TEXT, renderMissingSubtitles } from "@/features/projects/subtitle-notes";
import { openDownload } from "@/lib/download";
import { describeError, isApiError } from "@/lib/api/client";
import { useMe } from "@/lib/api/hooks/use-auth";
import { useClip, useClipDownload, useDeleteClip, useRenderClip, useUpdateClip } from "@/lib/api/hooks/use-clips";
import type { AspectRatio, ClipDetail, FramingMode, SubtitlePosition, SubtitleStyle, UpdateClipRequest } from "@/lib/api/types";
import { formatBytes, formatDateTime, formatDuration, formatTimecode, parseTimecode } from "@/lib/format";
import {
  categoryLabel,
  CLIP_STATUS_LABELS,
  CLIP_STATUS_TONES,
  FRAMING_LABELS,
  isActiveJobStatus,
  SUBTITLE_POSITION_LABELS,
  SUBTITLE_STYLE_LABELS,
} from "@/lib/labels";
import { clamp, cn, roundTo } from "@/lib/utils";

const MIN_CLIP_SECONDS = 1;

/** Aspect ratio of what the player shows: the render's own, from its size when it does not say. */
export function renderAspectRatio(
  render: { aspectRatio?: AspectRatio | null; width?: number | null; height?: number | null } | null | undefined,
): AspectRatio | null {
  if (!render) return null;
  if (render.aspectRatio) return render.aspectRatio;
  if (render.width && render.height) {
    const ratio = render.width / render.height;
    return ratio < 0.8 ? "9:16" : ratio > 1.25 ? "16:9" : "1:1";
  }
  return null;
}

interface EditorForm {
  title: string;
  start: number;
  end: number;
  aspectRatio: AspectRatio;
  subtitlesEnabled: boolean;
  subtitleStyle: SubtitleStyle;
  subtitlePosition: SubtitlePosition;
  framing: FramingMode;
}

function formFromClip(clip: ClipDetail): EditorForm {
  return {
    title: clip.title,
    start: clip.startSeconds,
    end: clip.endSeconds,
    aspectRatio: clip.aspectRatio,
    subtitlesEnabled: clip.subtitles?.enabled ?? true,
    subtitleStyle: clip.subtitles?.style ?? "bold",
    subtitlePosition: clip.subtitles?.position ?? "bottom",
    framing: clip.framing?.mode ?? "auto",
  };
}

function diffForm(form: EditorForm, clip: ClipDetail): UpdateClipRequest {
  const body: UpdateClipRequest = {};
  const base = formFromClip(clip);
  if (form.title.trim() !== base.title) body.title = form.title.trim();
  if (form.start !== base.start) body.startSeconds = roundTo(form.start, 3);
  if (form.end !== base.end) body.endSeconds = roundTo(form.end, 3);
  if (form.aspectRatio !== base.aspectRatio) body.aspectRatio = form.aspectRatio;
  if (
    form.subtitlesEnabled !== base.subtitlesEnabled ||
    form.subtitleStyle !== base.subtitleStyle ||
    form.subtitlePosition !== base.subtitlePosition
  ) {
    body.subtitles = { enabled: form.subtitlesEnabled, style: form.subtitleStyle, position: form.subtitlePosition };
  }
  if (form.framing !== base.framing) body.framing = { ...clip.framing, mode: form.framing };
  return body;
}

function TimecodeField({
  id,
  label,
  value,
  onCommit,
}: {
  id: string;
  label: string;
  value: number;
  onCommit: (seconds: number) => void;
}) {
  const [text, setText] = useState(() => formatTimecode(value));
  const [lastValue, setLastValue] = useState(value);
  const [invalid, setInvalid] = useState(false);

  // Keep the text in sync when the value changes from outside (slider, player buttons).
  if (value !== lastValue) {
    setLastValue(value);
    setText(formatTimecode(value));
    setInvalid(false);
  }

  const commit = () => {
    const parsed = parseTimecode(text);
    if (parsed === null) {
      setInvalid(true);
      return;
    }
    setInvalid(false);
    onCommit(parsed);
  };

  return (
    <div className="space-y-1">
      <Label htmlFor={id} className="text-xs text-fg-muted">
        {label}
      </Label>
      <Input
        id={id}
        value={text}
        inputMode="decimal"
        onChange={(event) => setText(event.target.value)}
        onBlur={commit}
        onKeyDown={(event) => {
          if (event.key === "Enter") {
            event.preventDefault();
            commit();
          }
        }}
        invalid={invalid}
        className="h-9 w-28 font-mono tabular-nums"
        aria-describedby={invalid ? `${id}-error` : undefined}
      />
      {invalid ? (
        <p id={`${id}-error`} className="text-xs text-danger">
          Use mm:ss.s
        </p>
      ) : null}
    </div>
  );
}

function ClipEditor({ clip }: { clip: ClipDetail }) {
  const router = useRouter();
  const me = useMe();
  const playerRef = useRef<VideoPlayerHandle | null>(null);
  const [form, setForm] = useState<EditorForm>(() => formFromClip(clip));
  const [needsRender, setNeedsRender] = useState(false);
  const [preferRender, setPreferRender] = useState(true);
  const [currentTime, setCurrentTime] = useState<number | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);

  const update = useUpdateClip(clip.id);
  const render = useRenderClip(clip.id);
  const remove = useDeleteClip();
  const download = useClipDownload();

  const sourceDuration = clip.source.durationSeconds > 0 ? clip.source.durationSeconds : Math.max(clip.endSeconds, form.end);
  const renderReady = clip.currentRender?.status === "ready" && !!clip.currentRender.streamUrl;
  const mode: "render" | "source" = preferRender && renderReady ? "render" : "source";
  const playerSrc = mode === "render" ? clip.currentRender?.streamUrl : clip.source.previewable ? clip.source.previewUrl : null;
  const toAbsolute = (seconds: number) => (mode === "render" ? clip.startSeconds + seconds : seconds);
  const dirty = Object.keys(diffForm(form, clip)).length > 0;
  const activeJob = clip.activeJob && isActiveJobStatus(clip.activeJob.status) ? clip.activeJob : null;
  const rendering = !!activeJob || clip.status === "rendering";
  const limits = me.data?.limits;
  const webcamAllowed = isFramingAllowed("webcam-split", limits);
  const customStylesAllowed = isSubtitleStyleAllowed("boxed", limits);
  // A clip can still carry a style/position its plan no longer includes (made before a downgrade). A
  // re-render keeps it, but the API saves any subtitle edit with the plan's default style and position.
  const subtitlesOutsidePlan =
    !isSubtitleStyleAllowed(form.subtitleStyle, limits) || !isSubtitlePositionAllowed(form.subtitlePosition, limits);
  const subtitleHints =
    [customStylesAllowed ? null : "clip-subtitle-lock", subtitlesOutsidePlan ? "clip-subtitle-current" : null].filter(Boolean).join(" ") ||
    undefined;
  const titleError = form.title.trim().length === 0 ? "Informe um título para o clipe." : null;

  const setRange = (start: number, end: number) => {
    const safeStart = clamp(roundTo(start, 1), 0, Math.max(0, sourceDuration - MIN_CLIP_SECONDS));
    const safeEnd = clamp(roundTo(end, 1), safeStart + MIN_CLIP_SECONDS, sourceDuration);
    setForm((current) => ({ ...current, start: safeStart, end: safeEnd }));
  };

  const setStartFromPlayer = () => {
    if (currentTime === null) return;
    setRange(currentTime, Math.max(form.end, currentTime + MIN_CLIP_SECONDS));
  };

  const setEndFromPlayer = () => {
    if (currentTime === null) return;
    setRange(Math.min(form.start, currentTime - MIN_CLIP_SECONDS), currentTime);
  };

  const playSelection = () => {
    const player = playerRef.current;
    if (!player) return;
    player.seek(mode === "render" ? 0 : form.start);
    player.play();
  };

  const save = () => {
    const body = diffForm(form, clip);
    if (Object.keys(body).length === 0) return;
    update.mutate(body, {
      onSuccess: (result) => {
        // Show what the API stored (it trims the title and applies the plan to the subtitles), so a
        // saved form is clean again instead of staying "dirty" with values that were not kept.
        setForm(formFromClip({ ...clip, ...result.clip }));
        setNeedsRender(result.needsRender);
        toast.success(result.needsRender ? "Alterações salvas. Renderize novamente para aplicá-las ao vídeo." : "Alterações salvas.");
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const startRender = () => {
    render.mutate(undefined, {
      onSuccess: () => {
        setNeedsRender(false);
        toast.success("Renderização iniciada");
      },
      onError: (error) => {
        toast.error(describeError(error));
      },
    });
  };

  const downloadVersion = (renderId?: string) => {
    download.mutate(
      { clipId: clip.id, renderId },
      { onSuccess: ({ url }) => openDownload(url), onError: (error) => toast.error(describeError(error)) },
    );
  };

  const confirmDelete = () => {
    remove.mutate(clip.id, {
      onSuccess: () => {
        toast.success("Clipe excluído");
        router.push("/library");
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  const versions = [...(clip.renders ?? [])].sort((a, b) => b.version - a.version);
  // Technical message of the failed render (worker/FFmpeg output, often in English): shown as a detail only.
  const renderError =
    (clip.currentRender as { error?: string | null } | null | undefined)?.error ??
    (versions[0]?.status === "failed" ? versions[0].error : null) ??
    null;

  return (
    <>
      <PageHeader
        backHref={`/projects/${clip.projectId}`}
        backLabel={clip.projectTitle ? `Projeto: ${clip.projectTitle}` : "Projeto"}
        title={clip.title}
        description={
          <span className="inline-flex flex-wrap items-center gap-2">
            <Badge tone={CLIP_STATUS_TONES[clip.status] ?? "neutral"} pulse={clip.status === "rendering"}>
              {CLIP_STATUS_LABELS[clip.status] ?? clip.status}
            </Badge>
            {clip.category ? <Badge tone="accent">{categoryLabel(clip.category)}</Badge> : null}
            {typeof clip.score === "number" ? <span className="text-fg-muted">relevância estimada {Math.round(clip.score)}</span> : null}
            <span className="text-fg-muted">
              {formatDuration(clip.startSeconds)} – {formatDuration(clip.endSeconds)} · {formatDuration(clip.durationSeconds)}
            </span>
          </span>
        }
        actions={
          <>
            {renderReady ? (
              <Button variant="secondary" onClick={() => downloadVersion()} loading={download.isPending} leftIcon={<Download aria-hidden="true" />}>
                Baixar
              </Button>
            ) : null}
            <Button variant="danger" onClick={() => setDeleteOpen(true)} leftIcon={<Trash2 aria-hidden="true" />}>
              Excluir
            </Button>
          </>
        }
      />

      {activeJob ? <JobStatus job={activeJob} compact className="mb-6" /> : null}
      {clip.status === "failed" && !activeJob ? (
        <Alert variant="danger" className="mb-6" title="A última renderização falhou">
          <p>O vídeo deste clipe não pôde ser gerado. Ajuste o corte, se quiser, e use “Renderizar novamente”.</p>
          {renderError ? <p className="mt-1 break-words font-mono text-xs text-fg-subtle">detalhe técnico: {renderError}</p> : null}
        </Alert>
      ) : null}

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">
        <div className="min-w-0 space-y-4">
          <VideoPlayer
            ref={playerRef}
            src={playerSrc}
            fragment={mode === "source" ? { start: clip.startSeconds, end: clip.endSeconds } : null}
            clampEnd={mode === "source" ? form.end : null}
            aspectRatio={mode === "render" ? (renderAspectRatio(clip.currentRender) ?? clip.aspectRatio) : "16:9"}
            onTimeUpdate={(seconds) => setCurrentTime(toAbsolute(seconds))}
            emptyMessage={
              renderReady
                ? "Pré-visualização indisponível."
                : "Ainda não há renderização pronta e a gravação original não está disponível para prévia."
            }
          />
          {renderMissingSubtitles(clip.subtitles?.enabled, clip.currentRender) ? (
            <p className="flex items-center gap-1.5 rounded-md border border-line bg-surface px-3 py-2 text-xs text-fg-muted">
              <Captions className="size-3.5 shrink-0 text-fg-subtle" aria-hidden="true" />
              {NO_SPEECH_SUBTITLES_TEXT}
            </p>
          ) : null}
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-fg-muted">
            <div className="flex items-center gap-2">
              {renderReady && clip.source.previewable ? (
                <div className="inline-flex rounded-md border border-line bg-surface p-0.5" role="group" aria-label="Fonte do player">
                  <button
                    type="button"
                    onClick={() => setPreferRender(true)}
                    className={cn("rounded-sm px-2.5 py-1", mode === "render" ? "bg-elevated text-fg" : "hover:text-fg")}
                    aria-pressed={mode === "render"}
                  >
                    Clipe renderizado
                  </button>
                  <button
                    type="button"
                    onClick={() => setPreferRender(false)}
                    className={cn("rounded-sm px-2.5 py-1", mode === "source" ? "bg-elevated text-fg" : "hover:text-fg")}
                    aria-pressed={mode === "source"}
                  >
                    Gravação original
                  </button>
                </div>
              ) : (
                <span>{mode === "render" ? "Reproduzindo o clipe renderizado" : "Reproduzindo a gravação original (prévia do corte)"}</span>
              )}
            </div>
            <span className="font-mono tabular-nums">tempo atual {currentTime === null ? "--:--.-" : formatTimecode(currentTime)}</span>
          </div>

          <Card>
            <CardHeader>
              <CardTitle>Corte</CardTitle>
              <CardDescription>Arraste os marcadores ou use o tempo atual do player. A seleção é limitada à duração da gravação.</CardDescription>
            </CardHeader>
            <CardContent className="space-y-5">
              <TrimSlider
                max={sourceDuration}
                value={[form.start, form.end]}
                onChange={([start, end]) => setRange(start, end)}
                currentTime={currentTime}
                minLength={MIN_CLIP_SECONDS}
                disabled={rendering}
              />
              <div className="flex flex-wrap items-end gap-4">
                <TimecodeField id="trim-start" label="Início (mm:ss.s)" value={form.start} onCommit={(seconds) => setRange(seconds, form.end)} />
                <TimecodeField id="trim-end" label="Fim (mm:ss.s)" value={form.end} onCommit={(seconds) => setRange(form.start, seconds)} />
                <div className="space-y-1">
                  <p className="text-xs text-fg-muted">Duração</p>
                  <p className="h-9 font-mono text-sm leading-9 tabular-nums">{formatTimecode(form.end - form.start)}</p>
                </div>
              </div>
              <div className="flex flex-wrap gap-2">
                <Button variant="secondary" size="sm" onClick={setStartFromPlayer} disabled={currentTime === null || rendering}>
                  Definir início no tempo atual do player
                </Button>
                <Button variant="secondary" size="sm" onClick={setEndFromPlayer} disabled={currentTime === null || rendering}>
                  Definir fim no tempo atual do player
                </Button>
                <Button variant="ghost" size="sm" onClick={playSelection} disabled={!playerSrc} leftIcon={<SkipBack aria-hidden="true" />}>
                  Reproduzir do início da seleção
                </Button>
              </div>
              {mode === "render" && dirty ? (
                <p className="text-xs text-fg-subtle">
                  O player mostra a versão renderizada; o corte editado só aparece após renderizar novamente (ou troque para a gravação
                  original).
                </p>
              ) : null}
            </CardContent>
          </Card>

          {clip.reasoning || clip.transcript ? (
            <Card>
              <CardHeader>
                <CardTitle>Por que este trecho</CardTitle>
              </CardHeader>
              <CardContent className="space-y-3 text-sm">
                {clip.reasoning ? <p className="text-fg">{clip.reasoning}</p> : null}
                {clip.transcript ? <p className="italic text-fg-muted">“{clip.transcript}”</p> : null}
              </CardContent>
            </Card>
          ) : null}
        </div>

        <div className="min-w-0 space-y-4">
          <Card>
            <CardHeader>
              <CardTitle>Detalhes</CardTitle>
            </CardHeader>
            <CardContent className="space-y-5">
              <div className="space-y-1.5">
                <Label htmlFor="clip-title">Título</Label>
                <Input
                  id="clip-title"
                  value={form.title}
                  onChange={(event) => setForm({ ...form, title: event.target.value })}
                  maxLength={120}
                  invalid={!!titleError}
                  aria-describedby={titleError ? "clip-title-error" : undefined}
                />
                {titleError ? (
                  <p id="clip-title-error" className="text-xs text-danger" role="alert">
                    {titleError}
                  </p>
                ) : null}
              </div>
              <div className="space-y-2">
                <p className="text-sm font-medium">Formato</p>
                <ChoiceGroup<AspectRatio>
                  label="Formato"
                  value={form.aspectRatio}
                  onChange={(aspectRatio) => setForm({ ...form, aspectRatio })}
                  size="sm"
                  options={(["9:16", "16:9", "1:1"] as AspectRatio[]).map((ratio) => ({
                    value: ratio,
                    label: ratio,
                    icon: <AspectRatioIcon ratio={ratio} className="size-5" />,
                  }))}
                />
              </div>
              <div className="space-y-1.5">
                <Label htmlFor="clip-framing">Enquadramento</Label>
                <NativeSelect
                  id="clip-framing"
                  value={form.framing}
                  aria-describedby={webcamAllowed ? undefined : "clip-framing-lock"}
                  onChange={(event) => setForm({ ...form, framing: event.target.value as FramingMode })}
                >
                  {(Object.keys(FRAMING_LABELS) as FramingMode[]).map((framingMode) => (
                    <option key={framingMode} value={framingMode} disabled={!isFramingAllowed(framingMode, limits)}>
                      {FRAMING_LABELS[framingMode]}
                      {isFramingAllowed(framingMode, limits) ? "" : PLAN_LOCKED_SUFFIX}
                    </option>
                  ))}
                </NativeSelect>
                {!webcamAllowed ? <WebcamLockNote id="clip-framing-lock" /> : null}
              </div>
              <div className="space-y-3 border-t border-line pt-4">
                <div className="flex items-center justify-between gap-3">
                  <Label htmlFor="clip-subtitles">Legendas</Label>
                  <Switch
                    id="clip-subtitles"
                    checked={form.subtitlesEnabled}
                    onCheckedChange={(subtitlesEnabled) => setForm({ ...form, subtitlesEnabled })}
                  />
                </div>
                <div className="grid grid-cols-2 gap-3">
                  <div className="space-y-1.5">
                    <Label htmlFor="clip-subtitle-style">Estilo</Label>
                    <NativeSelect
                      id="clip-subtitle-style"
                      value={form.subtitleStyle}
                      disabled={!form.subtitlesEnabled}
                      aria-describedby={subtitleHints}
                      onChange={(event) => setForm({ ...form, subtitleStyle: event.target.value as SubtitleStyle })}
                    >
                      {(Object.keys(SUBTITLE_STYLE_LABELS) as SubtitleStyle[]).map((style) => (
                        <option key={style} value={style} disabled={!isSubtitleStyleAllowed(style, limits)}>
                          {SUBTITLE_STYLE_LABELS[style]}
                          {isSubtitleStyleAllowed(style, limits) ? "" : PLAN_LOCKED_SUFFIX}
                        </option>
                      ))}
                    </NativeSelect>
                  </div>
                  <div className="space-y-1.5">
                    <Label htmlFor="clip-subtitle-position">Posição</Label>
                    <NativeSelect
                      id="clip-subtitle-position"
                      value={form.subtitlePosition}
                      disabled={!form.subtitlesEnabled}
                      aria-describedby={subtitleHints}
                      onChange={(event) => setForm({ ...form, subtitlePosition: event.target.value as SubtitlePosition })}
                    >
                      {(Object.keys(SUBTITLE_POSITION_LABELS) as SubtitlePosition[]).map((position) => (
                        <option key={position} value={position} disabled={!isSubtitlePositionAllowed(position, limits)}>
                          {SUBTITLE_POSITION_LABELS[position]}
                          {isSubtitlePositionAllowed(position, limits) ? "" : PLAN_LOCKED_SUFFIX}
                        </option>
                      ))}
                    </NativeSelect>
                  </div>
                </div>
                {!customStylesAllowed ? <SubtitleLockNote id="clip-subtitle-lock" /> : null}
                {subtitlesOutsidePlan ? (
                  <p id="clip-subtitle-current" className="text-xs text-warning">
                    As legendas atuais deste clipe ({SUBTITLE_STYLE_LABELS[form.subtitleStyle]},{" "}
                    {SUBTITLE_POSITION_LABELS[form.subtitlePosition].toLowerCase()}) não estão incluídas no seu plano. Uma nova renderização
                    as mantém, mas, se você alterar as legendas, elas passam a usar o estilo {SUBTITLE_STYLE_LABELS[DEFAULT_SUBTITLE_STYLE]},{" "}
                    {SUBTITLE_POSITION_LABELS[DEFAULT_SUBTITLE_POSITION].toLowerCase()}.
                  </p>
                ) : null}
                <SubtitlePreview style={form.subtitleStyle} position={form.subtitlePosition} enabled={form.subtitlesEnabled} />
              </div>

              {needsRender && !dirty ? (
                <Alert variant="info" title="Renderização pendente">
                  As alterações foram salvas, mas o vídeo atual ainda reflete a versão anterior. Clique em “Renderizar novamente”.
                </Alert>
              ) : null}

              <div className="flex flex-col gap-2 border-t border-line pt-4 sm:flex-row">
                <Button
                  className="flex-1"
                  onClick={save}
                  disabled={!dirty || rendering || !!titleError}
                  loading={update.isPending}
                  leftIcon={<Save aria-hidden="true" />}
                >
                  Salvar alterações
                </Button>
                <Button
                  className="flex-1"
                  variant={needsRender ? "primary" : "secondary"}
                  onClick={startRender}
                  disabled={rendering || dirty}
                  loading={render.isPending}
                  leftIcon={<RefreshCw aria-hidden="true" />}
                >
                  Renderizar novamente
                </Button>
              </div>
              {dirty ? <p className="text-xs text-fg-subtle">Salve as alterações antes de renderizar.</p> : null}
            </CardContent>
          </Card>

          <Card>
            <CardHeader>
              <CardTitle>Versões</CardTitle>
              <CardDescription>Cada renderização gera uma nova versão. A versão atual é a usada em downloads.</CardDescription>
            </CardHeader>
            <CardContent>
              {versions.length === 0 ? (
                <p className="text-sm text-fg-muted">Nenhuma versão renderizada ainda.</p>
              ) : (
                <ul className="divide-y divide-line">
                  {versions.map((version) => {
                    const current = version.id === clip.currentRender?.id;
                    return (
                      <li key={version.id} className="flex flex-wrap items-center justify-between gap-2 py-2.5 text-sm">
                        <div className="min-w-0">
                          <p className="flex items-center gap-2 font-medium">
                            <Film className="size-4 text-fg-subtle" aria-hidden="true" />v{version.version}
                            {current ? <Badge tone="accent">atual</Badge> : null}
                            <Badge tone={CLIP_STATUS_TONES[version.status] ?? "neutral"} pulse={version.status === "rendering"}>
                              {CLIP_STATUS_LABELS[version.status] ?? version.status}
                            </Badge>
                          </p>
                          <p className="mt-0.5 text-xs text-fg-subtle">
                            {version.width && version.height ? `${version.width}×${version.height}` : "—"}
                            {version.sizeBytes ? ` · ${formatBytes(version.sizeBytes)}` : ""}
                            {version.createdAt ? ` · ${formatDateTime(version.createdAt)}` : ""}
                            {version.hasSubtitles ? " · com legendas" : form.subtitlesEnabled && version.status === "ready" && version.hasSubtitles === false ? " · sem legendas (nenhuma fala detectada)" : ""}
                          </p>
                          {version.error ? (
                            <p className="mt-0.5 break-words font-mono text-[11px] text-fg-subtle">detalhe técnico: {version.error}</p>
                          ) : null}
                        </div>
                        {version.status === "ready" ? (
                          <Button variant="ghost" size="sm" onClick={() => downloadVersion(version.id)} leftIcon={<Download aria-hidden="true" />}>
                            Baixar
                          </Button>
                        ) : null}
                      </li>
                    );
                  })}
                </ul>
              )}
            </CardContent>
          </Card>
        </div>
      </div>

      <ConfirmDialog
        open={deleteOpen}
        onOpenChange={setDeleteOpen}
        title="Excluir clipe?"
        description="Todas as versões renderizadas serão removidas. O candidato original continua disponível no projeto."
        confirmLabel="Excluir"
        destructive
        loading={remove.isPending}
        onConfirm={confirmDelete}
      />
    </>
  );
}

export function ClipEditorPage() {
  const params = useParams<{ id: string }>();
  const clip = useClip(params.id);

  if (clip.isError) {
    const notFound = isApiError(clip.error) && clip.error.status === 404;
    return (
      <>
        <PageHeader title={notFound ? "Clipe não encontrado" : "Erro ao carregar o clipe"} backHref="/library" backLabel="Biblioteca" />
        <Alert
          variant={notFound ? "warning" : "danger"}
          action={
            notFound ? undefined : (
              <Button size="sm" variant="secondary" onClick={() => clip.refetch()} loading={clip.isFetching}>
                Tentar novamente
              </Button>
            )
          }
        >
          {notFound ? "Este clipe não existe ou foi excluído." : describeError(clip.error)}
        </Alert>
      </>
    );
  }

  if (clip.isPending || !clip.data) {
    return (
      <div className="space-y-6" aria-busy="true">
        <Skeleton className="h-8 w-72" />
        <div className="grid gap-6 lg:grid-cols-[1.4fr_1fr]">
          <Skeleton className="aspect-video w-full" />
          <CardSkeleton />
        </div>
      </div>
    );
  }

  return <ClipEditor key={clip.data.id} clip={clip.data} />;
}
