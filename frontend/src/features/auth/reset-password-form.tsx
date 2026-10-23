"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { CheckCircle2 } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Alert } from "@/components/ui/alert";
import { Button, buttonVariants } from "@/components/ui/button";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { AuthCard } from "@/features/auth/auth-card";
import { useResetPassword } from "@/lib/api/hooks/use-auth";
import { resetPasswordSchema, type ResetPasswordInput } from "@/lib/validation";

export function ResetPasswordForm() {
  const searchParams = useSearchParams();
  const email = searchParams.get("email") ?? "";
  const token = searchParams.get("token") ?? "";
  const reset = useResetPassword();
  const [formError, setFormError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  const form = useForm<ResetPasswordInput>({
    resolver: zodResolver(resetPasswordSchema),
    defaultValues: { newPassword: "", confirmPassword: "" },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    reset.mutate(
      { email, token, newPassword: values.newPassword },
      {
        onSuccess: () => setDone(true),
        onError: (error) =>
          setFormError(applyApiErrors(error, form.setError, ["newPassword"], { password: "newPassword" })),
      },
    );
  });

  if (!email || !token) {
    return (
      <AuthCard title="Link inválido">
        <Alert variant="warning">
          Este link de redefinição está incompleto ou expirou. Solicite um novo link para continuar.
        </Alert>
        <Link href="/forgot-password" className={buttonVariants({ variant: "secondary", className: "mt-6 w-full" })}>
          Solicitar novo link
        </Link>
      </AuthCard>
    );
  }

  if (done) {
    return (
      <AuthCard title="Senha redefinida">
        <div className="flex flex-col items-center text-center">
          <div className="flex size-12 items-center justify-center rounded-full bg-success/15 text-success">
            <CheckCircle2 className="size-6" aria-hidden="true" />
          </div>
          <p className="mt-4 text-sm text-fg-muted">Sua senha foi alterada. Entre novamente com a nova senha.</p>
          <Link href="/login" className={buttonVariants({ className: "mt-6" })}>
            Ir para o login
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Definir nova senha" description={`Conta: ${email}`}>
      <form onSubmit={onSubmit} noValidate className="space-y-4">
        <FormError message={formError} />
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
        <Button type="submit" className="w-full" size="lg" loading={reset.isPending}>
          Salvar nova senha
        </Button>
      </form>
    </AuthCard>
  );
}
