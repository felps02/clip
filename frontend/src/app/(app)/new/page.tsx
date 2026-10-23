import type { Metadata } from "next";
import { NewProjectPage } from "@/features/new-project/new-project-page";

export const metadata: Metadata = { title: "Novo projeto" };

export default function Page() {
  return <NewProjectPage />;
}
