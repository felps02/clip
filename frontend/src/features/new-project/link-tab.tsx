"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { CheckCircle2, Link2, Radio, UploadCloud, XCircle } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useRef, useState } from "react";
import { useForm } from "react-hook-form";
import { toast } from "sonner";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { trimSettingsForSubmit } from "@/features/projects/analysis-settings";
import { AnalysisSettingsForm } from "@/features/projects/analysis-settings-form";
import { describeError } from "@/lib/api/client";
import { useCreateProject } from "@/lib/api/hooks/use-projects";
import { useValidateSource } from "@/lib/api/hooks/use-sources";
import type { AnalysisSettings, PlanLimits, SourceAlternative, SourceValidation } from "@/lib/api/types";
import { formatDuration } from "@/lib/format";
import { platformLabel } from "@/lib/labels";
import { randomUuid } from "@/lib/uuid";
import { linkSchema, type LinkInput } from "@/lib/validation";
import { cn } from "@/lib/utils";

export interface LinkTabProps {
  settings: AnalysisSettings;
  onSettingsChange: (next: AnalysisSettings) => void;
  limits?: PlanLimits | null;
  onSwitchToUpload: () => void;
}

function AlternativeCard({ alternative, onSwitchToUpload }: { alternative: SourceAlternative; onSwitchToUpload: () => void }) {
  const isUpload = alternative.kind === "upload";
  const isCompanion = alternative.kind === "obs_companion";
  const Icon = isUpload ? UploadCloud : isCompanion ? Radio : Link2;
  return (
    <div className="flex flex-col rounded-md border border-line bg-canvas/40 p-4">
      <div className="flex size-9 items-center justify-center rounded-md bg-elevated text-accent">
        <Icon className="size-4" aria-hidden="true" />
      </div>
      <p className="mt-3 text-sm font-medium">{alternative.title}</p>
      <p className="mt-1 flex-1 text-xs text-fg-muted">{alternative.description}</p>
      {isUpload ? (
        <Button variant="secondary" size="sm" className="mt-3 self-start" onClick={onSwitchToUpload}>
          Enviar gravação
        </Button>
      ) : isCompanion ? (
        <Link href="/live" className={cn(buttonVariants({ variant: "secondary", size: "sm" }), "mt-3 self-start")}>
          Ver captura ao vivo
        </Link>
      ) : null}
    </div>
  );
}

