"use client";

import { AlertTriangle, Coins, FileVideo, Loader2, UploadCloud, X } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useId, useRef, useState, type DragEvent } from "react";
import { toast } from "sonner";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Progress } from "@/components/ui/progress";
import { AssistantPanel } from "@/features/assistant/assistant-panel";
import { trimSettingsForSubmit } from "@/features/projects/analysis-settings";
import { AnalysisSettingsForm } from "@/features/projects/analysis-settings-form";
import { uploadStepLabel, useUploadFlow } from "@/features/new-project/use-upload-flow";
import { useUsage } from "@/lib/api/hooks/use-account";
import type { AnalysisSettings, PlanLimits, UsageResponse } from "@/lib/api/types";
import { formatBytes, formatDuration, formatEta, formatNumber, formatSeconds, formatSpeed } from "@/lib/format";
import { ACCEPT_ATTRIBUTE, ACCEPTED_EXTENSIONS, isAcceptedFile, readMediaDuration } from "@/lib/media";
import { cn } from "@/lib/utils";

interface SelectedFile {
  file: File;
  duration: number | null;
  durationStatus: "reading" | "ready" | "unknown";
}

/** The recording chosen on the upload tab, once its duration was read (or could not be). */
export interface SelectedSource {
  fileName: string;
  durationSeconds: number | null;
}

/** The clip lengths were fitted into a recording shorter than the clip minimum. */
export interface ShortSourceAdjustment {
  durationSeconds: number;
  previous: Pick<AnalysisSettings, "minClipSeconds" | "maxClipSeconds" | "targetClipSeconds">;
}

export interface UploadTabProps {
  settings: AnalysisSettings;
  onSettingsChange: (next: AnalysisSettings) => void;
  /** Plan caps (and short-recording bounds) applied to changes made by the assistant panel. */
  normalize?: (settings: AnalysisSettings) => AnalysisSettings;
  limits?: PlanLimits | null;
  shortSource?: ShortSourceAdjustment | null;
  /** Called when a recording's duration is known and when the recording is removed (`null`). */
  onSourceChange?: (source: SelectedSource | null) => void;
}

/** Processing minutes the recording will use, before the upload starts (the API still enforces the quota). */
export function estimateProcessing(durationSeconds: number, usage: UsageResponse | undefined) {
  const estimateMinutes = Math.max(1, Math.ceil(durationSeconds / 60));
  if (!usage) return { estimateMinutes, remainingSeconds: undefined as number | null | undefined, exceedsRemaining: false };
  const reported = usage.remaining?.processingSeconds;
  const remainingSeconds =
    reported === null
      ? null
      : typeof reported === "number"
        ? Math.max(0, reported)
        : Math.max(0, usage.plan.limits.monthlyProcessingMinutes * 60 - usage.used.processingSeconds);
  return { estimateMinutes, remainingSeconds, exceedsRemaining: remainingSeconds !== null && durationSeconds > remainingSeconds };
}

function ConsumptionEstimate({
  durationSeconds,
  usage,
  usageFailed,
  durationTooLong,
  maxDurationMinutes,
}: {
  durationSeconds: number;
  usage: UsageResponse | undefined;
  usageFailed: boolean;
  durationTooLong: boolean;
  maxDurationMinutes?: number;
}) {
  const { estimateMinutes, remainingSeconds, exceedsRemaining } = estimateProcessing(durationSeconds, usage);
  const remainingText =
    remainingSeconds === null
      ? " (sem limite no período)"
      : typeof remainingSeconds === "number"
        ? ` (restam ${formatNumber(Math.floor(remainingSeconds / 60))} min no período)`
        : usageFailed
          ? ""
          : " (consultando o saldo do período…)";
  return (
    <div className="space-y-2 rounded-md border border-line bg-canvas/40 px-3 py-2.5 text-sm" aria-live="polite">
      <p className="flex items-start gap-2">
        <Coins className="mt-0.5 size-4 shrink-0 text-fg-subtle" aria-hidden="true" />
        <span>
          Consumo estimado: <strong className="font-semibold">{formatNumber(estimateMinutes)} min</strong> de processamento
          <span className="text-fg-muted">{remainingText}</span>
        </span>
      </p>
      {exceedsRemaining ? (
        <p className="flex items-start gap-1.5 text-xs text-warning">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          <span>
            O consumo estimado passa do que resta no período: o servidor vai recusar o processamento.{" "}
            <Link href="/billing" className="text-accent underline-offset-4 hover:underline">
              Ver planos
            </Link>
          </span>
        </p>
      ) : null}
      {durationTooLong ? (
        <p className="flex items-start gap-1.5 text-xs text-warning">
          <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
          A duração excede o limite do seu plano ({maxDurationMinutes} min por vídeo). O processamento pode ser recusado.
        </p>
      ) : null}
    </div>
  );
}

