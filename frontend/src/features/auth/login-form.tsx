"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import Link from "next/link";
import { useRouter, useSearchParams } from "next/navigation";
import { useEffect, useState } from "react";
import { useForm } from "react-hook-form";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Button } from "@/components/ui/button";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { AuthCard } from "@/features/auth/auth-card";
import { HANDOFF_WATCHDOG_MS, handOffToApp, SessionHandoffAlert } from "@/features/auth/session-handoff";
import { useRestoreSession } from "@/features/auth/use-restore-session";
import { useLogin } from "@/lib/api/hooks/use-auth";
import { loginSchema, type LoginInput } from "@/lib/validation";
import { safeNextPath } from "@/lib/utils";

export function LoginForm() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const next = safeNextPath(searchParams.get("next"));
  // "Sair" lands here with signedOut=1 once the API confirmed the cookies are cleared: nothing to restore.
  const signedOut = searchParams.get("signedOut") === "1";
  // A valid refresh cookie restores the session without credentials (the access cookie only lives 15 min).
  const restore = useRestoreSession(next, { skip: signedOut });
  const login = useLogin();
  const [formError, setFormError] = useState<string | null>(null);
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

  const form = useForm<LoginInput>({
    resolver: zodResolver(loginSchema),
    defaultValues: { email: "", password: "" },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    setHandoffBlocked(false);
    login.mutate(values, {
      onSuccess: () => {
        setHandingOff(true);
        void handOffToApp(next, router).then((outcome) => {
          if (outcome !== "blocked") return;
          setHandingOff(false);
          setHandoffBlocked(true);
        });
      },
      onError: (error) => {
        setFormError(applyApiErrors(error, form.setError, ["email", "password"]));
      },
    });
  });

  return (
    <AuthCard
      title="Entrar"
      description="Acesse sua conta para continuar."
      footer={
        <>
          Ainda não tem conta?{" "}
          <Link href="/register" className="font-medium text-accent hover:underline">
            Criar conta
          </Link>
        </>
      }
    >
      <form onSubmit={onSubmit} noValidate className="space-y-4">
        {restore === "blocked" || handoffBlocked ? <SessionHandoffAlert /> : null}
        {restore === "checking" || restore === "restored" ? (
          <p role="status" className="text-xs text-fg-subtle">
            {restore === "restored" ? "Sessão restaurada. Redirecionando…" : "Verificando se você já está conectado…"}
          </p>
        ) : signedOut ? (
          <p role="status" className="text-xs text-fg-subtle">
            Você saiu da sua conta neste navegador.
          </p>
        ) : null}
        <FormError message={formError} />
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
        <FormField id="password" label="Senha" error={errors.password?.message} required>
          <Input
            id="password"
            type="password"
            autoComplete="current-password"
            invalid={!!errors.password}
            aria-describedby={fieldDescribedBy("password", !!errors.password, false)}
            {...form.register("password")}
          />
        </FormField>
        <div className="flex items-center justify-end">
          <Link href="/forgot-password" className="text-sm text-fg-muted hover:text-fg">
            Esqueci minha senha
          </Link>
        </div>
        <Button
          type="submit"
          className="w-full"
          size="lg"
          loading={login.isPending || handingOff}
          disabled={restore === "checking" || restore === "restored"}
        >
          Entrar
        </Button>
      </form>
    </AuthCard>
  );
}