export function LinkTab({ settings, onSettingsChange, limits, onSwitchToUpload }: LinkTabProps) {
  const router = useRouter();
  const validate = useValidateSource();
  const createProject = useCreateProject();
  const [result, setResult] = useState<{ url: string; validation: SourceValidation } | null>(null);
  const [formError, setFormError] = useState<string | null>(null);
  const resultRef = useRef<HTMLDivElement | null>(null);

  // The verdict appears right under the field: focus it (screen readers read it, keyboard users
  // continue from there) and scroll it into view, so "Verificar" never looks like it did nothing.
  useEffect(() => {
    const element = resultRef.current;
    if (!result || !element) return;
    element.focus({ preventScroll: true });
    element.scrollIntoView({ block: "nearest", behavior: "smooth" });
  }, [result]);

  const form = useForm<LinkInput>({ resolver: zodResolver(linkSchema), defaultValues: { url: "" } });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    setResult(null);
    validate.mutate(values.url, {
      onSuccess: (validation) => setResult({ url: values.url, validation }),
      onError: (error) => setFormError(applyApiErrors(error, form.setError, ["url"])),
    });
  });

  const process = () => {
    if (!result || result.validation.supported !== true) return;
    const idempotencyKey = randomUuid();
    createProject.mutate(
      {
        body: {
          title: result.validation.metadata?.title ?? undefined,
          source: { kind: "link", url: result.url },
          settings: trimSettingsForSubmit(settings),
        },
        idempotencyKey,
      },
      {
        onSuccess: (created) => {
          toast.success("Projeto criado. A análise começou.");
          router.push(`/projects/${created.project.id}`);
        },
        onError: (error) => toast.error(describeError(error)),
      },
    );
  };

  const validation = result?.validation;
  const supported = validation?.supported === true;

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1.1fr)_minmax(0,1fr)]">
      <div className="min-w-0 space-y-5">
        <Card>
          <CardHeader>
            <CardTitle>Link da transmissão</CardTitle>
            <CardDescription>
              Cole um link para saber a plataforma e se ele pode ser processado. Transmissões da Twitch, do YouTube e da Kick não podem
              ser baixadas por terceiros, então para elas o caminho é enviar o arquivo da gravação.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <form onSubmit={onSubmit} noValidate className="space-y-4">
              <FormError message={formError} />
              <FormField id="source-url" label="URL" error={errors.url?.message}>
                <div className="flex flex-col gap-2 sm:flex-row">
                  <Input
                    id="source-url"
                    type="url"
                    inputMode="url"
                    placeholder="https://…"
                    invalid={!!errors.url}
                    aria-describedby={fieldDescribedBy("source-url", !!errors.url, false)}
                    {...form.register("url")}
                  />
                  <Button type="submit" loading={validate.isPending} className="sm:w-32">
                    Verificar
                  </Button>
                </div>
              </FormField>
            </form>
            <div aria-live="polite" aria-atomic="true" className="sr-only">
              {validation
                ? `Link verificado: ${platformLabel(validation.platform)}, ${supported ? "pode ser processado" : "não pode ser processado diretamente"}.`
                : ""}
            </div>
            {validation ? (
              <div
                ref={resultRef}
                tabIndex={-1}
                role="region"
                aria-label="Resultado da verificação do link"
                className="mt-4 space-y-4 rounded-md border border-line bg-canvas/40 p-4 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60"
              >
                <div className="flex flex-wrap items-center gap-2">
                  <Badge tone="info">{platformLabel(validation.platform)}</Badge>
                  {supported ? (
                    <Badge tone="success">
                      <CheckCircle2 className="size-3" aria-hidden="true" /> Suportado
                    </Badge>
                  ) : (
                    <Badge tone="warning">
                      <XCircle className="size-3" aria-hidden="true" /> Não suportado
                    </Badge>
                  )}
                </div>
                {validation.metadata?.title ? (
                  <p className="text-sm">
                    <span className="text-fg-muted">Título: </span>
                    {validation.metadata.title}
                    {validation.metadata.durationSeconds ? (
                      <span className="text-fg-muted"> · {formatDuration(validation.metadata.durationSeconds)}</span>
                    ) : null}
                  </p>
                ) : null}
                {!supported ? (
                  <Alert variant="warning" title="Este link não pode ser processado diretamente">
                    {validation.reason ||
                      "As plataformas não permitem baixar transmissões de terceiros e não fazemos scraping. Use uma das alternativas."}
                  </Alert>
                ) : (
                  <Alert variant="success">O link pode ser processado. Revise as configurações e clique em Processar.</Alert>
                )}
                {!supported && validation.alternatives && validation.alternatives.length > 0 ? (
                  <div className="grid gap-3 sm:grid-cols-2">
                    {validation.alternatives.map((alternative) => (
                      <AlternativeCard
                        key={`${alternative.kind}-${alternative.title}`}
                        alternative={alternative}
                        onSwitchToUpload={onSwitchToUpload}
                      />
                    ))}
                  </div>
                ) : null}
                {supported ? (
                  <div className="flex justify-end">
                    <Button size="lg" onClick={process} loading={createProject.isPending}>
                      Processar
                    </Button>
                  </div>
                ) : null}
              </div>
            ) : null}
          </CardContent>
        </Card>
      </div>

      <Card className="h-fit min-w-0">
        <CardHeader>
          <CardTitle>Configurações da análise</CardTitle>
          <CardDescription>Aplicadas quando o link for suportado.</CardDescription>
        </CardHeader>
        <CardContent>
          <AnalysisSettingsForm value={settings} onChange={onSettingsChange} limits={limits} idPrefix="link" />
        </CardContent>
      </Card>
    </div>
  );
}
