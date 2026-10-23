import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { CompanionAvailability } from "@/features/live/companion-availability";

describe("CompanionAvailability", () => {
  it("says the companion is still in development when no download link is configured", () => {
    render(<CompanionAvailability downloadUrl={null} />);

    expect(screen.getByRole("alert")).toHaveTextContent("Companion ainda em desenvolvimento");
    expect(screen.getByRole("alert")).toHaveTextContent("ainda não foi publicado");
    expect(screen.queryByRole("link", { name: /baixar o companion/i })).not.toBeInTheDocument();
  });

  it("links to the configured download instead", () => {
    render(<CompanionAvailability downloadUrl="https://downloads.example.com/companion-1.0.0.zip" />);

    const link = screen.getByRole("link", { name: /baixar o companion/i });
    expect(link).toHaveAttribute("href", "https://downloads.example.com/companion-1.0.0.zip");
    expect(link).toHaveAttribute("rel", expect.stringContaining("noopener"));
    expect(screen.queryByText(/em desenvolvimento/i)).not.toBeInTheDocument();
  });
});
