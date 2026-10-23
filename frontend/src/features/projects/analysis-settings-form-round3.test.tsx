import { fireEvent, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { applyPlanLimits, buildDefaultSettings } from "@/features/projects/analysis-settings";
import { AnalysisSettingsForm } from "@/features/projects/analysis-settings-form";
import type { AnalysisSettings, PlanLimits } from "@/lib/api/types";

const limits: PlanLimits = {
  monthlyProcessingMinutes: 60,
  maxSourceDurationMinutes: 120,
  maxFileSizeMb: 2048,
  maxActiveProjects: 3,
  maxCandidatesPerJob: 15,
  maxAutoRenderPerJob: 15,
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

function Harness({ initial, planLimits = limits }: { initial?: AnalysisSettings; planLimits?: PlanLimits }) {
  const [settings, setSettings] = useState(initial ?? buildDefaultSettings(null, planLimits));
  return (
    <>
      <AnalysisSettingsForm value={settings} onChange={(next) => setSettings(applyPlanLimits(next, planLimits))} limits={planLimits} />
      <output data-testid="settings">{JSON.stringify(settings)}</output>
    </>
  );
}

const current = (): AnalysisSettings => JSON.parse(screen.getByTestId("settings").textContent ?? "{}") as AnalysisSettings;

describe("AnalysisSettingsForm (round 3)", () => {
  it("defaults the automatic clips to every moment found, capped by the plan", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    const autoRender = screen.getByLabelText("Clipes prontos automaticamente");
    expect(autoRender).toHaveValue("all");
    expect(within(autoRender).getByRole("option", { name: "Todos os momentos encontrados" })).toBeInTheDocument();
    expect(current().autoRenderTop).toBe(15);

    // "Todos" follows the maximum of moments.
    fireEvent.change(screen.getByLabelText("Máximo de momentos"), { target: { value: "8" } });
    expect(current()).toMatchObject({ maxCandidates: 8, autoRenderTop: 8 });

    await user.selectOptions(autoRender, "top");
    expect(current().autoRenderTop).toBe(5);
    expect(screen.getByLabelText("Quantos")).toHaveValue(5);
    await user.selectOptions(autoRender, "none");
    expect(current().autoRenderTop).toBe(0);
  });

  it("says when the plan renders fewer clips than the moments found", () => {
    render(<Harness planLimits={{ ...limits, maxAutoRenderPerJob: 3 }} />);
    expect(screen.getByRole("option", { name: "Todos os momentos encontrados (até 3 pelo seu plano)" })).toBeInTheDocument();
  });

  it("toggles focus categories as chips", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    const focus = screen.getByRole("group", { name: "Foco da análise" });
    await user.click(within(focus).getByRole("button", { name: "Frases" }));
    await user.click(within(focus).getByRole("button", { name: "Engraçados" }));
    expect(current().focusCategories).toEqual(["funny", "quote"]);
    expect(within(focus).getByRole("button", { name: "Engraçados" })).toHaveAttribute("aria-pressed", "true");
    await user.click(within(focus).getByRole("button", { name: "Frases" }));
    expect(current().focusCategories).toEqual(["funny"]);
  });

  it("adds custom keywords as tags, refusing duplicates and long words", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    const input = screen.getByLabelText("Palavras-chave");
    await user.type(input, "clutch{Enter}que isso,");
    expect(current().customKeywords).toEqual(["clutch", "que isso"]);
    await user.type(input, "CLUTCH{Enter}");
    expect(screen.getByRole("alert")).toHaveTextContent("Essa palavra-chave já está na lista.");
    await user.clear(input);
    await user.type(input, `${"x".repeat(41)}{Enter}`);
    expect(screen.getByRole("alert")).toHaveTextContent("Use até 40 caracteres por palavra-chave.");
    expect(current().customKeywords).toEqual(["clutch", "que isso"]);

    await user.click(screen.getByRole("button", { name: "Remover palavra-chave clutch" }));
    expect(current().customKeywords).toEqual(["que isso"]);
  });

  it("keeps the request in the person's own words (instructions)", async () => {
    const user = userEvent.setup();
    render(<Harness />);

    const instructions = screen.getByLabelText("Pedido em suas palavras (opcional)");
    await user.type(instructions, "só risadas ");
    expect(current().instructions).toBe("só risadas ");
    expect(instructions).toHaveAttribute("maxLength", "500");
  });

  it("labels the subtitle preview and keeps the three positions on one row each", () => {
    render(<Harness />);
    expect(screen.getByText("Prévia do estilo")).toBeInTheDocument();
    const positions = screen.getByRole("radiogroup", { name: "Posição da legenda" });
    expect(within(positions).getAllByRole("radio")).toHaveLength(3);
    expect(within(positions).getByText("Em cima")).toHaveClass("whitespace-nowrap");
  });

  it("lets a short recording use clip lengths below the usual 10 s and shows why", () => {
    const short = { ...buildDefaultSettings(null, limits), minClipSeconds: 7, targetClipSeconds: 7, maxClipSeconds: 7 };
    render(
      <AnalysisSettingsForm
        value={short}
        onChange={() => undefined}
        limits={limits}
        shortSourceSeconds={7.4}
        clipLengthNote={<p>nota do vídeo curto</p>}
      />,
    );
    const [minThumb, maxThumb] = screen.getAllByRole("slider");
    expect(minThumb).toHaveAttribute("aria-valuemin", "5");
    expect(minThumb).toHaveAttribute("aria-valuenow", "7");
    expect(maxThumb).toHaveAttribute("aria-valuemax", "7");
    expect(screen.getByText("nota do vídeo curto")).toBeInTheDocument();
  });
});
