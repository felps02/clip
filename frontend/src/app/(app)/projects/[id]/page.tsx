import type { Metadata } from "next";
import { ProjectDetailPage } from "@/features/projects/project-detail-page";

export const metadata: Metadata = { title: "Projeto" };

export default function Page() {
  return <ProjectDetailPage />;
}
