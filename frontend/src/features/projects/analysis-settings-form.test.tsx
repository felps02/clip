import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it, vi } from "vitest";
import { buildDefaultSettings } from "@/features/projects/analysis-settings";
import { AnalysisSettingsForm } from "@/features/projects/analysis-settings-form";
import type { PlanLimits } from "@/lib/api/types";

const limits: PlanLimits = {
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

describe("AnalysisSettingsForm plan gating", () => {
  it("disables non-default subtitle styles and the webcam layout the plan lacks, and says why", () => {
    render(<AnalysisSettingsForm value={buildDefaultSettings({ subtitleStyle: "boxed" }, limits)} onChange={() => undefined} limits={limits} />);

    const styles = screen.getByRole("radiogroup", { name: "Estilo da legenda" });
    expect(within(styles).getByRole("radio", { name: "Destaque" })).toBeEnabled();
    expect(within(styles).getByRole("radio", { name: "Destaque" })).toHaveAttribute("aria-checked", "true");
    expect(within(styles).getByRole("radio", { name: /Limpo/ })).toBeDisabled();
    expect(within(styles).getByRole("radio", { name: /Caixa/ })).toBeDisabled();
    expect(screen.getByText(/não estão incluídos no seu plano/)).toBeInTheDocument();

    const framing = screen.getByLabelText("Enquadramento");
    expect(within(framing).getByRole("option", { name: "Webcam + gameplay (indisponível no seu plano)" })).toBeDisabled();
    expect(screen.getByText(/não está incluído no seu plano; use Automático ou Centralizado/)).toBeInTheDocument();
  });

  it("keeps subtitles at the bottom on plans without custom styles: other positions are disabled, also in the preview", async () => {
    const onChange = vi.fn();
    render(
      <AnalysisSettingsForm
        value={buildDefaultSettings({ subtitlePosition: "top" }, limits)}
        onChange={onChange}
        limits={limits}
      />,
    );

    const positions = screen.getByRole("radiogroup", { name: "Posição da legenda" });
    expect(within(positions).getByRole("radio", { name: "Embaixo" })).toHaveAttribute("aria-checked", "true");
    expect(within(positions).getByRole("radio", { name: "Embaixo" })).toBeEnabled();
    for (const label of [/^Em cima/, /^Centro/]) {
      const option = within(positions).getByRole("radio", { name: label });
      expect(option).toBeDisabled();
      expect(option).toHaveTextContent("indisponível no seu plano");
    }
    expect(
      screen.getByText(
        "Os estilos Limpo e Caixa e as posições Em cima e Centro não estão incluídos no seu plano; as legendas usam o estilo Destaque, na parte de baixo do quadro.",
        { exact: false },
      ),
    ).toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Prévia da legenda no estilo Destaque, posição embaixo" })).toBeInTheDocument();

    await userEvent.setup().click(within(positions).getByRole("radio", { name: /Em cima/ }));
    expect(onChange).not.toHaveBeenCalled();
  });

  it("offers everything when the plan allows it", () => {
    const paid = { ...limits, allowWebcamLayout: true, allowCustomSubtitleStyles: true };
    render(<AnalysisSettingsForm value={buildDefaultSettings(null, paid)} onChange={() => undefined} limits={paid} />);

    expect(screen.getByRole("radio", { name: "Caixa" })).toBeEnabled();
    expect(screen.getByRole("radio", { name: "Em cima" })).toBeEnabled();
    expect(screen.getByRole("radio", { name: "Centro" })).toBeEnabled();
    expect(screen.queryByText(/não estão incluídos no seu plano/)).not.toBeInTheDocument();
    expect(within(screen.getByLabelText("Enquadramento")).getByRole("option", { name: "Webcam + gameplay" })).toBeEnabled();
  });
});
