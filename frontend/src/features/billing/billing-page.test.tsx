import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { BillingPage, CHECKOUT_WAIT_MS } from "@/features/billing/billing-page";
import type { Plan, PlanLimits, SubscriptionResponse, UsageResponse } from "@/lib/api/types";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  return { replace, search: "checkout=success", router: { replace, push: vi.fn(), prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  usePathname: () => "/billing",
  useSearchParams: () => new URLSearchParams(navigation.search),
}));

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
  transcriptionModel: "tiny",
  allowWebcamLayout: false,
  allowCustomSubtitleStyles: false,
};
const freePlan: Plan = { code: "free", name: "Free", priceMonthlyCents: 0, currency: "BRL", limits };
const creatorPlan: Plan = { code: "creator", name: "Creator", priceMonthlyCents: 4990, currency: "BRL", limits };
const usage: UsageResponse = {
  plan: { code: "free", name: "Free", limits },
  period: { start: "2026-10-01T00:00:00Z", end: "2026-10-31T23:59:59Z" },
  used: { processingSeconds: 600, renders: 2, storageBytes: 1024, activeProjects: 1 },
  remaining: {},
  history: [],
};

function subscriptionOf(plan: Plan): SubscriptionResponse {
  return { subscription: { planCode: plan.code, planName: plan.name, status: "active" }, plan, paymentsConfigured: true, portalAvailable: false };
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

const fetchMock = vi.fn<typeof fetch>();
let subscriptionCalls = 0;
/** Which plan the subscription endpoint reports on the n-th call (1-based). */
let planForCall: (call: number) => Plan = () => freePlan;
let usageStatus = 200;

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  navigation.search = "checkout=success";
  subscriptionCalls = 0;
  planForCall = () => freePlan;
  usageStatus = 200;
  fetchMock.mockReset();
  fetchMock.mockImplementation((input) => {
    const url = urlOf(input);
    if (url.endsWith("/api/billing/subscription")) {
      subscriptionCalls += 1;
      return Promise.resolve(json(subscriptionOf(planForCall(subscriptionCalls))));
    }
    if (url.endsWith("/api/account/usage")) {
      return Promise.resolve(usageStatus === 200 ? json(usage) : json({ title: "Erro", status: 503, code: "unavailable" }, 503));
    }
    if (url.endsWith("/api/plans")) return Promise.resolve(json({ items: [freePlan, creatorPlan] }));
    if (url.includes("/api/billing/payments")) return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
    return Promise.resolve(json({ title: "not found" }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
  vi.useFakeTimers({ shouldAdvanceTime: true });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

describe("BillingPage after checkout", () => {
  it("polls the subscription until the new plan shows up, then stops and keeps the confirmation", async () => {
    planForCall = (call) => (call >= 2 ? creatorPlan : freePlan);
    window.sessionStorage.setItem("cf.checkout", JSON.stringify({ planCode: "creator", previousPlanCode: "free" }));

    render(<BillingPage />, { wrapper });

    expect(await screen.findByText("Voltamos do checkout")).toBeInTheDocument();
    await vi.advanceTimersByTimeAsync(4_100);
    expect(await screen.findByText("Assinatura atualizada")).toBeInTheDocument();
    expect(screen.getByText("Seu plano agora é Creator.")).toBeInTheDocument();
    expect(JSON.parse(window.sessionStorage.getItem("cf.checkout") ?? "{}")).toMatchObject({ planCode: "creator", confirmed: true });

    const callsAtConfirmation = subscriptionCalls;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(subscriptionCalls).toBe(callsAtConfirmation);
  });

  it("stops polling after the bounded wait and says the provider has not confirmed yet", async () => {
    window.sessionStorage.setItem("cf.checkout", JSON.stringify({ planCode: "creator", previousPlanCode: "free" }));

    render(<BillingPage />, { wrapper });

    expect(await screen.findByText("Voltamos do checkout")).toBeInTheDocument();
    await vi.advanceTimersByTimeAsync(CHECKOUT_WAIT_MS + 100);
    expect(await screen.findByText("Ainda aguardando o provedor de pagamento")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Verificar agora" })).toBeInTheDocument();

    const callsAtTimeout = subscriptionCalls;
    await vi.advanceTimersByTimeAsync(20_000);
    expect(subscriptionCalls).toBe(callsAtTimeout);
    expect(screen.queryByText("Assinatura atualizada")).not.toBeInTheDocument();
  });

  it("shows the usage error (with retry) in the history card instead of the empty message", async () => {
    usageStatus = 503;
    navigation.search = "";

    render(<BillingPage />, { wrapper });

    const retries = await screen.findAllByRole("button", { name: "Tentar novamente" });
    expect(retries).toHaveLength(2);
    expect(screen.queryByText("Nenhum período anterior registrado.")).not.toBeInTheDocument();
    navigation.search = "checkout=success";
  });
});

describe("BillingPage plan changes", () => {
  const proPlan: Plan = { code: "pro", name: "Pro", priceMonthlyCents: 14990, currency: "BRL", limits };

  it("keeps the neutral message after ?checkout=success when this tab started no checkout", async () => {
    navigation.search = "checkout=success";

    render(<BillingPage />, { wrapper });

    expect(
      await screen.findByText("Voltamos do checkout. Confira abaixo o status atual da assinatura; mudanças só valem após a confirmação do provedor."),
    ).toBeInTheDocument();
    expect(screen.queryByText("Assinatura atualizada")).not.toBeInTheDocument();
  });

  it("treats planChanged as an in-place change: confirmed first, no checkout round trip, waits for the provider on this page", async () => {
    navigation.search = "";
    let changeRequestedAt = Number.POSITIVE_INFINITY;
    const checkoutBodies: unknown[] = [];
    fetchMock.mockImplementation((input, init) => {
      const url = urlOf(input);
      if (url.endsWith("/api/billing/checkout")) {
        checkoutBodies.push(JSON.parse(String(init?.body)));
        changeRequestedAt = subscriptionCalls;
        return Promise.resolve(json({ checkoutUrl: "http://web.test/billing?checkout=success", planChanged: true }));
      }
      if (url.endsWith("/api/billing/subscription")) {
        subscriptionCalls += 1;
        // The webhook "arrives" on the second poll after the change was requested.
        return Promise.resolve(json(subscriptionOf(subscriptionCalls >= changeRequestedAt + 2 ? proPlan : creatorPlan)));
      }
      if (url.endsWith("/api/account/usage")) return Promise.resolve(json(usage));
      if (url.endsWith("/api/plans")) return Promise.resolve(json({ items: [freePlan, creatorPlan, proPlan] }));
      if (url.includes("/api/billing/payments")) return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
      return Promise.resolve(json({ title: "not found" }, 404));
    });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    render(<BillingPage />, { wrapper });
    await user.click(await screen.findByRole("button", { name: "Mudar de plano" }));

    // The paid subscription changes right away at the provider: nothing is sent before the person confirms.
    const dialog = await screen.findByRole("dialog", { name: "Mudar para o plano Pro?" });
    expect(within(dialog).getByText(/A troca vale agora, na sua assinatura atual/)).toBeInTheDocument();
    expect(within(dialog).getByText(/cobra ou credita a diferença proporcional ao tempo que resta do período/)).toBeInTheDocument();
    expect(within(dialog).getByText(/Novo valor: R\$\s149,90\/mês\./)).toBeInTheDocument();
    expect(within(dialog).queryByText("O cancelamento agendado será desfeito")).not.toBeInTheDocument();
    expect(checkoutBodies).toHaveLength(0);
    await user.click(within(dialog).getByRole("button", { name: "Mudar de plano" }));

    expect(await screen.findByText("Troca de plano solicitada")).toBeInTheDocument();
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.queryByText("Voltamos do checkout")).not.toBeInTheDocument();
    expect(checkoutBodies).toEqual([expect.objectContaining({ planCode: "pro" })]);
    expect(JSON.parse(window.sessionStorage.getItem("cf.checkout") ?? "{}")).toMatchObject({ planCode: "pro", inPlace: true });

    // Until the provider confirms, the cards still show the old plan: no second change can be sent.
    expect(screen.getByRole("button", { name: "Troca solicitada" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Voltar para o plano Free" })).toBeDisabled();
    expect(screen.getByRole("button", { name: "Cancelar assinatura" })).toBeDisabled();
    expect(screen.queryByRole("button", { name: "Mudar de plano" })).not.toBeInTheDocument();

    await vi.advanceTimersByTimeAsync(8_200);
    expect(await screen.findByText("Plano alterado")).toBeInTheDocument();
    expect(screen.getByText("Seu plano agora é Pro.")).toBeInTheDocument();
    expect(within(screen.getByRole("article", { name: "Plano Creator" })).getByRole("button", { name: "Mudar de plano" })).toBeEnabled();
    expect(checkoutBodies).toHaveLength(1);
    navigation.search = "checkout=success";
  });

  it("warns that switching plans undoes a scheduled cancellation, and sends nothing when the person backs out", async () => {
    navigation.search = "";
    let checkoutCalls = 0;
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/billing/checkout")) {
        checkoutCalls += 1;
        return Promise.resolve(json({ checkoutUrl: "http://web.test/billing?checkout=success", planChanged: true }));
      }
      if (url.endsWith("/api/billing/subscription")) {
        return Promise.resolve(
          json({
            subscription: { planCode: "creator", planName: "Creator", status: "active", cancelAtPeriodEnd: true, currentPeriodEnd: "2026-11-30T12:00:00Z" },
            plan: creatorPlan,
            paymentsConfigured: true,
            portalAvailable: false,
          }),
        );
      }
      if (url.endsWith("/api/account/usage")) return Promise.resolve(json(usage));
      if (url.endsWith("/api/plans")) return Promise.resolve(json({ items: [freePlan, creatorPlan, proPlan] }));
      if (url.includes("/api/billing/payments")) return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
      return Promise.resolve(json({ title: "not found" }, 404));
    });
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });

    render(<BillingPage />, { wrapper });
    await user.click(await screen.findByRole("button", { name: "Mudar de plano" }));

    const dialog = await screen.findByRole("dialog", { name: "Mudar para o plano Pro?" });
    expect(within(dialog).getByText("O cancelamento agendado será desfeito")).toBeInTheDocument();
    expect(
      within(dialog).getByText(/a assinatura deixa de terminar e volta a ser renovada em 30\/11\/2026, com a cobrança do novo plano/),
    ).toBeInTheDocument();
    await user.click(within(dialog).getByRole("button", { name: "Voltar" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(checkoutCalls).toBe(0);
    expect(screen.getByRole("button", { name: "Mudar de plano" })).toBeEnabled();
    navigation.search = "checkout=success";
  });

  it("explains why going back to the free plan is not offered while a cancellation is scheduled", async () => {
    navigation.search = "";
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/billing/subscription")) {
        return Promise.resolve(
          json({
            subscription: { planCode: "creator", planName: "Creator", status: "active", cancelAtPeriodEnd: true, currentPeriodEnd: "2026-11-30T12:00:00Z" },
            plan: creatorPlan,
            paymentsConfigured: true,
            portalAvailable: false,
          }),
        );
      }
      if (url.endsWith("/api/account/usage")) return Promise.resolve(json(usage));
      if (url.endsWith("/api/plans")) return Promise.resolve(json({ items: [freePlan, creatorPlan] }));
      if (url.includes("/api/billing/payments")) return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
      return Promise.resolve(json({ title: "not found" }, 404));
    });

    render(<BillingPage />, { wrapper });

    const freeCard = await screen.findByRole("article", { name: "Plano Free" });
    expect(await within(freeCard).findByText(/Cancelamento já agendado: a conta volta para este plano em 30\/11\/2026\./)).toBeInTheDocument();
    expect(within(freeCard).queryByRole("button")).not.toBeInTheDocument();
    navigation.search = "checkout=success";
  });
});

describe("BillingPage prices and assistant usage (round 3)", () => {
  it("shows price 0 as Grátis, a missing price as Sob consulta, and the assistant messages of the period", async () => {
    navigation.search = "";
    const proOnRequest: Plan = { code: "pro", name: "Pro", priceMonthlyCents: null, currency: "BRL", limits };
    const withAssistant: UsageResponse = {
      ...usage,
      plan: { ...usage.plan, limits: { ...limits, assistantMessagesPerMonth: 100 } },
      used: { ...usage.used, assistantMessages: 7 },
    };
    fetchMock.mockImplementation((input) => {
      const url = urlOf(input);
      if (url.endsWith("/api/billing/subscription")) return Promise.resolve(json(subscriptionOf(freePlan)));
      if (url.endsWith("/api/account/usage")) return Promise.resolve(json(withAssistant));
      if (url.endsWith("/api/plans")) return Promise.resolve(json({ items: [freePlan, creatorPlan, proOnRequest] }));
      if (url.includes("/api/billing/payments")) return Promise.resolve(json({ items: [], page: 1, pageSize: 10, total: 0 }));
      return Promise.resolve(json({ title: "not found" }, 404));
    });

    render(<BillingPage />, { wrapper });

    const freeCard = await screen.findByRole("article", { name: "Plano Free" });
    expect(within(freeCard).getByText("Grátis")).toBeInTheDocument();
    // The current plan's summary says "Grátis" too (never "Sem cobrança" for a free plan).
    const currentCard = (await screen.findByRole("heading", { name: "Plano atual" })).closest("div.rounded-lg") as HTMLElement;
    expect(await within(currentCard).findByText("Grátis")).toBeInTheDocument();
    expect(within(currentCard).queryByText(/Sem cobrança/)).not.toBeInTheDocument();
    const proCard = screen.getByRole("article", { name: "Plano Pro" });
    expect(within(proCard).getAllByText("Sob consulta").length).toBeGreaterThan(0);
    const row = await screen.findByText("Mensagens do assistente");
    expect(row.parentElement).toHaveTextContent("7 / 100");
    expect(screen.getByRole("progressbar", { name: "Mensagens do assistente" })).toHaveAttribute("aria-valuetext", "7 de 100");
  });

  it("hides the assistant row when the API does not report it", async () => {
    navigation.search = "";
    render(<BillingPage />, { wrapper });
    expect(await screen.findByText("Minutos de processamento")).toBeInTheDocument();
    expect(screen.queryByText("Mensagens do assistente")).not.toBeInTheDocument();
  });
});
