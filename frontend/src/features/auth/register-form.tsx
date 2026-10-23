"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { MailCheck } from "lucide-react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { useEffect, useState } from "react";
import { Controller, useForm } from "react-hook-form";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Button, buttonVariants } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { AuthCard } from "@/features/auth/auth-card";
import { HANDOFF_WATCHDOG_MS, handOffToApp, SessionHandoffAlert } from "@/features/auth/session-handoff";
import { useRestoreSession } from "@/features/auth/use-restore-session";
import { useRegister } from "@/lib/api/hooks/use-auth";
import { registerSchema, type RegisterInput } from "@/lib/validation";

export function RegisterForm() {
  const router = useRouter();
  // Someone already signed in lands in the app instead of on the sign-up form.
  const restore = useRestoreSession("/dashboard");
  const register = useRegister();
  const [formError, setFormError] = useState<string | null>(null);
  const [confirmationMessage, setConfirmationMessage] = useState<string | null>(null);
  const [handingOff, setHandingOff] = useState(false);
  const [handoffBlocked, setHandoffBlocked] = useState(false);
  // Still on this page long after handing off: the navigation was bounced back (see HANDOFF_WATCHDOG_MS).
  useEffect(() => {
    if (!handingOff) return;
    const timer = window.setTimeout(() => {
      setHandingOff(false);
      setHandoffBlocked(true);
    }, HANDOFF_WATCHDOG_MS);
    return () => window.clearTimeout(timer);
  }, [handingOff]);

  const form = useForm<RegisterInput>({
    resolver: zodResolver(registerSchema),
    defaultValues: { displayName: "", email: "", password: "", acceptedTerms: false },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    register.mutate(
      { email: values.email, password: values.password, displayName: values.displayName, acceptedTerms: true },
      {
        onSuccess: (result) => {
          if (result.status === 202) {
            setConfirmationMessage(
              result.message || "Enviamos um e-mail de confirmação. Abra o link para ativar a sua conta.",
            );
            return;
          }
          setHandingOff(true);
          void handOffToApp("/dashboard", router).then((outcome) => {
            if (outcome !== "blocked") return;
            setHandingOff(false);
            setHandoffBlocked(true);
          });
        },
        onError: (error) => {
          setFormError(
            applyApiErrors(error, form.setError, ["displayName", "email", "password", "acceptedTerms"], {
              terms: "acceptedTerms",
            }),
          );
        },
      },
    );
  });

  if (confirmationMessage) {
    return (
      <AuthCard title="Confirme o seu e-mail">
        <div className="flex flex-col items-center text-center">
          <div className="flex size-12 items-center justify-center rounded-full bg-accent/15 text-accent">
            <MailCheck className="size-6" aria-hidden="true" />
          </div>
          <p className="mt-4 text-sm text-fg-muted">{confirmationMessage}</p>
          <Link href="/login" className={buttonVariants({ variant: "secondary", className: "mt-6" })}>
            Ir para o login
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title="Criar conta"
      description="Comece no plano gratuito, sem cartão de crédito."
      footer={
        <>
          Já tem conta?{" "}
          <Link href="/login" className="font-medium text-accent hover:underline">
            Entrar
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate className="space-y-4">
        {restore === "blocked" || handoffBlocked ? <SessionHandoffAlert /> : null}
        {restore === "checking" || restore === "restored" ? (
          <p role="status" className="text-xs text-fg-subtle">
            {restore === "restored" ? "Você já está conectado. Redirecionando…" : "Verificando se você já está conectado…"}
          </p>
        ) : null}
        <FormError message={formError} />
        <FormField id="displayName" label="Nome de exibição" error={errors.displayName?.message} required>
          <Input
            id="displayName"
            autoComplete="nickname"
            placeholder="Como quer ser chamado"
            invalid={!!errors.displayName}
            aria-describedby={fieldDescribedBy("displayName", !!errors.displayName, false)}
            {...form.register("displayName")}
          />
        </FormField>
        <FormField id="email" label="E-mail" error={errors.email?.message} required>
          <Input
            id="email"
            type="email"
            autoComplete="email"
            inputMode="email"
            placeholder="voce@exemplo.com"
            invalid={!!errors.email}
            aria-describedby={fieldDescribedBy("email", !!errors.email, false)}
            {...form.register("email")}
          />
        </FormField>
        <FormField
          id="password"
          label="Senha"
          hint="Mínimo de 8 caracteres, com pelo menos uma letra e um número."
          error={errors.password?.message}
          required
        >
          <Input
            id="password"
            type="password"
            autoComplete="new-password"
            invalid={!!errors.password}
            aria-describedby={fieldDescribedBy("password", !!errors.password, true)}
            {...form.register("password")}
          />
        </FormField>
        <Controller
          control={form.control}
          name="acceptedTerms"
          render={({ field }) => (
            <FormField
              id="acceptedTerms"
              inline
              error={errors.acceptedTerms?.message}
              label={
                <span>
                  Li e aceito os{" "}
                  <Link href="/terms" target="_blank" className="text-accent hover:underline">
                    Termos de uso
                  </Link>{" "}
                  e a{" "}
                  <Link href="/privacy" target="_blank" className="text-accent hover:underline">
                    Política de privacidade
                  </Link>
                  , e confirmo que tenho o direito de processar o conteúdo que enviar (incluindo imagem e voz de quem aparece nele).
                </span>
              }
            >
              <Checkbox
                id="acceptedTerms"
                checked={field.value}
                onCheckedChange={(checked) => field.onChange(checked === true)}
                onBlur={field.onBlur}
                aria-invalid={!!errors.acceptedTerms}
                aria-describedby={fieldDescribedBy("acceptedTerms", !!errors.acceptedTerms, false)}
              />
            </FormField>
          )}
        />
        <Button
          type="submit"
          className="w-full"
          size="lg"
          loading={register.isPending || handingOff}
          disabled={restore === "checking" || restore === "restored" || handoffBlocked}
        >
          Criar conta
        </Button>
      </form>
    </AuthCard>
  );
}
