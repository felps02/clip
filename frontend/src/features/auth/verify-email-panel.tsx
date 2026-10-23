"use client";

import { CheckCircle2, MailWarning } from "lucide-react";
import Link from "next/link";
import { useSearchParams } from "next/navigation";
import { Alert } from "@/components/ui/alert";
import { Button, buttonVariants } from "@/components/ui/button";
import { AuthCard } from "@/features/auth/auth-card";
import { describeError } from "@/lib/api/client";
import { useVerifyEmail } from "@/lib/api/hooks/use-auth";

/**
 * Confirmation is triggered by an explicit click: e-mail scanners prefetch links and
 * would otherwise consume single-use tokens before the person opens the page.
 */
export function VerifyEmailPanel() {
  const searchParams = useSearchParams();
  const userId = searchParams.get("userId") ?? "";
  const token = searchParams.get("token") ?? "";
  const verify = useVerifyEmail();

  if (!userId || !token) {
    return (
      <AuthCard title="Link inválido">
        <Alert variant="warning">Este link de confirmação está incompleto. Abra o link exatamente como veio no e-mail.</Alert>
        <Link href="/login" className={buttonVariants({ variant: "secondary", className: "mt-6 w-full" })}>
          Ir para o login
        </Link>
      </AuthCard>
    );
  }

  if (verify.isSuccess) {
    return (
      <AuthCard title="E-mail confirmado">
        <div className="flex flex-col items-center text-center">
          <div className="flex size-12 items-center justify-center rounded-full bg-success/15 text-success">
            <CheckCircle2 className="size-6" aria-hidden="true" />
          </div>
          <p className="mt-4 text-sm text-fg-muted">Sua conta está ativa. Você já pode entrar.</p>
          <Link href="/login" className={buttonVariants({ className: "mt-6" })}>
            Entrar
          </Link>
        </div>
      </AuthCard>
    );
  }

  return (
    <AuthCard title="Confirmar e-mail" description="Clique no botão abaixo para ativar a sua conta.">
      {verify.isError ? (
        <Alert variant="danger" className="mb-4" title="Não foi possível confirmar">
          {describeError(verify.error, "O link pode ter expirado. Faça login para solicitar um novo e-mail.")}
        </Alert>
      ) : null}
      <div className="flex flex-col items-center text-center">
        <div className="flex size-12 items-center justify-center rounded-full bg-accent/15 text-accent">
          <MailWarning className="size-6" aria-hidden="true" />
        </div>
        <Button
          className="mt-6 w-full"
          size="lg"
          loading={verify.isPending}
          onClick={() => verify.mutate({ userId, token })}
        >
          Confirmar meu e-mail
        </Button>
      </div>
    </AuthCard>
  );
}
