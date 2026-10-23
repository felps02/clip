import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, within } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { PlansSection } from "@/components/marketing/plans-section";
import { FALLBACK_PLANS } from "@/components/marketing/plans-fallback";

const fetchMock = vi.fn<typeof fetch>();

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, retryDelay: 0 } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("PlansSection", () => {
  it("shows skeletons (no plan names, limits or prices) while the plans are loading", () => {
    fetchMock.mockReturnValue(new Promise<Response>(() => undefined));

    render(<PlansSection />, { wrapper });

    expect(screen.getByRole("status")).toHaveTextContent("Carregando os planos…");
    expect(screen.queryByRole("article")).not.toBeInTheDocument();
    expect(screen.queryByText(/min\/mês/)).not.toBeInTheDocument();
    expect(screen.queryByText(/valores de referência/i)).not.toBeInTheDocument();
  });

  it("when the API fails shows the reference values with an explicit notice and no paid price", async () => {
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    render(<PlansSection />, { wrapper });

    expect(await screen.findByText("Planos indisponíveis no momento: valores de referência", {}, { timeout: 5000 })).toBeInTheDocument();
    expect(screen.getByText(/Não foi possível carregar os planos do servidor\./)).toBeInTheDocument();
    expect(screen.getByText(/Os preços dos planos pagos não são exibidos\./)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Tentar novamente" })).toBeInTheDocument();
    for (const plan of FALLBACK_PLANS) {
      const card = screen.getByRole("article", { name: `Plano ${plan.name}` });
      expect(within(card).getByText("Valores de referência")).toBeInTheDocument();
      if (plan.code === "free") {
        // Price 0 is "Grátis" everywhere, the reference values included.
        expect(within(card).getByText("Grátis")).toBeInTheDocument();
        expect(within(card).queryByText("Preço indisponível no momento")).not.toBeInTheDocument();
      } else {
        expect(within(card).getByText("Preço indisponível no momento")).toBeInTheDocument();
      }
    }
    expect(screen.queryByText(/R\$/)).not.toBeInTheDocument();
    expect(screen.queryByText("Sob consulta")).not.toBeInTheDocument();
    expect(screen.getByText("600 min/mês")).toBeInTheDocument();
    expect(screen.getByText("2.400 min/mês")).toBeInTheDocument();
  });

  it("labels the highlighted tier as a recommendation, never as a popularity statistic", async () => {
    fetchMock.mockRejectedValue(new TypeError("Failed to fetch"));

    render(<PlansSection />, { wrapper });

    const creator = await screen.findByRole("article", { name: "Plano Creator" }, { timeout: 5000 });
    expect(within(creator).getByText("Recomendado")).toBeInTheDocument();
    expect(screen.getAllByText("Recomendado")).toHaveLength(1);
    expect(screen.queryByText(/mais escolhido|mais popular/i)).not.toBeInTheDocument();
  });

  it("renders live plans and prices from the API without any reference notice", async () => {
    fetchMock.mockResolvedValue(
      new Response(
        JSON.stringify({
          items: [
            { ...FALLBACK_PLANS[0], priceMonthlyCents: 0 },
            { ...FALLBACK_PLANS[1], priceMonthlyCents: 4990, currency: "BRL" },
            { ...FALLBACK_PLANS[2], priceMonthlyCents: 14990, currency: "BRL" },
          ],
        }),
        { status: 200, headers: { "content-type": "application/json" } },
      ),
    );

    render(<PlansSection />, { wrapper });

    expect(await screen.findByText("Grátis")).toBeInTheDocument();
    expect(screen.getByText((content) => content.replace(/ /g, " ") === "R$ 49,90")).toBeInTheDocument();
    expect(screen.getByText((content) => content.replace(/ /g, " ") === "R$ 149,90")).toBeInTheDocument();
    expect(screen.queryByText("Preço indisponível no momento")).not.toBeInTheDocument();
    expect(screen.queryByText(/valores de referência/i)).not.toBeInTheDocument();
    // Paid plans are subscribed after sign-up: the CTA says so instead of promising a direct purchase.
    expect(screen.getByRole("link", { name: "Criar conta e assinar o plano Creator" })).toHaveAttribute("href", "/register");
    expect(screen.getByRole("link", { name: "Criar conta grátis" })).toHaveAttribute("href", "/register");
  });

  it("formats file-size limits without rounding small values to 0 GB", async () => {
    fetchMock.mockResolvedValue(
      new Response(JSON.stringify({ items: [{ ...FALLBACK_PLANS[0], limits: { ...FALLBACK_PLANS[0].limits, maxFileSizeMb: 300 } }] }), {
        status: 200,
        headers: { "content-type": "application/json" },
      }),
    );

    render(<PlansSection />, { wrapper });

    expect(await screen.findByText("300 MB")).toBeInTheDocument();
  });
});
