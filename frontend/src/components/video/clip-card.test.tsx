import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { ClipCard } from "@/components/video/clip-card";
import type { ClipSummary } from "@/lib/api/types";

const clip: ClipSummary = {
  id: "clip-1",
  projectId: "project-1",
  projectTitle: "Live de sábado",
  title: "Clutch no último round",
  category: "reaction",
  score: 78,
  status: "ready",
  aspectRatio: "9:16",
  durationSeconds: 32,
  startSeconds: 120,
  endSeconds: 152,
  thumbnailUrl: "https://storage.test/thumb.jpg?sig=abc",
  createdAt: "2026-10-20T15:30:00Z",
  updatedAt: "2026-10-20T15:35:00Z",
  currentRender: { id: "r1", version: 1, width: 1080, height: 1920, sizeBytes: 1024, status: "ready" },
};

describe("ClipCard", () => {
  it("renders title, thumbnail, duration overlay, category, score, status and date", () => {
    render(<ClipCard clip={clip} showProject />);

    expect(screen.getByRole("heading", { name: "Clutch no último round" })).toBeInTheDocument();
    expect(screen.getByRole("link", { name: "Clutch no último round" })).toHaveAttribute("href", "/clips/clip-1");
    expect(screen.getByText("00:32")).toBeInTheDocument();
    expect(screen.getByText("9:16")).toBeInTheDocument();
    expect(screen.getByText("reação")).toBeInTheDocument();
    expect(screen.getByText("Pronto")).toBeInTheDocument();
    expect(screen.getByText(/relevância estimada/i)).toBeInTheDocument();
    expect(screen.getByText("78")).toBeInTheDocument();
    expect(screen.getByText("Live de sábado")).toBeInTheDocument();
    expect(screen.getByText("20/10/2026")).toBeInTheDocument();
    const image = document.querySelector("img");
    expect(image).not.toBeNull();
    expect(image?.getAttribute("src")).toContain("storage.test/thumb.jpg");
  });

  it("shows a status badge and no preview button for clips that are still rendering", () => {
    const onPreview = vi.fn();
    render(<ClipCard clip={{ ...clip, status: "rendering", thumbnailUrl: null }} onPreview={onPreview} />);

    expect(screen.getByText("Renderizando")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /pré-visualizar/i })).not.toBeInTheDocument();
    expect(document.querySelector("img")).toBeNull();
  });

  it("calls onPreview when the play overlay is clicked", async () => {
    const onPreview = vi.fn();
    render(<ClipCard clip={clip} onPreview={onPreview} />);

    await userEvent.click(screen.getByRole("button", { name: "Pré-visualizar Clutch no último round" }));

    expect(onPreview).toHaveBeenCalledWith(clip);
  });
});
