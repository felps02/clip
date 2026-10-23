import type { Metadata } from "next";
import { ProjectsPage } from "@/features/projects/projects-page";

export const metadata: Metadata = { title: "Projetos" };

export default function Page() {
  return <ProjectsPage />;
}
