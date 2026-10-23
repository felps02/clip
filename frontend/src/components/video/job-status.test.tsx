import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { JobStatus } from "@/components/video/job-status";
import type { Job } from "@/lib/api/types";

const job: Job = {
  id: "j1",
  projectId: "p1",
  kind: "analyze_source",
  status: "analyzing",
  progress: 37,
  statusMessage: "Transcrevendo o áudio",
  attempt: 1,
  queuedAt: "2026-10-23T10:00:00Z",
  startedAt: "2026-10-23T10:00:05Z",
  events: [{ id: "e1", createdAt: "2026-10-23T10:00:06Z", level: "warning", message: "Transcrição lenta" }],
};

describe("JobStatus", () => {
  it("announces only the status, not every poll update", () => {
    const { container, rerender } = render(<JobStatus job={job} />);

    const liveRegions = container.querySelectorAll("[aria-live]");
    expect(liveRegions).toHaveLength(1);
    expect(liveRegions[0]).toHaveTextContent("Análise da gravação: Analisando");
    expect(liveRegions[0]).not.toHaveTextContent("37");

    rerender(<JobStatus job={{ ...job, progress: 52, statusMessage: "Detectando picos" }} />);
    expect(container.querySelector("[aria-live]")).toHaveTextContent("Análise da gravação: Analisando");

    rerender(<JobStatus job={{ ...job, status: "completed", progress: 100 }} />);
    expect(container.querySelector("[aria-live]")).toHaveTextContent("Análise da gravação: Concluído");
    expect(screen.getByRole("progressbar", { name: "Progresso do trabalho" })).toHaveAttribute("aria-valuenow", "100");
  });
});
