import type { Metadata } from "next";
import { LiveSessionPage } from "@/features/live/live-session-page";

export const metadata: Metadata = { title: "Sessão ao vivo" };

export default function Page() {
  return <LiveSessionPage />;
}
