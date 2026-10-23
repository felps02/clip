import type { Metadata } from "next";
import { ClipEditorPage } from "@/features/clips/clip-editor-page";

export const metadata: Metadata = { title: "Editor de clipe" };

export default function Page() {
  return <ClipEditorPage />;
}
