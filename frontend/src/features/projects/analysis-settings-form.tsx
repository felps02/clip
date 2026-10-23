"use client";

import { Check, Plus, X } from "lucide-react";
import Link from "next/link";
import { useId, useState, type KeyboardEvent, type ReactNode } from "react";
import { Button } from "@/components/ui/button";
import { ChoiceGroup } from "@/components/ui/choice-group";
import { Input, Textarea } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import {
  autoRenderCap,
  autoRenderMode,
  type AutoRenderMode,
  clipLengthBounds,
  CUSTOM_KEYWORD_MAX_LENGTH,
  CUSTOM_KEYWORDS_MAX,
  DEFAULT_SUBTITLE_STYLE,
  INSTRUCTIONS_MAX_LENGTH,
  isFramingAllowed,
  isSubtitlePositionAllowed,
  isSubtitleStyleAllowed,
  normalizeFocusCategories,
  sensitivityHint,
} from "@/features/projects/analysis-settings";
import { SubtitlePreview } from "@/features/projects/subtitle-preview";
import type {
  AnalysisSettings,
  AspectRatio,
  CandidateCategory,
  FramingMode,
  PlanLimits,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";
import {
  ASPECT_RATIO_LABELS,
  CANDIDATE_CATEGORIES,
  FOCUS_CATEGORY_LABELS,
  FRAMING_DESCRIPTIONS,
  FRAMING_LABELS,
  LANGUAGE_LABELS,
  SUBTITLE_POSITION_LABELS,
  SUBTITLE_STYLE_DESCRIPTIONS,
  SUBTITLE_STYLE_LABELS,
} from "@/lib/labels";
import { clamp, cn } from "@/lib/utils";

export function AspectRatioIcon({ ratio, className }: { ratio: AspectRatio; className?: string }) {
  const dims = ratio === "9:16" ? { w: 12, h: 20 } : ratio === "16:9" ? { w: 22, h: 12.5 } : { w: 16, h: 16 };
  return (
    <svg viewBox="0 0 24 24" className={cn("size-6", className)} aria-hidden="true">
      <rect
        x={(24 - dims.w) / 2}
        y={(24 - dims.h) / 2}
        width={dims.w}
        height={dims.h}
        rx="2"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
      />
    </svg>
  );
}

/** Shown on (or after) options the current plan does not include. */
export const PLAN_LOCKED_DESCRIPTION = "indisponível no seu plano";
/** Suffix for `<option>`s the current plan does not include. */
export const PLAN_LOCKED_SUFFIX = ` (${PLAN_LOCKED_DESCRIPTION})`;

/**
 * Why only the default subtitle style and position are offered (plans without
 * `allowCustomSubtitleStyles`: the API renders every clip of those plans that way).
 */
export function SubtitleLockNote({ id, className }: { id?: string; className?: string }) {
  return (
    <p id={id} className={cn("text-xs text-fg-muted", className)}>
      Os estilos {SUBTITLE_STYLE_LABELS.clean} e {SUBTITLE_STYLE_LABELS.boxed} e as posições {SUBTITLE_POSITION_LABELS.top} e{" "}
      {SUBTITLE_POSITION_LABELS.center} não estão incluídos no seu plano; as legendas usam o estilo{" "}
      {SUBTITLE_STYLE_LABELS[DEFAULT_SUBTITLE_STYLE]}, na parte de baixo do quadro.{" "}
      <Link href="/billing" className="text-accent underline-offset-4 hover:underline">
        Ver planos
      </Link>
    </p>
  );
}

/** Why the webcam + gameplay layout is disabled (plans without `allowWebcamLayout`). */
export function WebcamLockNote({ id, className }: { id?: string; className?: string }) {
  return (
    <p id={id} className={cn("text-xs text-fg-muted", className)}>
      O layout {FRAMING_LABELS["webcam-split"]} não está incluído no seu plano; use Automático ou Centralizado.{" "}
      <Link href="/billing" className="text-accent underline-offset-4 hover:underline">
        Ver planos
      </Link>
    </p>
  );
}

function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return (
    <section className="space-y-3">
      <div>
        <h3 className="text-sm font-semibold">{title}</h3>
        {description ? <p className="text-xs text-fg-muted">{description}</p> : null}
      </div>
      {children}
    </section>
  );
}

