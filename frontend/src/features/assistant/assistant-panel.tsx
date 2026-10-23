"use client";

import { Bot, CornerDownLeft, Sparkles, Undo2, Zap } from "lucide-react";
import Link from "next/link";
import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";
import { Alert } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Textarea } from "@/components/ui/input";
import {
  applySettingsPatch,
  changePaths,
  INSTRUCTIONS_MAX_LENGTH,
  restoreSettingsPaths,
  SETTINGS_PRESETS,
} from "@/features/projects/analysis-settings";
import { describeError, isApiError } from "@/lib/api/client";
import { useAssistantChat, useAssistantStatus } from "@/lib/api/hooks/use-assistant";
import type { AnalysisSettings, AssistantChatContext, AssistantChatResponse, AssistantMessage, PlanLimits } from "@/lib/api/types";
import { cn } from "@/lib/utils";

/** API contract: 1–20 messages per request, each up to 2000 characters. */
export const ASSISTANT_MAX_MESSAGES = 20;
export const ASSISTANT_MAX_MESSAGE_LENGTH = 2000;
export const ASSISTANT_UNAVAILABLE_TEXT = "Assistente indisponível neste ambiente";

interface AppliedChange {
  key: string;
  label: string;
  /** Settings paths restored by "Desfazer" (empty: nothing the web can restore). */
  paths: string[];
  /** Settings right before the reply was applied. */
  before: AnalysisSettings;
  undone: boolean;
}

type ChatEntry =
  | { id: string; role: "user"; content: string; state: "sending" | "sent" | "failed" }
  | { id: string; role: "assistant"; content: string; changes: AppliedChange[] };

export interface AssistantPanelProps {
  settings: AnalysisSettings;
  onSettingsChange: (next: AnalysisSettings) => void;
  /** Applied to every change made from the panel (plan caps, short-recording bounds). */
  normalize?: (settings: AnalysisSettings) => AnalysisSettings;
  /** Plan limits, to tell "Todos os momentos encontrados" apart when a reply changes the maximum of moments. */
  limits?: PlanLimits | null;
  context?: AssistantChatContext;
  disabled?: boolean;
  className?: string;
}

function chatErrorMessage(error: unknown): { text: string; planLimit: boolean } {
  if (isApiError(error)) {
    if (error.code === "rate_limited" || error.status === 429) {
      return { text: "Muitas mensagens em pouco tempo. Aguarde alguns instantes e tente de novo.", planLimit: false };
    }
    if (error.code === "plan_limit" || error.status === 409) {
      return { text: "Você atingiu o limite de mensagens do assistente do seu plano neste período.", planLimit: true };
    }
  }
  return { text: describeError(error, "Não foi possível falar com o assistente. Tente novamente."), planLimit: false };
}

function isUnavailableError(error: unknown): boolean {
  return isApiError(error) && (error.status === 503 || error.code === "assistant_unavailable");
}

/**
 * "Assistente": a chat that changes the analysis settings through `POST /api/assistant/chat`
 * (the product's own model or a free provider, chosen by the server). Every reply comes from the
 * API — the web never writes one — and lists the validated changes as chips with "Desfazer". The
 * settings form updates live and `settings.instructions` keeps the last request. The "Atalhos" are
 * fixed, deterministic shortcuts (no AI) and keep working when the assistant is unavailable.
 */
