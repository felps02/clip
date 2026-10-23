"use client";

import Link from "next/link";
import { useState } from "react";
import { toast } from "sonner";
import { Button, buttonVariants } from "@/components/ui/button";
import { ChoiceGroup } from "@/components/ui/choice-group";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Switch } from "@/components/ui/switch";
import { isFramingAllowed, isSubtitlePositionAllowed, isSubtitleStyleAllowed } from "@/features/projects/analysis-settings";
import { AspectRatioIcon, PLAN_LOCKED_SUFFIX, SubtitleLockNote, WebcamLockNote } from "@/features/projects/analysis-settings-form";
import { SubtitlePreview } from "@/features/projects/subtitle-preview";
import { describeError } from "@/lib/api/client";
import { useCreateClipFromCandidate } from "@/lib/api/hooks/use-projects";
import type {
  AnalysisSettings,
  AspectRatio,
  Candidate,
  FramingMode,
  PlanLimits,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";
import { formatDuration } from "@/lib/format";
import { categoryLabel, FRAMING_LABELS, SUBTITLE_POSITION_LABELS, SUBTITLE_STYLE_LABELS } from "@/lib/labels";

export interface GenerateClipDialogProps {
  projectId: string;
  projectTitle: string;
  candidate: Candidate | null;
  onOpenChange: (open: boolean) => void;
  defaults: AnalysisSettings;
  limits?: PlanLimits | null;
}

interface ClipForm {
  title: string;
  aspectRatio: AspectRatio;
  subtitlesEnabled: boolean;
  subtitleStyle: SubtitleStyle;
  subtitlePosition: SubtitlePosition;
  framing: FramingMode;
}

function initialForm(candidate: Candidate, projectTitle: string, defaults: AnalysisSettings): ClipForm {
  return {
    title: `${projectTitle} – ${categoryLabel(candidate.category)} ${formatDuration(candidate.startSeconds)}`,
    aspectRatio: defaults.aspectRatio,
    subtitlesEnabled: defaults.subtitles.enabled,
    subtitleStyle: defaults.subtitles.style,
    subtitlePosition: defaults.subtitles.position,
    framing: defaults.framing.mode,
  };
}

function GenerateClipForm({ projectId, projectTitle, candidate, onOpenChange, defaults, limits }: GenerateClipDialogProps & { candidate: Candidate }) {
  const [form, setForm] = useState<ClipForm>(() => initialForm(candidate, projectTitle, defaults));
  const [createdClipId, setCreatedClipId] = useState<string | null>(null);
  const create = useCreateClipFromCandidate(projectId);
  const webcamAllowed = isFramingAllowed("webcam-split", limits);
  const customStylesAllowed = isSubtitleStyleAllowed("boxed", limits);

  const submit = () => {
    create.mutate(
      {
        candidateId: candidate.id,
        body: {
          title: form.title.trim() || undefined,
          aspectRatio: form.aspectRatio,
          subtitles: { enabled: form.subtitlesEnabled, style: form.subtitleStyle, position: form.subtitlePosition },
          framing: { mode: form.framing },
        },
      },
      {
        onSuccess: (result) => {
          setCreatedClipId(result.clip.id);
          toast.success("Clipe criado. A renderização começou.");
        },
        onError: (error) => toast.error(describeError(error)),
      },
    );
  };

  if (createdClipId) {
    return (
      <>
        <DialogHeader>
          <DialogTitle>Clipe em renderização</DialogTitle>
          <DialogDescription>
            O clipe foi criado a partir do candidato #{candidate.rank}. Acompanhe a renderização e ajuste o corte no editor.
          </DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)}>
            Fechar
          </Button>
          <Link href={`/clips/${createdClipId}`} className={buttonVariants()}>
            Abrir clipe
          </Link>
        </DialogFooter>
      </>
    );
  }

  return (
    <>
      <DialogHeader>
        <DialogTitle>Gerar clipe</DialogTitle>
        <DialogDescription>
          Candidato #{candidate.rank} · {formatDuration(candidate.startSeconds)} – {formatDuration(candidate.endSeconds)} (
          {formatDuration(candidate.durationSeconds)})
        </DialogDescription>
      </DialogHeader>
      <div className="space-y-5">
        <div className="space-y-1.5">
          <Label htmlFor="clip-title">Título</Label>
          <Input id="clip-title" value={form.title} onChange={(event) => setForm({ ...form, title: event.target.value })} maxLength={120} />
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
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="space-y-3">
            <div className="flex items-center justify-between gap-3">
              <Label htmlFor="clip-subtitles">Legendas</Label>
              <Switch
                id="clip-subtitles"
                checked={form.subtitlesEnabled}
                onCheckedChange={(subtitlesEnabled) => setForm({ ...form, subtitlesEnabled })}
              />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="clip-subtitle-style">Estilo</Label>
              <NativeSelect
                id="clip-subtitle-style"
                value={form.subtitleStyle}
                disabled={!form.subtitlesEnabled}
                aria-describedby={customStylesAllowed ? undefined : "clip-subtitle-lock"}
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
                aria-describedby={customStylesAllowed ? undefined : "clip-subtitle-lock"}
                onChange={(event) => setForm({ ...form, subtitlePosition: event.target.value as SubtitlePosition })}
              >
                {(Object.keys(SUBTITLE_POSITION_LABELS) as SubtitlePosition[]).map((position) => (
                  <option key={position} value={position} disabled={!isSubtitlePositionAllowed(position, limits)}>
                    {SUBTITLE_POSITION_LABELS[position]}
                    {isSubtitlePositionAllowed(position, limits) ? "" : PLAN_LOCKED_SUFFIX}
                  </option>
                ))}
              </NativeSelect>
              {!customStylesAllowed ? <SubtitleLockNote id="clip-subtitle-lock" /> : null}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="clip-framing">Enquadramento</Label>
              <NativeSelect
                id="clip-framing"
                value={form.framing}
                aria-describedby={webcamAllowed ? undefined : "clip-framing-lock"}
                onChange={(event) => setForm({ ...form, framing: event.target.value as FramingMode })}
              >
                {(Object.keys(FRAMING_LABELS) as FramingMode[]).map((mode) => (
                  <option key={mode} value={mode} disabled={!isFramingAllowed(mode, limits)}>
                    {FRAMING_LABELS[mode]}
                    {isFramingAllowed(mode, limits) ? "" : PLAN_LOCKED_SUFFIX}
                  </option>
                ))}
              </NativeSelect>
              {!webcamAllowed ? <WebcamLockNote id="clip-framing-lock" /> : null}
            </div>
          </div>
          <SubtitlePreview style={form.subtitleStyle} position={form.subtitlePosition} enabled={form.subtitlesEnabled} />
        </div>
      </div>
      <DialogFooter>
        <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={create.isPending}>
          Cancelar
        </Button>
        <Button onClick={submit} loading={create.isPending}>
          Gerar clipe
        </Button>
      </DialogFooter>
    </>
  );
}

export function GenerateClipDialog(props: GenerateClipDialogProps) {
  const { candidate, onOpenChange } = props;
  return (
    <Dialog open={!!candidate} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        {candidate ? <GenerateClipForm key={candidate.id} {...props} candidate={candidate} /> : null}
      </DialogContent>
    </Dialog>
  );
}