/** Toggle chips for `focusCategories` (pressed = the analysis favours that kind of moment). */
export function FocusChips({
  value,
  onChange,
  disabled,
  label = "Foco da análise",
}: {
  value: CandidateCategory[];
  onChange: (next: CandidateCategory[]) => void;
  disabled?: boolean;
  label?: string;
}) {
  return (
    <div role="group" aria-label={label} className="flex flex-wrap gap-2">
      {CANDIDATE_CATEGORIES.map((category) => {
        const pressed = value.includes(category);
        return (
          <button
            key={category}
            type="button"
            aria-pressed={pressed}
            disabled={disabled}
            onClick={() =>
              onChange(pressed ? value.filter((item) => item !== category) : normalizeFocusCategories([...value, category]))
            }
            className={cn(
              "inline-flex h-8 items-center gap-1.5 rounded-full border px-3 text-xs font-medium transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 disabled:cursor-not-allowed disabled:opacity-50",
              pressed ? "border-accent/60 bg-accent/10 text-accent" : "border-line bg-canvas/40 text-fg-muted hover:border-line-strong hover:text-fg",
            )}
          >
            {pressed ? <Check className="size-3.5" aria-hidden="true" /> : null}
            {FOCUS_CATEGORY_LABELS[category]}
          </button>
        );
      })}
    </div>
  );
}

/** Tag input for `customKeywords`: Enter or comma adds, Backspace on an empty field removes the last one. */
export function KeywordsInput({
  id,
  value,
  onChange,
  disabled,
}: {
  id: string;
  value: string[];
  onChange: (next: string[]) => void;
  disabled?: boolean;
}) {
  const [draft, setDraft] = useState("");
  const [error, setError] = useState<string | null>(null);
  const hintId = `${id}-hint`;
  const errorId = `${id}-error`;
  const full = value.length >= CUSTOM_KEYWORDS_MAX;

  const add = () => {
    const keyword = draft.trim().replace(/\s+/g, " ");
    if (!keyword) return;
    if (keyword.length > CUSTOM_KEYWORD_MAX_LENGTH) {
      setError(`Use até ${CUSTOM_KEYWORD_MAX_LENGTH} caracteres por palavra-chave.`);
      return;
    }
    if (value.some((item) => item.toLocaleLowerCase("pt-BR") === keyword.toLocaleLowerCase("pt-BR"))) {
      setError("Essa palavra-chave já está na lista.");
      return;
    }
    if (full) {
      setError(`Limite de ${CUSTOM_KEYWORDS_MAX} palavras-chave.`);
      return;
    }
    onChange([...value, keyword]);
    setDraft("");
    setError(null);
  };

  const onKeyDown = (event: KeyboardEvent<HTMLInputElement>) => {
    if (event.key === "Enter" || event.key === ",") {
      event.preventDefault();
      add();
    } else if (event.key === "Backspace" && draft === "" && value.length > 0) {
      onChange(value.slice(0, -1));
    }
  };

  return (
    <div className="space-y-2">
      {value.length > 0 ? (
        <ul className="flex flex-wrap gap-1.5" aria-label="Palavras-chave adicionadas">
          {value.map((keyword) => (
            <li key={keyword} className="inline-flex max-w-full items-center gap-1 rounded-full border border-line bg-elevated py-0.5 pl-2.5 pr-1 text-xs">
              <span className="truncate">{keyword}</span>
              <button
                type="button"
                onClick={() => onChange(value.filter((item) => item !== keyword))}
                disabled={disabled}
                aria-label={`Remover palavra-chave ${keyword}`}
                className="flex size-5 shrink-0 items-center justify-center rounded-full text-fg-subtle hover:bg-overlay hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
              >
                <X className="size-3" aria-hidden="true" />
              </button>
            </li>
          ))}
        </ul>
      ) : null}
      <div className="flex gap-2">
        <Input
          id={id}
          value={draft}
          onChange={(event) => {
            setDraft(event.target.value);
            if (error) setError(null);
          }}
          onKeyDown={onKeyDown}
          maxLength={CUSTOM_KEYWORD_MAX_LENGTH + 10}
          placeholder={full ? "Limite atingido" : "Ex.: clutch, que isso"}
          disabled={disabled || full}
          invalid={!!error}
          aria-describedby={error ? `${hintId} ${errorId}` : hintId}
        />
        <Button type="button" variant="secondary" onClick={add} disabled={disabled || full || !draft.trim()} leftIcon={<Plus aria-hidden="true" />}>
          Adicionar
        </Button>
      </div>
      <p id={hintId} className="text-xs text-fg-subtle">
        Palavras ou expressões ditas na live que marcam bons momentos (até {CUSTOM_KEYWORDS_MAX}, com até {CUSTOM_KEYWORD_MAX_LENGTH} caracteres).
      </p>
      {error ? (
        <p id={errorId} className="text-xs text-danger" role="alert">
          {error}
        </p>
      ) : null}
    </div>
  );
}


