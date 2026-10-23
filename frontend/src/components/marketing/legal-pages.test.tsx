import { render, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

const LEGAL_ENV: Record<string, string> = {
  NEXT_PUBLIC_SUPPORT_EMAIL: "suporte@exemplo.com.br",
  NEXT_PUBLIC_LEGAL_COMPANY_NAME: "Exemplo Mídia Ltda.",
  NEXT_PUBLIC_LEGAL_CNPJ: "12.345.678/0001-90",
  NEXT_PUBLIC_LEGAL_ADDRESS: "Rua Exemplo, 100, São Paulo/SP",
  NEXT_PUBLIC_LEGAL_FORUM: "São Paulo/SP",
  NEXT_PUBLIC_LEGAL_DPO_EMAIL: "dpo@exemplo.com.br",
  NEXT_PUBLIC_LEGAL_UPDATED_AT: "2026-10-22",
};

const PLACEHOLDER = /\[[A-ZÇÃÉÊÍÓÚ /-]+\]/;

/** `brand` reads the environment at import time, so each scenario re-imports the modules. */
async function loadPages() {
  vi.resetModules();
  const [terms, privacy, chrome] = await Promise.all([
    import("@/app/(marketing)/terms/page"),
    import("@/app/(marketing)/privacy/page"),
    import("@/components/marketing/site-chrome"),
  ]);
  return { TermsPage: terms.default, PrivacyPage: privacy.default, MarketingFooter: chrome.MarketingFooter };
}

afterEach(() => {
  vi.unstubAllEnvs();
});

describe("legal pages", () => {
  it("never render bracket placeholders, reviewer notes or example addresses while the identity is unconfigured", async () => {
    const { TermsPage, PrivacyPage, MarketingFooter } = await loadPages();

    const { container } = render(
      <>
        <TermsPage />
        <PrivacyPage />
        <MarketingFooter />
      </>,
    );

    expect(container.textContent).not.toMatch(PLACEHOLDER);
    expect(container.textContent).not.toMatch(/substitua|revisão jurídica|example\.com/i);
    expect(screen.getAllByText(/ainda não foi publicado/i)).toHaveLength(2);
    expect(screen.queryByRole("link", { name: "Suporte" })).not.toBeInTheDocument();
    expect(screen.getByText(/Todos os direitos reservados/)).not.toHaveTextContent(/operado por/);
  });

  it("render the configured company, CNPJ, forum, DPO contact and review date", async () => {
    for (const [key, value] of Object.entries(LEGAL_ENV)) vi.stubEnv(key, value);
    const { TermsPage, PrivacyPage, MarketingFooter } = await loadPages();

    const { container } = render(
      <>
        <TermsPage />
        <PrivacyPage />
        <MarketingFooter />
      </>,
    );

    expect(container.textContent).not.toMatch(PLACEHOLDER);
    expect(container.textContent).not.toMatch(/substitua|revisão jurídica/i);
    expect(screen.getAllByText("Última atualização: 22/10/2026.")).toHaveLength(2);
    expect(container.textContent).toContain("inscrita no CNPJ sob o nº 12.345.678/0001-90, com sede em Rua Exemplo, 100, São Paulo/SP");
    expect(container.textContent).toContain("foro de São Paulo/SP");
    expect(screen.getAllByRole("link", { name: "dpo@exemplo.com.br" }).length).toBeGreaterThan(0);
    expect(screen.getByRole("link", { name: "Suporte" })).toHaveAttribute("href", "mailto:suporte@exemplo.com.br");
    expect(screen.getByText(/é operado por Exemplo Mídia Ltda\. \(CNPJ 12\.345\.678\/0001-90\)\. Todos os direitos reservados\./)).toBeInTheDocument();
  });
});
