import type { Metadata } from "next";
import { VerifyEmailPanel } from "@/features/auth/verify-email-panel";

export const metadata: Metadata = { title: "Confirmar e-mail" };

export default function VerifyEmailPage() {
  return <VerifyEmailPanel />;
}
