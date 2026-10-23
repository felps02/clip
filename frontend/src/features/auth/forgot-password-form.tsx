"use client";

import { zodResolver } from "@hookform/resolvers/zod";
import { MailCheck } from "lucide-react";
import Link from "next/link";
import { useState } from "react";
import { useForm } from "react-hook-form";
import { FormError } from "@/components/forms/form-error";
import { applyApiErrors } from "@/components/forms/form-utils";
import { Button, buttonVariants } from "@/components/ui/button";
import { fieldDescribedBy, FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { AuthCard } from "@/features/auth/auth-card";
import { useForgotPassword } from "@/lib/api/hooks/use-auth";
import { forgotPasswordSchema, type ForgotPasswordInput } from "@/lib/validation";

export function ForgotPasswordForm() {
  const forgot = useForgotPassword();
  const [formError, setFormError] = useState<string | null>(null);
  const [sent, setSent] = useState(false);

  const form = useForm<ForgotPasswordInput>({
    resolver: zodResolver(forgotPasswordSchema),
    defaultValues: { email: "" },
  });
  const { errors } = form.formState;

  const onSubmit = form.handleSubmit((values) => {
    setFormError(null);
    forgot.mutate(values, {
      onSuccess: () => setSent(true),
      onError: (error) => setFormError(applyApiErrors(error, form.setError, ["email"])),
    });
  });

  if (sent) {
    return (
      <AuthCard title="Verifique o seu e-mail">
        <div className="flex flex-col items-center text-center">
          <div className="flex size-12 items-center justify-center rounded-full bg-accent/15 text-accent">
            <MailCheck className="size-6" aria-hidden="true" />
          </div>
          <p className="mt-4 text-sm text-fg-muted">
            Se existir uma conta com este e-mail, enviaremos um link para redefinir a senha. O link expira em pouco tempo.
          </p>
          <Link href="/login" className={buttonVariants({ variant: "secondary", className: "mt-6" })}>
            Voltar ao login
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard
      title="Recuperar senha"
      description="Informe o e-mail da sua conta e enviaremos as instruções."
      footer={
        <Link href="/login" className="font-medium text-accent hover:underline">
          Voltar ao login
        </Link>
      }
    >
      <form onSubmit={onSubmit} noValidate className="space-y-4">
        <FormError message={formError} />
        <FormField id="email" label="E-mail" error={errors.email?.message} required>
          <Input
            id="email"
            type="email"
            autoComplete="email"
            inputMode="email"
            invalid={!!errors.email}
            aria-describedby={fieldDescribedBy("email", !!errors.email, false)}
            {...form.register("email")}
          />
        </FormField>
        <Button type="submit" className="w-full" size="lg" loading={forgot.isPending}>
          Enviar instruções
        </Button>
      </form>
    </AuthCard>
  );
}