export function UploadTab({ settings, onSettingsChange, normalize, limits, shortSource, onSourceChange }: UploadTabProps) {
  const router = useRouter();
  const inputId = useId();
  const inputRef = useRef<HTMLInputElement | null>(null);
  const [selected, setSelected] = useState<SelectedFile | null>(null);
  const [title, setTitle] = useState("");
  const [dragging, setDragging] = useState(false);
  const [fileError, setFileError] = useState<string | null>(null);
  const flow = useUploadFlow();
  const usage = useUsage();
  // Latest callback for the async duration read and the unmount cleanup.
  const onSourceChangeRef = useRef(onSourceChange);
  useEffect(() => {
    onSourceChangeRef.current = onSourceChange;
  }, [onSourceChange]);
  // Leaving the tab drops the file: give the clip lengths of a short recording back.
  useEffect(() => () => onSourceChangeRef.current?.(null), []);

  const maxBytes = limits ? limits.maxFileSizeMb * 1024 * 1024 : null;
  const maxDurationSeconds = limits ? limits.maxSourceDurationMinutes * 60 : null;
  // File whose duration is being read; cleared when the file is removed.
  const pendingFile = useRef<File | null>(null);

  const pickFile = (file: File | undefined) => {
    if (!file) return;
    setFileError(null);
    if (!isAcceptedFile(file)) {
      setFileError(`Formato não suportado. Envie um arquivo ${ACCEPTED_EXTENSIONS.map((ext) => `.${ext}`).join(", ")}.`);
      return;
    }
    if (maxBytes && file.size > maxBytes) {
      setFileError(
        `O arquivo tem ${formatBytes(file.size)} e o seu plano aceita até ${formatBytes(maxBytes)}. Compacte a gravação ou faça upgrade.`,
      );
      return;
    }
    flow.reset();
    setSelected({ file, duration: null, durationStatus: "reading" });
    pendingFile.current = file;
    void readMediaDuration(file).then((duration) => {
      // A newer pick (or a removal) replaced this file meanwhile: its duration no longer matters.
      if (pendingFile.current !== file) return;
      setSelected({ file, duration, durationStatus: duration === null ? "unknown" : "ready" });
      onSourceChangeRef.current?.({ fileName: file.name, durationSeconds: duration });
    });
  };

  const onDrop = (event: DragEvent<HTMLDivElement>) => {
    event.preventDefault();
    setDragging(false);
    if (flow.busy) return;
    pickFile(event.dataTransfer.files?.[0]);
  };

  const clearFile = () => {
    flow.reset();
    setSelected(null);
    setFileError(null);
    pendingFile.current = null;
    if (inputRef.current) inputRef.current.value = "";
    onSourceChange?.(null);
  };

  const start = async () => {
    if (!selected) return;
    const projectId = await flow.submit({
      file: selected.file,
      settings: trimSettingsForSubmit(settings),
      title,
      declaredDurationSeconds: selected.duration,
    });
    if (projectId) {
      toast.success("Projeto criado. A análise começou.");
      router.push(`/projects/${projectId}`);
    }
  };

  const retry = async () => {
    const projectId = flow.canRetryProject ? await flow.retryProject() : null;
    if (projectId) {
      router.push(`/projects/${projectId}`);
      return;
    }
    if (!flow.canRetryProject) await start();
  };

  const durationTooLong =
    selected?.duration !== null && selected?.duration !== undefined && maxDurationSeconds !== null && selected.duration > maxDurationSeconds;

  const phase = flow.phase;

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]">
      <div className="min-w-0 space-y-5">
        <Card>
          <CardHeader>
            <CardTitle>Gravação</CardTitle>
            <CardDescription>
              MP4, MOV, MKV, WebM, MP3 ou WAV
              {limits ? ` · até ${formatBytes(maxBytes)} e ${limits.maxSourceDurationMinutes} min por arquivo` : ""}.
            </CardDescription>
          </CardHeader>
          <CardContent className="space-y-4">
            {!selected ? (
              <div
                onDragOver={(event) => {
                  event.preventDefault();
                  if (!dragging) setDragging(true);
                }}
                onDragLeave={() => setDragging(false)}
                onDrop={onDrop}
                className={cn(
                  "flex flex-col items-center justify-center rounded-lg border-2 border-dashed px-6 py-12 text-center transition-colors duration-150",
                  dragging ? "border-accent bg-accent/5" : "border-line-strong bg-canvas/40 hover:border-fg-subtle",
                )}
              >
                <div className="flex size-12 items-center justify-center rounded-full bg-elevated text-accent">
                  <UploadCloud className="size-6" aria-hidden="true" />
                </div>
                <p className="mt-4 text-sm font-medium">Arraste a gravação para cá</p>
                <p className="mt-1 text-xs text-fg-muted">ou</p>
                <Label htmlFor={inputId} className="sr-only">
                  Escolher arquivo
                </Label>
                <input
                  ref={inputRef}
                  id={inputId}
                  type="file"
                  accept={ACCEPT_ATTRIBUTE}
                  className="sr-only"
                  onChange={(event) => pickFile(event.target.files?.[0])}
                />
                <Button variant="secondary" className="mt-3" onClick={() => inputRef.current?.click()}>
                  Escolher arquivo
                </Button>
              </div>
            ) : (
              <div className="flex items-start gap-3 rounded-lg border border-line bg-canvas/40 p-4">
                <div className="flex size-11 shrink-0 items-center justify-center rounded-md bg-elevated text-fg-muted">
                  <FileVideo className="size-5" aria-hidden="true" />
                </div>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium" title={selected.file.name}>
                    {selected.file.name}
                  </p>
                  <p className="mt-0.5 text-xs text-fg-muted">
                    {formatBytes(selected.file.size)}
                    {" · "}
                    {selected.durationStatus === "reading" ? (
                      <span className="inline-flex items-center gap-1">
                        <Loader2 className="size-3 animate-spin" aria-hidden="true" /> lendo duração…
                      </span>
                    ) : selected.durationStatus === "ready" ? (
                      `duração ${formatDuration(selected.duration)}`
                    ) : (
                      "duração será medida no servidor"
                    )}
                  </p>
                  {shortSource ? (
                    <p className="mt-2 flex items-start gap-1.5 text-xs text-warning" role="status">
                      <AlertTriangle className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
                      <span>
                        O vídeo ({formatSeconds(shortSource.durationSeconds)}) é mais curto que a duração mínima dos clipes (
                        {shortSource.previous.minClipSeconds} s). A duração dos clipes foi ajustada automaticamente para caber nele.
                      </span>
                    </p>
                  ) : null}
                </div>
                {!flow.busy ? (
                  <Button variant="ghost" size="icon-sm" onClick={clearFile} aria-label="Remover arquivo">
                    <X aria-hidden="true" />
                  </Button>
                ) : null}
              </div>
            )}
            {selected && selected.durationStatus === "ready" && selected.duration ? (
              <ConsumptionEstimate
                durationSeconds={selected.duration}
                usage={usage.data}
                usageFailed={usage.isError}
                durationTooLong={durationTooLong}
                maxDurationMinutes={limits?.maxSourceDurationMinutes}
              />
            ) : null}
            {fileError ? <Alert variant="danger">{fileError}</Alert> : null}

            <div className="space-y-1.5">
              <Label htmlFor="project-title">Título do projeto (opcional)</Label>
              <Input
                id="project-title"
                value={title}
                onChange={(event) => setTitle(event.target.value)}
                placeholder="Ex.: Live de sábado – ranqueada"
                maxLength={120}
                disabled={flow.busy}
              />
            </div>
          </CardContent>
        </Card>

        {phase.kind !== "idle" ? (
          <Card aria-live="polite">
            <CardContent className="space-y-3 pt-5">
              {phase.kind === "uploading" ? (
                <>
                  <div className="flex items-center justify-between text-sm">
                    <span className="font-medium">Enviando o arquivo</span>
                    <span className="font-mono tabular-nums">{Math.floor(phase.progress.percent)}%</span>
                  </div>
                  <Progress value={phase.progress.percent} label="Progresso do envio" />
                  <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-fg-muted">
                    <span>
                      {formatBytes(phase.progress.loadedBytes)} de {formatBytes(phase.progress.totalBytes)} ·{" "}
                      {formatSpeed(phase.progress.bytesPerSecond)} · restam {formatEta(phase.progress.etaSeconds)}
                    </span>
                    <Button variant="danger" size="sm" onClick={flow.cancel}>
                      Cancelar envio
                    </Button>
                  </div>
                </>
              ) : null}
              {phase.kind === "creating" || phase.kind === "completing" || phase.kind === "creating-project" ? (
                <div className="flex items-center gap-3 text-sm">
                  <Loader2 className="size-4 animate-spin text-accent" aria-hidden="true" />
                  <span>
                    {phase.kind === "creating"
                      ? uploadStepLabel("create")
                      : phase.kind === "completing"
                        ? uploadStepLabel("complete")
                        : uploadStepLabel("project")}
                    …
                  </span>
                </div>
              ) : null}
              {phase.kind === "done" ? (
                <Alert variant="success" title="Projeto criado">
                  Redirecionando para o acompanhamento da análise…{" "}
                  <Link href={`/projects/${phase.projectId}`} className="text-accent underline-offset-4 hover:underline">
                    Abrir projeto
                  </Link>
                </Alert>
              ) : null}
              {phase.kind === "error" ? (
                <Alert
                  variant="danger"
                  title={`Falha ao ${uploadStepLabel(phase.step).toLowerCase()}`}
                  action={
                    phase.retryable ? (
                      <Button size="sm" variant="secondary" onClick={retry}>
                        Tentar novamente
                      </Button>
                    ) : undefined
                  }
                >
                  {phase.message}
                  {phase.code === "plan_limit" || phase.code === "quota_exceeded" ? (
                    <>
                      {" "}
                      <Link href="/billing" className="text-accent underline-offset-4 hover:underline">
                        Ver planos
                      </Link>
                    </>
                  ) : null}
                </Alert>
              ) : null}
              {flow.warnings.length > 0 ? (
                <Alert variant="warning" title="Avisos do servidor">
                  <ul className="list-disc space-y-0.5 pl-4">
                    {flow.warnings.map((warning) => (
                      <li key={warning}>{warning}</li>
                    ))}
                  </ul>
                </Alert>
              ) : null}
            </CardContent>
          </Card>
        ) : null}

        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-xs text-fg-subtle">
            Ao enviar, você confirma que tem o direito de processar este conteúdo.
          </p>
          <Button size="lg" onClick={start} disabled={!selected || flow.busy || selected.durationStatus === "reading"} loading={flow.busy}>
            Enviar e analisar
          </Button>
        </div>

        <AssistantPanel
          settings={settings}
          onSettingsChange={onSettingsChange}
          normalize={normalize}
          limits={limits}
          disabled={flow.busy}
          context={
            selected
              ? {
                  fileName: selected.file.name,
                  ...(selected.duration ? { durationSeconds: Math.round(selected.duration) } : {}),
                }
              : undefined
          }
        />
      </div>

      <Card className="h-fit min-w-0">
        <CardHeader>
          <CardTitle>Configurações da análise</CardTitle>
          <CardDescription>Você pode reanalisar com outras configurações depois.</CardDescription>
        </CardHeader>
        <CardContent>
          <AnalysisSettingsForm
            value={settings}
            onChange={onSettingsChange}
            limits={limits}
            disabled={flow.busy}
            idPrefix="upload"
            shortSourceSeconds={shortSource?.durationSeconds ?? null}
            clipLengthNote={
              shortSource ? (
                <Alert variant="info">
                  Ajustada para o vídeo de {formatSeconds(shortSource.durationSeconds)}: clipes de {settings.minClipSeconds} a{" "}
                  {settings.maxClipSeconds} s (antes: {shortSource.previous.minClipSeconds} a {shortSource.previous.maxClipSeconds} s). A faixa
                  anterior volta se você trocar de arquivo.
                </Alert>
              ) : null
            }
          />
        </CardContent>
      </Card>
    </div>
  );
}
