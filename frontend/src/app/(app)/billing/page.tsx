import type { Metadata } from "next";
import { BillingPage } from "@/features/billing/billing-page";

export const metadata: Metadata = { title: "Assinatura" };

export default function Page() {
  return <BillingPage />;
}
