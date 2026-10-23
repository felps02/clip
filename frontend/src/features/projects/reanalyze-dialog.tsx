"use client";

import { useState } from "react";
import { toast } from "sonner";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { AssistantPanel } from "@/features/assistant/assistant-panel";
import { adjustForShortSource, applyPlanLimits, isShortSource, trimSettingsForSubmit } from "@/features/projects/analysis-settings";
import { AnalysisSettingsForm } from "@/features/projects/analysis-settings-form";
import { describeError } from "@/lib/api/client";
import { useCreateJob } from "@/lib/api/hooks/use-projects";
import type { AnalysisSettings, AssistantChatContext, PlanLimits } from "@/lib/api/types";
import { formatSeconds } from "@/lib/format";

export interface ReanalyzeDialogProps {
  projectId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  initialSettings: AnalysisSettings;
  limits?: PlanLimits | null;
  /** What the assistant knows about the recording (file name, duration). */
  context?: AssistantChatContext;
  /** Duration of the recording: one shorter than the clip minimum gets its clip lengths fitted into it. */
  sourceDurationSeconds?: number | null;
}

function ReanalyzeForm({
  projectId,
  onOpenChange,
  initialSettings,
  limits,
  context,
  sourceDurationSeconds,
}: Omit<ReanalyzeDialogProps, "open">) {
  // A recording shorter than the clip minimum found nothing: start from lengths that fit inside it.
  const [shortSource] = useState(() =>
    isShortSource(sourceDurationSeconds, initialSettings.minClipSeconds)
      ? { durationSeconds: sourceDurationSeconds, previousMin: initialSettings.minClipSeconds, previousMax: initialSettings.maxClipSeconds }
      : null,
  );
  const options = { shortSourceSeconds: shortSource?.durationSeconds ?? null };
  const normalize = (next: AnalysisSettings) => applyPlanLimits(next, limits, options);
  const [settings, setSettings] = useState<AnalysisSettings>(() =>
    shortSource ? applyPlanLimits(adjustForShortSource(initialSettings, shortSource.durationSeconds), limits, options) : initialSettings,
  );
  const createJob = useCreateJob(projectId);

  const submit = () => {
    createJob.mutate(trimSettingsForSubmit(settings), {
      onSuccess: () => {
        toast.success("Nova análise na fila");
        onOpenChange(false);
      },
      onError: (error) => toast.error(describeError(error)),
    });
  };

  return (
    <>
      <DialogHeader>
        <DialogTitle>Analisar novamente</DialogTitle>
        <DialogDescription>
          Uma nova análise substitui a lista de momentos encontrados. Os clipes já gerados são mantidos. O tempo da gravação conta
          novamente na cota do período.
        </DialogDescription>
      </DialogHeader>
      {/* Same assistant as on "Novo projeto", starting from this project's settings. */}
      <AssistantPanel
        settings={settings}
        onSettingsChange={setSettings}
        normalize={normalize}
        limits={limits}
        context={context}
        disabled={createJob.isPending}
        className="shadow-none"
      />
      <AnalysisSettingsForm
        value={settings}
        onChange={(next) => setSettings(normalize(next))}
        limits={limits}
        idPrefix="reanalyze"
        shortSourceSeconds={shortSource?.durationSeconds ?? null}
        clipLengthNote={
          shortSource ? (
            <Alert variant="info">
              O vídeo tem {formatSeconds(shortSource.durationSeconds)}, menos que a duração mínima anterior ({shortSource.previousMin} s): a faixa foi
              ajustada para clipes de {settings.minClipSeconds} a {settings.maxClipSeconds} s.
            </Alert>
          ) : null
        }
      />
      <DialogFooter>
        <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={createJob.isPending}>
          Cancelar
        </Button>
        <Button onClick={submit} loading={createJob.isPending}>
          Iniciar análise
        </Button>
      </DialogFooter>
    </>
  );
}

export function ReanalyzeDialog(props: ReanalyzeDialogProps) {
  return (
    <Dialog open={props.open} onOpenChange={props.onOpenChange}>
      <DialogContent size="lg">{props.open ? <ReanalyzeForm {...props} /> : null}</DialogContent>
    </Dialog>
  );
}
