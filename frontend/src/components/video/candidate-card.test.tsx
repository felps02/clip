import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { describe, expect, it } from "vitest";
import { TooltipProvider } from "@/components/ui/tooltip";
import { CandidateCard, SCORE_DISCLAIMER } from "@/components/video/candidate-card";
import type { Candidate } from "@/lib/api/types";

const candidate: Candidate = {
  id: "cand-1",
  rank: 1,
  startSeconds: 120,
  endSeconds: 152,
  durationSeconds: 32,
  category: "reaction",
  score: 87,
  reasoning: "Pico de áudio seguido de risadas.",
  transcript: "Não acredito que isso aconteceu!",
  signals: { contributors: ["audio.energy"] },
  status: "detected",
  previewable: false,
};

function renderCard() {
  return render(
    <TooltipProvider>
      <CandidateCard candidate={candidate} />
    </TooltipProvider>,
  );
}

describe("CandidateCard score", () => {
  it("exposes the score as a focusable control announced as 'N de 100' and always described by the disclaimer", () => {
    renderCard();

    const trigger = screen.getByRole("button", { name: "Relevância estimada 87 de 100" });
    expect(trigger).toHaveAttribute("type", "button");
    const describedBy = trigger.getAttribute("aria-describedby") ?? "";
    expect(describedBy).not.toBe("");
    expect(document.getElementById(describedBy)).toHaveTextContent(SCORE_DISCLAIMER);
    expect(document.querySelector("div[aria-label]")).toBeNull();
  });

  it("opens the disclaimer tooltip from the keyboard", async () => {
    renderCard();
    const user = userEvent.setup();

    await user.tab();

    expect(screen.getByRole("button", { name: "Relevância estimada 87 de 100" })).toHaveFocus();
    expect(await screen.findByRole("tooltip")).toHaveTextContent(SCORE_DISCLAIMER);
  });
});