export function AssistantPanel({ settings, onSettingsChange, normalize, limits, context, disabled, className }: AssistantPanelProps) {
  const status = useAssistantStatus();
  const chat = useAssistantChat();
  const inputId = useId();
  const [entries, setEntries] = useState<ChatEntry[]>([]);
  const [draft, setDraft] = useState("");
  const [unavailable, setUnavailable] = useState(false);
  const [chatError, setChatError] = useState<{ text: string; planLimit: boolean } | null>(null);
  const settingsRef = useRef(settings);
  const nextId = useRef(0);
  const listRef = useRef<HTMLOListElement | null>(null);

  useEffect(() => {
    settingsRef.current = settings;
  }, [settings]);

  useEffect(() => {
    const list = listRef.current;
    if (list) list.scrollTop = list.scrollHeight;
  }, [entries, chat.isPending]);

  const apply = (next: AnalysisSettings) => {
    const normalized = normalize ? normalize(next) : next;
    settingsRef.current = normalized;
    onSettingsChange(normalized);
  };

  const checking = status.isPending;
  const available = !unavailable && status.data?.available === true;
  const showUnavailable = !checking && !available;
  const busy = chat.isPending;

  const send = () => {
    const text = draft.trim().slice(0, ASSISTANT_MAX_MESSAGE_LENGTH);
    if (!text || busy || !available || disabled) return;
    const userId = `m${nextId.current++}`;
    const history: AssistantMessage[] = entries
      .filter((entry) => entry.role === "assistant" || entry.state === "sent")
      .map((entry) => ({ role: entry.role, content: entry.content.slice(0, ASSISTANT_MAX_MESSAGE_LENGTH) }));
    const messages = [...history, { role: "user" as const, content: text }].slice(-ASSISTANT_MAX_MESSAGES);
    setEntries((current) => [...current, { id: userId, role: "user", content: text, state: "sending" }]);
    setDraft("");
    setChatError(null);
    chat.mutate(
      { messages, currentSettings: settingsRef.current, ...(context ? { context } : {}) },
      {
        onSuccess: (response: AssistantChatResponse) => {
          const before = settingsRef.current;
          const patch = response.settingsPatch ?? null;
          // The conversation's last request is what the analysis receives as `instructions`.
          apply({ ...applySettingsPatch(before, patch, limits), instructions: text.slice(0, INSTRUCTIONS_MAX_LENGTH) });
          const changes = response.changes ?? [];
          const replyId = `m${nextId.current++}`;
          setEntries((current) => [
            ...current.map((entry) => (entry.id === userId ? { ...entry, state: "sent" as const } : entry)),
            {
              id: replyId,
              role: "assistant",
              content: response.reply,
              changes: changes.map((change, index) => ({
                key: `${replyId}-${index}`,
                label: change.label,
                paths: changePaths(
                  change.field,
                  patch,
                  changes.filter((_, other) => other !== index).map((other) => other.field),
                ),
                before,
                undone: false,
              })),
            },
          ]);
        },
        onError: (error) => {
          setEntries((current) => current.map((entry) => (entry.id === userId ? { ...entry, state: "failed" as const } : entry)));
          setDraft((current) => current || text);
          if (isUnavailableError(error)) {
            setUnavailable(true);
            return;
          }
          setChatError(chatErrorMessage(error));
        },
      },
    );
  };

  const undo = (entryId: string, change: AppliedChange) => {
    if (change.undone || change.paths.length === 0) return;
    apply(restoreSettingsPaths(settingsRef.current, change.before, change.paths));
    // A change of the same reply whose paths were all restored with this one is undone too
    // (e.g. "Máximo de momentos" and "Clipes automáticos" restore the same pair).
    const undoneWithIt = (item: AppliedChange) =>
      item.key === change.key || (item.paths.length > 0 && item.paths.every((path) => change.paths.includes(path)));
    setEntries((current) =>
      current.map((entry) =>
        entry.id === entryId && entry.role === "assistant"
          ? { ...entry, changes: entry.changes.map((item) => (undoneWithIt(item) ? { ...item, undone: true } : item)) }
          : entry,
      ),
    );
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      send();
    }
  };

  return (
    <section className={cn("rounded-lg border border-line bg-surface shadow-card", className)} aria-labelledby={`${inputId}-title`}>
      <div className="flex items-start gap-3 p-5 pb-3">
        <span className="flex size-9 shrink-0 items-center justify-center rounded-md bg-accent/10 text-accent">
          <Bot className="size-4" aria-hidden="true" />
        </span>
        <div className="min-w-0">
          <h2 id={`${inputId}-title`} className="text-base font-semibold leading-tight">
            Assistente
          </h2>
          <p className="mt-1 text-sm text-fg-muted">
            Diga o que você quer dos clipes e as configurações da análise mudam na hora. Você confere tudo antes de enviar.
          </p>
        </div>
      </div>

      <div className="space-y-4 p-5 pt-0">
        <div className="space-y-2">
          <p className="flex items-center gap-1.5 text-xs font-medium text-fg-muted" id={`${inputId}-presets`}>
            <Zap className="size-3.5" aria-hidden="true" />
            Atalhos <span className="font-normal text-fg-subtle">(ajustes prontos, sem IA)</span>
          </p>
          <div role="group" aria-labelledby={`${inputId}-presets`} className="flex flex-wrap gap-2">
            {SETTINGS_PRESETS.map((preset) => {
              const active = preset.isActive(settings);
              return (
                <button
                  key={preset.id}
                  type="button"
                  aria-pressed={active}
                  disabled={disabled}
                  onClick={() => apply(preset.toggle(settingsRef.current))}
                  className={cn(
                    "inline-flex h-8 items-center rounded-full border px-3 text-xs font-medium transition-colors duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 disabled:cursor-not-allowed disabled:opacity-50",
                    active ? "border-accent/60 bg-accent/10 text-accent" : "border-line bg-canvas/40 text-fg-muted hover:border-line-strong hover:text-fg",
                  )}
                >
                  {preset.label}
                </button>
              );
            })}
          </div>
        </div>

        {checking ? (
          <p className="text-sm text-fg-muted" role="status">
            Verificando o assistente…
          </p>
        ) : showUnavailable ? (
          <Alert variant="info" title={ASSISTANT_UNAVAILABLE_TEXT}>
            Os atalhos acima continuam funcionando, e todas as configurações podem ser ajustadas no formulário.
          </Alert>
        ) : null}

        {entries.length > 0 || busy ? (
          <ol
            ref={listRef}
            className="max-h-80 space-y-3 overflow-y-auto rounded-md border border-line bg-canvas/40 p-3 scrollbar-thin"
            aria-label="Conversa com o assistente"
            aria-live="polite"
          >
            {entries.map((entry) =>
              entry.role === "user" ? (
                <li key={entry.id} className="flex flex-col items-end">
                  <p className="max-w-[85%] whitespace-pre-wrap break-words rounded-lg rounded-br-sm bg-accent/15 px-3 py-2 text-sm text-fg">
                    <span className="sr-only">Você: </span>
                    {entry.content}
                  </p>
                  {entry.state === "failed" ? <span className="mt-1 text-[11px] text-danger">Não enviada</span> : null}
                </li>
              ) : (
                <li key={entry.id} className="flex flex-col items-start gap-2">
                  <p className="max-w-[85%] whitespace-pre-wrap break-words rounded-lg rounded-bl-sm border border-line bg-elevated px-3 py-2 text-sm text-fg">
                    <span className="sr-only">Assistente: </span>
                    {entry.content}
                  </p>
                  {entry.changes.length > 0 ? (
                    <ul className="flex max-w-full flex-wrap gap-1.5" aria-label="Alterações aplicadas">
                      {entry.changes.map((change) => (
                        <li
                          key={change.key}
                          className={cn(
                            "inline-flex max-w-full items-center gap-1 rounded-full border py-0.5 pl-2.5 pr-1 text-xs",
                            change.undone ? "border-line text-fg-subtle line-through" : "border-accent/40 bg-accent/10 text-accent",
                          )}
                        >
                          <Sparkles className="size-3 shrink-0" aria-hidden="true" />
                          <span className="truncate">{change.label}</span>
                          {change.undone ? (
                            <span className="px-1.5 text-[11px] no-underline">desfeito</span>
                          ) : change.paths.length > 0 ? (
                            <button
                              type="button"
                              onClick={() => undo(entry.id, change)}
                              disabled={disabled}
                              className="inline-flex h-6 items-center gap-1 rounded-full px-2 text-[11px] font-medium text-fg-muted hover:bg-overlay hover:text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
                              aria-label={`Desfazer: ${change.label}`}
                            >
                              <Undo2 className="size-3" aria-hidden="true" />
                              Desfazer
                            </button>
                          ) : null}
                        </li>
                      ))}
                    </ul>
                  ) : null}
                </li>
              ),
            )}
            {busy ? (
              <li className="flex items-start" role="status">
                <span className="inline-flex items-center gap-1 rounded-lg rounded-bl-sm border border-line bg-elevated px-3 py-2.5" aria-hidden="true">
                  <span className="size-1.5 animate-bounce rounded-full bg-fg-muted [animation-delay:-0.2s]" />
                  <span className="size-1.5 animate-bounce rounded-full bg-fg-muted [animation-delay:-0.1s]" />
                  <span className="size-1.5 animate-bounce rounded-full bg-fg-muted" />
                </span>
                <span className="sr-only">O assistente está respondendo…</span>
              </li>
            ) : null}
          </ol>
        ) : null}

        {chatError ? (
          <Alert variant="warning">
            {chatError.text}
            {chatError.planLimit ? (
              <>
                {" "}
                <Link href="/billing" className="text-accent underline-offset-4 hover:underline">
                  Ver planos
                </Link>
              </>
            ) : null}
          </Alert>
        ) : null}

        {available ? (
          <form
            className="space-y-2"
            onSubmit={(event) => {
              event.preventDefault();
              send();
            }}
          >
            <label htmlFor={inputId} className="sr-only">
              Mensagem para o assistente
            </label>
            <div className="flex items-end gap-2">
              <Textarea
                id={inputId}
                value={draft}
                onChange={(event) => setDraft(event.target.value)}
                onKeyDown={onKeyDown}
                rows={2}
                maxLength={ASSISTANT_MAX_MESSAGE_LENGTH}
                placeholder="Ex.: quero clipes curtos para TikTok com os momentos mais engraçados"
                className="min-h-12 flex-1"
                disabled={disabled}
              />
              <Button type="submit" disabled={disabled || busy || !draft.trim()} loading={busy} leftIcon={busy ? undefined : <CornerDownLeft aria-hidden="true" />}>
                Enviar
              </Button>
            </div>
            <p className="text-[11px] text-fg-subtle">
              Enter envia · Shift+Enter quebra a linha. O assistente só altera as configurações desta análise.
            </p>
          </form>
        ) : null}
      </div>
    </section>
  );
}
