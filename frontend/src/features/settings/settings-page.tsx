"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { Bell, Plug, Scissors, ShieldCheck, Trash2, UserRound } from "lucide-react";
import Link from "next/link";
import { usePathname, useRouter, useSearchParams } from "next/navigation";
import { useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { toast } from "sonner";
import { PageHeader } from "@/components/layout/page-header";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Alert } from "@/components/ui/alert";
import { Badge } from "@/components/ui/badge";
import { Button, buttonVariants } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { ChoiceGroup } from "@/components/ui/choice-group";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { NativeSelect } from "@/components/ui/select";
import { CardSkeleton } from "@/components/ui/skeleton";
import { Slider } from "@/components/ui/slider";
import { Switch } from "@/components/ui/switch";
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs";
import { brand } from "@/config/brand";
import { AspectRatioIcon, PLAN_LOCKED_SUFFIX, SubtitleLockNote } from "@/features/projects/analysis-settings-form";
import {
  CLIP_LENGTH_MAX,
  CLIP_LENGTH_MIN,
  CLIP_LENGTH_MIN_GAP,
  DEFAULT_SUBTITLE_POSITION,
  DEFAULT_SUBTITLE_STYLE,
  fitClipLengths,
  isSubtitlePositionAllowed,
  isSubtitleStyleAllowed,
} from "@/features/projects/analysis-settings";
import { SubtitlePreview } from "@/features/projects/subtitle-preview";
import { describeError, isApiError } from "@/lib/api/client";
import { useDeleteAccount, useProfile, useUpdateProfile } from "@/lib/api/hooks/use-account";
import { useChangePassword, useLogout, useMe } from "@/lib/api/hooks/use-auth";
import type {
  AspectRatio,
  AudioLanguage,
  EditingPreferences,
  NotificationPreferences,
  PlanLimits,
  Profile,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";
import { LANGUAGE_LABELS, SUBTITLE_POSITION_LABELS, SUBTITLE_STYLE_LABELS, type Tone } from "@/lib/labels";
import {
  AUDIO_LANGUAGE_VALUES,
  INTERFACE_LOCALE,
  normalizeEditingPreferences,
  normalizeNotificationPreferences,
  validateEditingPreferences,
  type EditingPreferenceErrors,
} from "@/lib/preferences";
import {
  changePasswordSchema,
  deleteAccountSchema,
  profileSchema,
  type ChangePasswordInput,
  type DeleteAccountInput,
  type ProfileInput,
} from "@/lib/validation";
import { cn } from "@/lib/utils";

const TABS = [
  { value: "perfil", label: "Perfil", icon: UserRound },
  { value: "seguranca", label: "Segurança", icon: ShieldCheck },
  { value: "edicao", label: "Preferências de edição", icon: Scissors },
  { value: "integracoes", label: "Integrações", icon: Plug },
  { value: "notificacoes", label: "Notificações", icon: Bell },
  { value: "conta", label: "Excluir conta", icon: Trash2 },
] as const;
type TabValue = (typeof TABS)[number]["value"];

function ProfileTab({ profile }: { profile: Profile }) {
  const update = useUpdateProfile();
  const [formError, setFormError] = useState<string | null>(null);
  const form = useForm<ProfileInput>({
    resolver: zodResolver(profileSchema),
    values: { displayName: profile.displayName ?? "", timezone: profile.timezone ?? "" },
  });
  const { errors, isDirty } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    update.mutate(
      // The interface only exists in pt-BR, so the locale is always sent as such.
      { displayName: values.displayName, locale: INTERFACE_LOCALE, timezone: values.timezone || null },
      {
        onSuccess: () => toast.success("Perfil atualizado"),
        onError: (error) => setFormError(applyApiErrors(error, form.setError, ["displayName", "timezone"])),
      },
    );
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Perfil</CardTitle>
        <CardDescription>Como você aparece no produto e nas notificações.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={onSubmit} noValidate className="max-w-md space-y-4">
          <FormError message={formError} />
          <FormField id="displayName" label="Nome de exibição" error={errors.displayName?.message} required>
            <Input
              id="displayName"
              autoComplete="nickname"
              invalid={!!errors.displayName}
              aria-describedby={fieldDescribedBy("displayName", !!errors.displayName, false)}
              {...form.register("displayName")}
            />
          </FormField>
          <dl className="space-y-1">
            <dt className="text-sm font-medium">Idioma da interface</dt>
            <dd className="text-sm text-fg">Português (Brasil)</dd>
            <dd className="text-xs text-fg-subtle">
              A interface está disponível apenas em português. O idioma falado nas suas gravações é escolhido em Preferências de edição.
            </dd>
          </dl>
          <FormField id="timezone" label="Fuso horário" hint="Ex.: America/Sao_Paulo" error={errors.timezone?.message}>
            <Input
              id="timezone"
              placeholder="America/Sao_Paulo"
              aria-describedby={fieldDescribedBy("timezone", !!errors.timezone, true)}
              {...form.register("timezone")}
            />
          </FormField>
          <Button type="submit" loading={update.isPending} disabled={!isDirty}>
            Salvar
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

function SecurityTab() {
  const change = useChangePassword();
  const [formError, setFormError] = useState<string | null>(null);
  const form = useForm<ChangePasswordInput>({
    resolver: zodResolver(changePasswordSchema),
    defaultValues: { currentPassword: "", newPassword: "", confirmPassword: "" },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    change.mutate(
      { currentPassword: values.currentPassword, newPassword: values.newPassword },
      {
        onSuccess: () => {
          toast.success("Senha alterada. As outras sessões foram encerradas.");
          form.reset();
        },
        onError: (error) =>
          setFormError(applyApiErrors(error, form.setError, ["currentPassword", "newPassword"], { password: "newPassword" })),
      },
    );
  });

  return (
    <Card>
      <CardHeader>
        <CardTitle>Segurança</CardTitle>
        <CardDescription>Ao alterar a senha, todas as outras sessões são encerradas.</CardDescription>
      </CardHeader>
      <CardContent>
        <form onSubmit={onSubmit} noValidate className="max-w-md space-y-4">
          <FormError message={formError} />
          <FormField id="currentPassword" label="Senha atual" error={errors.currentPassword?.message} required>
            <Input
              id="currentPassword"
              type="password"
              autoComplete="current-password"
              invalid={!!errors.currentPassword}
              aria-describedby={fieldDescribedBy("currentPassword", !!errors.currentPassword, false)}
              {...form.register("currentPassword")}
            />
          </FormField>
          <FormField
            id="newPassword"
            label="Nova senha"
            hint="Mínimo de 8 caracteres, com pelo menos uma letra e um número."
            error={errors.newPassword?.message}
            required
          >
            <Input
              id="newPassword"
              type="password"
              autoComplete="new-password"
              invalid={!!errors.newPassword}
              aria-describedby={fieldDescribedBy("newPassword", !!errors.newPassword, true)}
              {...form.register("newPassword")}
            />
          </FormField>
          <FormField id="confirmPassword" label="Confirmar nova senha" error={errors.confirmPassword?.message} required>
            <Input
              id="confirmPassword"
              type="password"
              autoComplete="new-password"
              invalid={!!errors.confirmPassword}
              aria-describedby={fieldDescribedBy("confirmPassword", !!errors.confirmPassword, false)}
              {...form.register("confirmPassword")}
            />
          </FormField>
          <Button type="submit" loading={change.isPending}>
            Alterar senha
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

const EDITING_FIELDS = ["aspectRatio", "subtitlesEnabled", "subtitleStyle", "subtitlePosition", "minClipSeconds", "maxClipSeconds", "targetClipSeconds", "language"] as const;

/** 400 validation_failed field errors keyed like editingPreferences.minClipSeconds, mapped to the form fields. */
function editingErrorsFromApi(error: unknown): EditingPreferenceErrors {
  if (!isApiError(error)) return {};
  const result: EditingPreferenceErrors = {};
  for (const [key, message] of Object.entries(error.fieldMessages())) {
    const field = key.replace(/^editingPreferences\./i, "");
    const match = EDITING_FIELDS.find((name) => name.toLowerCase() === field.toLowerCase());
    if (match) result[match] = message;
  }
  return result;
}

/** Stored preferences inside the range the clip-length sliders offer (the same as the new-project form). */
function preferencesForEditor(initial: Partial<EditingPreferences> | null | undefined): EditingPreferences {
  const prefs = normalizeEditingPreferences(initial);
  const lengths = fitClipLengths(prefs.minClipSeconds, prefs.maxClipSeconds, prefs.targetClipSeconds);
  return { ...prefs, minClipSeconds: lengths.min, maxClipSeconds: lengths.max, targetClipSeconds: lengths.target };
}

function FieldError({ id, message }: { id: string; message?: string }) {
  if (!message) return null;
  return (
    <p id={id} className="text-xs text-danger" role="alert">
      {message}
    </p>
  );
}

export function EditingTab({ initial, limits }: { initial: Partial<EditingPreferences> | null | undefined; limits?: PlanLimits | null }) {
  const update = useUpdateProfile();
  const [prefs, setPrefs] = useState<EditingPreferences>(() => preferencesForEditor(initial));
  const [errors, setErrors] = useState<EditingPreferenceErrors>({});
  const [formError, setFormError] = useState<string | null>(null);
  const customStylesAllowed = isSubtitleStyleAllowed("boxed", limits);
  // A saved preference the plan no longer includes is kept (it applies again after an upgrade), but
  // new projects use the plan's default: say so, and preview what they will actually get.
  const styleLocked = !isSubtitleStyleAllowed(prefs.subtitleStyle, limits);
  const positionLocked = !isSubtitlePositionAllowed(prefs.subtitlePosition, limits);

  const change = (patch: Partial<EditingPreferences>) => {
    setPrefs((current) => ({ ...current, ...patch }));
    setErrors({});
    setFormError(null);
  };

  const save = () => {
    const invalid = validateEditingPreferences(prefs);
    if (Object.keys(invalid).length > 0) {
      setErrors(invalid);
      return;
    }
    setFormError(null);
    update.mutate(
      { editingPreferences: prefs },
      {
        onSuccess: () => toast.success("Preferências salvas"),
        onError: (error) => {
          const fieldErrors = editingErrorsFromApi(error);
          setErrors(fieldErrors);
          setFormError(Object.keys(fieldErrors).length > 0 ? "Alguns valores não foram aceitos. Revise os campos destacados." : describeError(error));
        },
      },
    );
  };

  const styleDescribedBy = [customStylesAllowed ? null : "pref-subtitle-lock", styleLocked ? "pref-style-saved" : null, errors.subtitleStyle ? "pref-style-error" : null]
    .filter(Boolean)
    .join(" ");
  const positionDescribedBy = [
    customStylesAllowed ? null : "pref-subtitle-lock",
    positionLocked ? "pref-position-saved" : null,
    errors.subtitlePosition ? "pref-position-error" : null,
  ]
    .filter(Boolean)
    .join(" ");

  return (
    <Card>
      <CardHeader>
        <CardTitle>Preferências de edição</CardTitle>
        <CardDescription>Valores iniciais de cada novo projeto e dos seus clipes. Você sempre pode mudar caso a caso.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-6">
        <FormError message={formError} />
        <div className="space-y-2">
          <p className="text-sm font-medium">Formato padrão</p>
          <ChoiceGroup<AspectRatio>
            label="Formato padrão"
            value={prefs.aspectRatio}
            onChange={(aspectRatio) => change({ aspectRatio })}
            size="sm"
            className="max-w-md"
            options={(["9:16", "16:9", "1:1"] as AspectRatio[]).map((ratio) => ({
              value: ratio,
              label: ratio,
              icon: <AspectRatioIcon ratio={ratio} className="size-5" />,
            }))}
          />
          <FieldError id="pref-aspect-error" message={errors.aspectRatio} />
        </div>

        <div className="grid gap-4 sm:grid-cols-[1fr_1fr]">
          <div className="space-y-3">
            <div className="flex items-center justify-between gap-3">
              <Label htmlFor="pref-subtitles">Legendas por padrão</Label>
              <Switch id="pref-subtitles" checked={prefs.subtitlesEnabled} onCheckedChange={(subtitlesEnabled) => change({ subtitlesEnabled })} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="pref-style">Estilo da legenda</Label>
              <NativeSelect
                id="pref-style"
                value={prefs.subtitleStyle}
                invalid={!!errors.subtitleStyle}
                aria-describedby={styleDescribedBy || undefined}
                onChange={(event) => change({ subtitleStyle: event.target.value as SubtitleStyle })}
              >
                {(Object.keys(SUBTITLE_STYLE_LABELS) as SubtitleStyle[]).map((style) => {
                  const allowed = isSubtitleStyleAllowed(style, limits);
                  return (
                    <option key={style} value={style} disabled={!allowed}>
                      {SUBTITLE_STYLE_LABELS[style]}
                      {allowed ? "" : PLAN_LOCKED_SUFFIX}
                    </option>
                  );
                })}
              </NativeSelect>
              {styleLocked ? (
                <p id="pref-style-saved" className="text-xs text-warning">
                  A preferência salva ({SUBTITLE_STYLE_LABELS[prefs.subtitleStyle]}) não está disponível no seu plano atual: os novos
                  projetos usam {SUBTITLE_STYLE_LABELS[DEFAULT_SUBTITLE_STYLE]}.
                </p>
              ) : null}
              <FieldError id="pref-style-error" message={errors.subtitleStyle} />
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="pref-position">Posição da legenda</Label>
              <NativeSelect
                id="pref-position"
                value={prefs.subtitlePosition}
                invalid={!!errors.subtitlePosition}
                aria-describedby={positionDescribedBy || undefined}
                onChange={(event) => change({ subtitlePosition: event.target.value as SubtitlePosition })}
              >
                {(Object.keys(SUBTITLE_POSITION_LABELS) as SubtitlePosition[]).map((position) => {
                  const allowed = isSubtitlePositionAllowed(position, limits);
                  return (
                    <option key={position} value={position} disabled={!allowed}>
                      {SUBTITLE_POSITION_LABELS[position]}
                      {allowed ? "" : PLAN_LOCKED_SUFFIX}
                    </option>
                  );
                })}
              </NativeSelect>
              {positionLocked ? (
                <p id="pref-position-saved" className="text-xs text-warning">
                  A preferência salva ({SUBTITLE_POSITION_LABELS[prefs.subtitlePosition]}) não está disponível no seu plano atual: os novos
                  projetos usam {SUBTITLE_POSITION_LABELS[DEFAULT_SUBTITLE_POSITION]}.
                </p>
              ) : null}
              <FieldError id="pref-position-error" message={errors.subtitlePosition} />
            </div>
            {!customStylesAllowed ? <SubtitleLockNote id="pref-subtitle-lock" /> : null}
          </div>
          <SubtitlePreview
            style={styleLocked ? DEFAULT_SUBTITLE_STYLE : prefs.subtitleStyle}
            position={positionLocked ? DEFAULT_SUBTITLE_POSITION : prefs.subtitlePosition}
            enabled={prefs.subtitlesEnabled}
          />
        </div>

        <div className="space-y-3">
          <div>
            <p className="text-sm font-medium">Duração dos clipes</p>
            <p className="text-xs text-fg-muted">
              Faixa aceita para cada clipe e a duração alvo, entre {CLIP_LENGTH_MIN} s e {CLIP_LENGTH_MAX} s (a mesma faixa do formulário de novo projeto).
            </p>
          </div>
          <Slider
            min={CLIP_LENGTH_MIN}
            max={CLIP_LENGTH_MAX}
            step={1}
            minStepsBetweenThumbs={CLIP_LENGTH_MIN_GAP}
            value={[prefs.minClipSeconds, prefs.maxClipSeconds]}
            onValueChange={([min, max]) => {
              if (min === undefined || max === undefined) return;
              const lengths = fitClipLengths(min, max, prefs.targetClipSeconds);
              change({ minClipSeconds: lengths.min, maxClipSeconds: lengths.max, targetClipSeconds: lengths.target });
            }}
            thumbLabels={["Duração mínima", "Duração máxima"]}
          />
          <div className="space-y-1.5 pt-1">
            <p className="text-xs text-fg-muted" id="pref-target-label">
              Duração alvo (entre a mínima e a máxima)
            </p>
            <Slider
              min={prefs.minClipSeconds}
              max={prefs.maxClipSeconds}
              step={1}
              value={[prefs.targetClipSeconds]}
              onValueChange={([target]) => {
                if (target !== undefined) change({ targetClipSeconds: target });
              }}
              thumbLabels={["Duração alvo"]}
            />
          </div>
          <div className="flex flex-wrap items-center justify-between gap-2 text-xs text-fg-muted">
            <span>
              mínima <strong className="font-mono text-fg tabular-nums">{prefs.minClipSeconds}s</strong>
            </span>
            <span>
              alvo <strong className="font-mono text-fg tabular-nums">{prefs.targetClipSeconds}s</strong>
            </span>
            <span>
              máxima <strong className="font-mono text-fg tabular-nums">{prefs.maxClipSeconds}s</strong>
            </span>
          </div>
          <FieldError id="pref-min-error" message={errors.minClipSeconds} />
          <FieldError id="pref-max-error" message={errors.maxClipSeconds} />
          <FieldError id="pref-target-error" message={errors.targetClipSeconds} />
        </div>

        <div className="max-w-md space-y-1.5">
          <Label htmlFor="pref-language">Idioma falado nas gravações</Label>
          <NativeSelect
            id="pref-language"
            value={prefs.language}
            invalid={!!errors.language}
            aria-describedby={errors.language ? "pref-language-hint pref-language-error" : "pref-language-hint"}
            onChange={(event) => change({ language: event.target.value as AudioLanguage })}
          >
            {AUDIO_LANGUAGE_VALUES.map((code) => (
              <option key={code} value={code}>
                {LANGUAGE_LABELS[code]}
              </option>
            ))}
          </NativeSelect>
          <p id="pref-language-hint" className="text-xs text-fg-subtle">
            Usado na transcrição das gravações. Automático detecta o idioma de cada gravação. Não muda o idioma da interface.
          </p>
          <FieldError id="pref-language-error" message={errors.language} />
        </div>

        <Button onClick={save} loading={update.isPending}>
          Salvar preferências
        </Button>
      </CardContent>
    </Card>
  );
}

interface IntegrationInfo {
  name: string;
  description: string;
  status: string;
  tone: Tone;
  action: { href: string; label: string };
}

const INTEGRATIONS: IntegrationInfo[] = [
  {
    name: "Twitch",
    description:
      "Não há integração com a Twitch: nada é lido nem conectado à sua conta. Para gerar clipes de uma live, baixe a gravação (VOD) e envie o arquivo.",
    status: "Não disponível",
    tone: "neutral",
    action: { href: "/new", label: "Enviar gravação" },
  },
  {
    name: "YouTube",
    description:
      "Não há integração com o YouTube. Envie o arquivo da gravação; a verificação de link em Novo projeto diz se um endereço pode ser processado.",
    status: "Não disponível",
    tone: "neutral",
    action: { href: "/new", label: "Enviar gravação" },
  },
  {
    name: "OBS companion",
    description: "Captura ao vivo a partir da sua gravação local, conectada pelo obs-websocket.",
    // Same source of truth as the Lives page: only a configured download link makes it "available".
    status: brand.companionDownloadUrl ? "Disponível" : "Em desenvolvimento",
    tone: brand.companionDownloadUrl ? "success" : "neutral",
    action: { href: "/live", label: "Ver captura ao vivo" },
  },
];

function IntegrationsTab() {
  return (
    <Card>
      <CardHeader>
        <CardTitle>Integrações</CardTitle>
        <CardDescription>
          Nenhuma conta de plataforma é conectada. A fonte de um projeto é sempre um arquivo enviado por você ou a sua gravação local
          (companion).
        </CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4 md:grid-cols-3">
        {INTEGRATIONS.map((integration) => (
          <div key={integration.name} className="flex flex-col rounded-md border border-line bg-canvas/40 p-4">
            <div className="flex items-center justify-between gap-2">
              <p className="font-medium">{integration.name}</p>
              <Badge tone={integration.tone}>{integration.status}</Badge>
            </div>
            <p className="mt-2 flex-1 text-sm text-fg-muted">{integration.description}</p>
            <Link href={integration.action.href} className={cn(buttonVariants({ variant: "secondary", size: "sm" }), "mt-3 self-start")}>
              {integration.action.label}
            </Link>
          </div>
        ))}
      </CardContent>
    </Card>
  );
}

const NOTIFICATION_OPTIONS: Array<{ key: keyof NotificationPreferences; label: string; description: string }> = [
  { key: "emailOnJobCompleted", label: "Análise concluída", description: "Quando a análise de uma gravação termina e os candidatos estão prontos." },
  { key: "emailOnJobFailed", label: "Falha no processamento", description: "Quando uma análise ou renderização falha e precisa da sua atenção." },
  { key: "emailOnQuotaWarning", label: "Avisos de cota", description: "Ao atingir 80% e 100% dos minutos de processamento do período." },
  { key: "emailOnBilling", label: "Assinatura e pagamentos", description: "Renovações, falhas de pagamento e mudanças de plano." },
];

export function NotificationsTab({ initial }: { initial: Partial<NotificationPreferences> | null | undefined }) {
  const update = useUpdateProfile();
  const [prefs, setPrefs] = useState<NotificationPreferences>(() => normalizeNotificationPreferences(initial));

  const save = () => {
    update.mutate(
      { notificationPreferences: prefs },
      { onSuccess: () => toast.success("Notificações atualizadas"), onError: (error) => toast.error(describeError(error)) },
    );
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Notificações</CardTitle>
        <CardDescription>Escolha quais avisos também chegam por e-mail.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <Alert variant="info" title="Avisos dentro do produto ficam sempre ativos">
          Tudo o que está abaixo continua aparecendo no sino de notificações, com ou sem e-mail. Estas opções controlam apenas o envio por
          e-mail.
        </Alert>
        <fieldset>
          <legend className="text-sm font-medium">Enviar por e-mail</legend>
          <ul className="mt-1 divide-y divide-line">
            {NOTIFICATION_OPTIONS.map((option) => (
              <li key={option.key} className="flex items-center justify-between gap-4 py-3">
                <div>
                  <Label htmlFor={`notify-${option.key}`}>{option.label}</Label>
                  <p id={`notify-${option.key}-hint`} className="mt-0.5 text-xs text-fg-muted">
                    {option.description}
                  </p>
                </div>
                <Switch
                  id={`notify-${option.key}`}
                  aria-describedby={`notify-${option.key}-hint`}
                  checked={prefs[option.key]}
                  onCheckedChange={(checked) => setPrefs((current) => ({ ...current, [option.key]: checked }))}
                />
              </li>
            ))}
          </ul>
        </fieldset>
        <Button onClick={save} loading={update.isPending}>
          Salvar notificações
        </Button>
      </CardContent>
    </Card>
  );
}

function DeleteAccountTab() {
  const router = useRouter();
  const remove = useDeleteAccount();
  const logout = useLogout();
  const [formError, setFormError] = useState<string | null>(null);
  const form = useForm<DeleteAccountInput>({
    resolver: zodResolver(deleteAccountSchema),
    defaultValues: { password: "", confirmation: false },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    remove.mutate(
      { password: values.password },
      {
        onSuccess: () => {
          // The API already revoked every session when it scheduled the deletion; this clears the cookies.
          toast.success("Exclusão agendada. Sua sessão foi encerrada.");
          logout.mutate(undefined, { onSettled: () => router.replace("/") });
        },
        onError: (error) => setFormError(applyApiErrors(error, form.setError, ["password"])),
      },
    );
  });

  return (
    <Card className="border-danger/30">
      <CardHeader>
        <CardTitle className="text-danger">Excluir conta</CardTitle>
        <CardDescription>Esta ação é definitiva.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <Alert variant="warning" title="O que acontece ao excluir">
          <ul className="list-disc space-y-1 pl-4">
            <li>Suas sessões são encerradas imediatamente e todos os projetos são marcados para remoção.</li>
            <li>Gravações, transcrições, clipes e miniaturas são apagados do armazenamento, e os dados pessoais são removidos definitivamente.</li>
            <li>
              Registros de pagamento e de consumo são mantidos apenas de forma anonimizada pelo prazo exigido por lei, conforme a LGPD (Lei nº
              13.709/2018) e a nossa{" "}
              <Link href="/privacy" className="text-accent underline-offset-4 hover:underline">
                Política de privacidade
              </Link>
              .
            </li>
          </ul>
        </Alert>
        <form onSubmit={onSubmit} noValidate className="max-w-md space-y-4">
          <FormError message={formError} />
          <FormField id="delete-password" label="Confirme com a sua senha" error={errors.password?.message} required>
            <Input
              id="delete-password"
              type="password"
              autoComplete="current-password"
              invalid={!!errors.password}
              aria-describedby={fieldDescribedBy("delete-password", !!errors.password, false)}
              {...form.register("password")}
            />
          </FormField>
          <Controller
            control={form.control}
            name="confirmation"
            render={({ field }) => (
              <FormField id="delete-confirmation" inline label="Entendo que todos os meus dados e arquivos serão apagados e que isso não pode ser desfeito." error={errors.confirmation?.message}>
                <Checkbox
                  id="delete-confirmation"
                  checked={field.value}
                  onCheckedChange={(checked) => field.onChange(checked === true)}
                  aria-invalid={!!errors.confirmation}
                  aria-describedby={fieldDescribedBy("delete-confirmation", !!errors.confirmation, false)}
                />
              </FormField>
            )}
          />
          <Button type="submit" variant="danger" loading={remove.isPending} leftIcon={<Trash2 aria-hidden="true" />}>
            Excluir minha conta
          </Button>
        </form>
      </CardContent>
    </Card>
  );
}

function ProfileState({ query, children }: { query: ReturnType<typeof useProfile>; children: (profile: Profile) => React.ReactNode }) {
  if (query.isPending) return <CardSkeleton />;
  if (query.isError) {
    return (
      <Alert
        variant="danger"
        action={
          <Button size="sm" variant="secondary" onClick={() => query.refetch()} loading={query.isFetching}>
            Tentar novamente
          </Button>
        }
      >
        {describeError(query.error)}
      </Alert>
    );
  }
  return <>{children(query.data)}</>;
}

export function SettingsPage() {
  const router = useRouter();
  const pathname = usePathname();
  const searchParams = useSearchParams();
  const tabParam = searchParams.get("tab");
  const tab: TabValue = TABS.some((item) => item.value === tabParam) ? (tabParam as TabValue) : "perfil";
  const profile = useProfile();
  // Plan limits decide which subtitle styles the editing preferences offer.
  const me = useMe();

  const setTab = (value: string) => {
    router.replace(value === "perfil" ? pathname : `${pathname}?tab=${value}`, { scroll: false });
  };

  return (
    <>
      <PageHeader title="Configurações" description="Perfil, segurança, preferências e integrações." />
      <Tabs value={tab} onValueChange={setTab}>
        <TabsList aria-label="Seções das configurações" className="h-auto flex-wrap">
          {TABS.map((item) => {
            const Icon = item.icon;
            return (
              <TabsTrigger key={item.value} value={item.value}>
                <Icon aria-hidden="true" />
                {item.label}
              </TabsTrigger>
            );
          })}
        </TabsList>
        <TabsContent value="perfil">
          <ProfileState query={profile}>{(data) => <ProfileTab profile={data} />}</ProfileState>
        </TabsContent>
        <TabsContent value="seguranca">
          <SecurityTab />
        </TabsContent>
        <TabsContent value="edicao">
          <ProfileState query={profile}>
            {(data) => (
              <EditingTab key={JSON.stringify(data.editingPreferences ?? null)} initial={data.editingPreferences} limits={me.data?.limits} />
            )}
          </ProfileState>
        </TabsContent>
        <TabsContent value="integracoes">
          <IntegrationsTab />
        </TabsContent>
        <TabsContent value="notificacoes">
          <ProfileState query={profile}>
            {(data) => <NotificationsTab key={JSON.stringify(data.notificationPreferences ?? null)} initial={data.notificationPreferences} />}
          </ProfileState>
        </TabsContent>
        <TabsContent value="conta">
          <DeleteAccountTab />
        </TabsContent>
      </Tabs>
    </>
  );
}