export interface AnalysisSettingsFormProps {
  value: AnalysisSettings;
  onChange: (next: AnalysisSettings) => void;
  limits?: PlanLimits | null;
  disabled?: boolean;
  idPrefix?: string;
  className?: string;
  /**
   * Duration of a recording shorter than the clip minimum: the clip-length range then goes from
   * 5 s up to the recording's length (see `clipLengthBounds`).
   */
  shortSourceSeconds?: number | null;
  /** Shown under the clip-length range (e.g. why it was adjusted for a short recording). */
  clipLengthNote?: ReactNode;
}

export function AnalysisSettingsForm({
  value,
  onChange,
  limits,
  disabled,
  idPrefix = "settings",
  className,
  shortSourceSeconds,
  clipLengthNote,
}: AnalysisSettingsFormProps) {
  const update = (patch: Partial<AnalysisSettings>) => onChange({ ...value, ...patch });
  const maxCandidatesCap = limits?.maxCandidatesPerJob ?? 50;
  const renderCap = autoRenderCap(value.maxCandidates, limits);
  const renderMode = autoRenderMode(value, limits);
  const webcamAllowed = isFramingAllowed("webcam-split", limits);
  const customStylesAllowed = isSubtitleStyleAllowed("boxed", limits);
  const bounds = clipLengthBounds(shortSourceSeconds);
  const instructionsId = `${idPrefix}-instructions`;
  const instructionsHintId = useId();

  const setRenderMode = (mode: AutoRenderMode) => {
    if (mode === "all") update({ autoRenderTop: renderCap });
    else if (mode === "none") update({ autoRenderTop: 0 });
    else {
      const current = value.autoRenderTop > 0 && value.autoRenderTop < renderCap ? value.autoRenderTop : Math.min(5, Math.max(1, renderCap - 1));
      update({ autoRenderTop: clamp(current, 1, renderCap) });
    }
  };

  return (
    <div className={cn("space-y-8", className)}>
      <Section title="Foco da análise" description="Opcional: o tipo de momento que deve pesar mais na escolha dos clipes.">
        <FocusChips
          value={value.focusCategories}
          onChange={(focusCategories) => update({ focusCategories })}
          disabled={disabled}
        />
        <div className="space-y-1.5">
          <Label htmlFor={`${idPrefix}-keywords`}>Palavras-chave</Label>
          <KeywordsInput
            id={`${idPrefix}-keywords`}
            value={value.customKeywords}
            onChange={(customKeywords) => update({ customKeywords })}
            disabled={disabled}
          />
        </div>
        <div className="space-y-1.5">
          <Label htmlFor={instructionsId}>Pedido em suas palavras (opcional)</Label>
          <Textarea
            id={instructionsId}
            value={value.instructions ?? ""}
            onChange={(event) => update({ instructions: event.target.value.length > 0 ? event.target.value : null })}
            maxLength={INSTRUCTIONS_MAX_LENGTH}
            rows={2}
            className="min-h-16"
            placeholder="Ex.: quero os momentos em que o chat explode de rir"
            disabled={disabled}
            aria-describedby={instructionsHintId}
          />
          <p id={instructionsHintId} className="text-xs text-fg-subtle">
            O Assistente guarda aqui o seu último pedido. A análise usa este texto quando há um modelo de IA disponível no servidor.
          </p>
        </div>
      </Section>

      <Section title="Duração dos clipes" description="Faixa de duração aceita para cada clipe gerado.">
        <Slider
          min={bounds.min}
          max={Math.max(bounds.max, bounds.min + 1)}
          step={1}
          minStepsBetweenThumbs={bounds.gap}
          value={[value.minClipSeconds, value.maxClipSeconds]}
          onValueChange={([min, max]) => {
            if (min === undefined || max === undefined) return;
            // The target (from the profile preferences) stays put unless the new range excludes it.
            update({ minClipSeconds: min, maxClipSeconds: max, targetClipSeconds: clamp(value.targetClipSeconds, min, max) });
          }}
          thumbLabels={["Duração mínima", "Duração máxima"]}
          disabled={disabled}
        />
        <div className="flex items-center justify-between text-xs text-fg-muted">
          <span>
            mínima <strong className="font-mono text-fg tabular-nums">{value.minClipSeconds}s</strong>
          </span>
          <span>
            alvo <strong className="font-mono text-fg tabular-nums">{value.targetClipSeconds}s</strong>
          </span>
          <span>
            máxima <strong className="font-mono text-fg tabular-nums">{value.maxClipSeconds}s</strong>
          </span>
        </div>
        {clipLengthNote}
      </Section>

      <Section title="Formato" description="Proporção dos clipes renderizados automaticamente.">
        <ChoiceGroup<AspectRatio>
          label="Formato"
          value={value.aspectRatio}
          onChange={(aspectRatio) => update({ aspectRatio })}
          options={(["9:16", "16:9", "1:1"] as AspectRatio[]).map((ratio) => ({
            value: ratio,
            label: ratio,
            description: ASPECT_RATIO_LABELS[ratio].replace(/\s*\(.*\)/, ""),
            icon: <AspectRatioIcon ratio={ratio} />,
            disabled,
          }))}
        />
      </Section>

      <Section title="Legendas" description="Legendas sincronizadas por palavra, queimadas no vídeo.">
        <div className="flex items-center justify-between gap-4 rounded-md border border-line bg-canvas/40 px-3 py-2.5">
          <Label htmlFor={`${idPrefix}-subtitles`} className="cursor-pointer">
            Gerar legendas
          </Label>
          <Switch
            id={`${idPrefix}-subtitles`}
            checked={value.subtitles.enabled}
            onCheckedChange={(enabled) => update({ subtitles: { ...value.subtitles, enabled } })}
            disabled={disabled}
          />
        </div>
        {/* Stacked (not side by side with the preview), so each option gets a third of the card's
            width and labels such as "Em cima" stay on one line. */}
        <div className={cn("space-y-4", !value.subtitles.enabled && "opacity-60")}>
          <div className="space-y-2">
            <p className="text-xs font-medium text-fg-muted" aria-hidden="true">
              Estilo
            </p>
            <ChoiceGroup<SubtitleStyle>
              label="Estilo da legenda"
              value={value.subtitles.style}
              onChange={(style) => update({ subtitles: { ...value.subtitles, style } })}
              columns={3}
              size="sm"
              options={(["bold", "clean", "boxed"] as SubtitleStyle[]).map((style) => {
                const locked = !isSubtitleStyleAllowed(style, limits);
                return {
                  value: style,
                  label: SUBTITLE_STYLE_LABELS[style],
                  description: locked ? PLAN_LOCKED_DESCRIPTION : undefined,
                  disabled: disabled || !value.subtitles.enabled || locked,
                };
              })}
            />
            <p className="text-xs text-fg-muted">{SUBTITLE_STYLE_DESCRIPTIONS[value.subtitles.style]}</p>
          </div>
          <div className="space-y-2">
            <p className="text-xs font-medium text-fg-muted" aria-hidden="true">
              Posição
            </p>
            <ChoiceGroup<SubtitlePosition>
              label="Posição da legenda"
              value={value.subtitles.position}
              onChange={(position) => update({ subtitles: { ...value.subtitles, position } })}
              columns={3}
              size="sm"
              options={(["top", "center", "bottom"] as SubtitlePosition[]).map((position) => {
                const locked = !isSubtitlePositionAllowed(position, limits);
                return {
                  value: position,
                  label: <span className="whitespace-nowrap">{SUBTITLE_POSITION_LABELS[position]}</span>,
                  description: locked ? PLAN_LOCKED_DESCRIPTION : undefined,
                  disabled: disabled || !value.subtitles.enabled || locked,
                };
              })}
            />
          </div>
          {!customStylesAllowed ? <SubtitleLockNote /> : null}
          <SubtitlePreview
            style={value.subtitles.style}
            position={value.subtitles.position}
            enabled={value.subtitles.enabled}
            className="sm:max-w-sm"
          />
        </div>
      </Section>

      <Section
        title="Sensibilidade"
        description="Baixa = só momentos muito fortes · Alta = mais candidatos para revisar."
      >
        <Slider
          min={0}
          max={100}
          step={5}
          value={[value.sensitivity]}
          onValueChange={([sensitivity]) => sensitivity !== undefined && update({ sensitivity })}
          thumbLabels={["Sensibilidade"]}
          disabled={disabled}
        />
        <div className="flex items-start justify-between gap-4 text-xs">
          <span className="text-fg-muted">{sensitivityHint(value.sensitivity)}</span>
          <span className="font-mono text-fg tabular-nums">{value.sensitivity}</span>
        </div>
      </Section>

      <div className="grid gap-4">
        <div className="space-y-1.5">
          <Label htmlFor={`${idPrefix}-max-candidates`}>Máximo de momentos</Label>
          <Input
            id={`${idPrefix}-max-candidates`}
            type="number"
            min={1}
            max={maxCandidatesCap}
            value={value.maxCandidates}
            onChange={(event) => {
              const next = clamp(Number(event.target.value) || 1, 1, maxCandidatesCap);
              const nextCap = autoRenderCap(next, limits);
              // "Todos os momentos encontrados" follows the new maximum; a fixed number only shrinks with it.
              update({ maxCandidates: next, autoRenderTop: renderMode === "all" ? nextCap : Math.min(value.autoRenderTop, nextCap) });
            }}
            disabled={disabled}
            aria-describedby={`${idPrefix}-max-candidates-hint`}
          />
          <p id={`${idPrefix}-max-candidates-hint`} className="text-xs text-fg-subtle">
            Seu plano permite até {maxCandidatesCap} por análise.
          </p>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor={`${idPrefix}-auto-render`}>Clipes prontos automaticamente</Label>
          <NativeSelect
            id={`${idPrefix}-auto-render`}
            value={renderMode}
            onChange={(event) => setRenderMode(event.target.value as AutoRenderMode)}
            disabled={disabled}
            aria-describedby={`${idPrefix}-auto-render-hint`}
          >
            <option value="all">
              Todos os momentos encontrados{renderCap < value.maxCandidates ? ` (até ${renderCap} pelo seu plano)` : ""}
            </option>
            {renderCap > 1 ? <option value="top">Só os melhores</option> : null}
            <option value="none">Nenhum: revisar antes</option>
          </NativeSelect>
          {renderMode === "top" ? (
            <div className="flex items-center gap-2">
              <Label htmlFor={`${idPrefix}-auto-render-count`} className="text-xs font-normal text-fg-muted">
                Quantos
              </Label>
              <Input
                id={`${idPrefix}-auto-render-count`}
                type="number"
                min={1}
                max={renderCap}
                value={value.autoRenderTop}
                onChange={(event) => update({ autoRenderTop: clamp(Number(event.target.value) || 1, 1, renderCap) })}
                disabled={disabled}
                className="h-9 w-24"
              />
            </div>
          ) : null}
          <p id={`${idPrefix}-auto-render-hint`} className="text-xs text-fg-subtle">
            {renderMode === "all"
              ? `Cada momento encontrado vira um clipe pronto para baixar (até ${renderCap} por análise).`
              : renderMode === "top"
                ? `Os ${value.autoRenderTop} melhores momentos viram clipes; os outros ficam em “Momentos encontrados” para gerar depois.`
                : "Nenhum clipe automático: revise os momentos encontrados e gere só os que quiser."}
          </p>
        </div>
      </div>

      <div className="grid gap-4 sm:grid-cols-2">
        <div className="space-y-1.5">
          <Label htmlFor={`${idPrefix}-language`}>Idioma falado na gravação</Label>
          <NativeSelect
            id={`${idPrefix}-language`}
            value={value.language}
            onChange={(event) => update({ language: event.target.value })}
            disabled={disabled}
          >
            {Object.entries(LANGUAGE_LABELS).map(([code, label]) => (
              <option key={code} value={code}>
                {label}
              </option>
            ))}
          </NativeSelect>
        </div>
        <div className="space-y-1.5">
          <Label htmlFor={`${idPrefix}-framing`}>Enquadramento</Label>
          <NativeSelect
            id={`${idPrefix}-framing`}
            value={value.framing.mode}
            onChange={(event) => update({ framing: { ...value.framing, mode: event.target.value as FramingMode } })}
            disabled={disabled}
            aria-describedby={webcamAllowed ? `${idPrefix}-framing-hint` : `${idPrefix}-framing-hint ${idPrefix}-framing-lock`}
          >
            {(Object.keys(FRAMING_LABELS) as FramingMode[]).map((mode) => (
              <option key={mode} value={mode} disabled={!isFramingAllowed(mode, limits)}>
                {FRAMING_LABELS[mode]}
                {isFramingAllowed(mode, limits) ? "" : PLAN_LOCKED_SUFFIX}
              </option>
            ))}
          </NativeSelect>
          <p id={`${idPrefix}-framing-hint`} className="text-xs text-fg-subtle">
            {FRAMING_DESCRIPTIONS[value.framing.mode]}
          </p>
          {!webcamAllowed ? <WebcamLockNote id={`${idPrefix}-framing-lock`} /> : null}
        </div>
      </div>
    </div>
  );
}
