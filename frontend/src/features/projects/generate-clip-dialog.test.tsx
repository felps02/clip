import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { settingsForProject } from "@/features/projects/analysis-settings";
import { GenerateClipDialog } from "@/features/projects/generate-clip-dialog";
import type { Candidate, PlanLimits } from "@/lib/api/types";

const freeLimits: PlanLimits = {
  monthlyProcessingMinutes: 60,
  maxSourceDurationMinutes: 120,
  maxFileSizeMb: 2048,
  maxActiveProjects: 3,
  maxCandidatesPerJob: 15,
  maxAutoRenderPerJob: 3,
  maxRendersPerMonth: 10,
  maxRenderHeight: 720,
  maxConcurrentJobs: 1,
  priority: 0,
  retentionDays: 7,
  storageQuotaGb: 5,
  transcriptionModel: "base",
  allowWebcamLayout: false,
  allowCustomSubtitleStyles: false,
};

const candidate: Candidate = {
  id: "cand-1",
  rank: 1,
  startSeconds: 120,
  endSeconds: 150,
  durationSeconds: 30,
  category: "highlight",
  score: 82,
  status: "detected",
  previewable: false,
};

const fetchMock = vi.fn<typeof fetch>();
let postBodies: unknown[];

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function renderDialog(limits: PlanLimits) {
  // The project's stored settings ask for a boxed subtitle at the top; the defaults are capped by the plan.
  const defaults = settingsForProject({ subtitles: { enabled: true, style: "boxed", position: "top" } }, null, limits);
  return render(
    <GenerateClipDialog projectId="p1" projectTitle="Live de sexta" candidate={candidate} onOpenChange={() => undefined} defaults={defaults} limits={limits} />,
    { wrapper },
  );
}

beforeEach(() => {
  postBodies = [];
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    if (urlOf(input).endsWith("/api/projects/p1/candidates/cand-1/clips") && init?.method === "POST") {
      postBodies.push(JSON.parse(String(init.body)));
      return Promise.resolve(
        new Response(JSON.stringify({ clip: { id: "clip-1" }, job: { id: "job-1" } }), { status: 202, headers: { "content-type": "application/json" } }),
      );
    }
    return Promise.resolve(new Response(JSON.stringify({ title: "not found", status: 404 }), { status: 404 }));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("GenerateClipDialog plan gating", () => {
  it("offers only the bottom position on plans without custom styles and sends what the API will render", async () => {
    const user = userEvent.setup();
    renderDialog(freeLimits);

    const position = screen.getByLabelText("Posição");
    expect(position).toHaveValue("bottom");
    expect(within(position).getByRole("option", { name: "Em cima (indisponível no seu plano)" })).toBeDisabled();
    expect(within(position).getByRole("option", { name: "Centro (indisponível no seu plano)" })).toBeDisabled();
    expect(within(position).getByRole("option", { name: "Embaixo" })).toBeEnabled();
    expect(position).toHaveAccessibleDescription(/na parte de baixo do quadro/);
    expect(screen.getByLabelText("Estilo")).toHaveValue("bold");
    expect(screen.getByRole("img", { name: "Prévia da legenda no estilo Destaque, posição embaixo" })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Gerar clipe" }));

    await waitFor(() => expect(postBodies).toHaveLength(1));
    expect(postBodies[0]).toMatchObject({ subtitles: { enabled: true, style: "bold", position: "bottom" } });
  });

  it("keeps the project's position and offers every option when the plan allows custom styles", () => {
    renderDialog({ ...freeLimits, allowCustomSubtitleStyles: true });

    const position = screen.getByLabelText("Posição");
    expect(position).toHaveValue("top");
    expect(within(position).getByRole("option", { name: "Centro" })).toBeEnabled();
    expect(position).not.toHaveAccessibleDescription();
    expect(screen.queryByText(/não estão incluídos no seu plano/)).not.toBeInTheDocument();
  });
});
