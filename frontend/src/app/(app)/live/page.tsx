import type { Metadata } from "next";
import { LivePage } from "@/features/live/live-page";

export const metadata: Metadata = { title: "Lives" };

export default function Page() {
  return <LivePage />;
}
