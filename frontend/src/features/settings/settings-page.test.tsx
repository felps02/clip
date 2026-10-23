import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SettingsPage } from "@/features/settings/settings-page";
import type { MeResponse, PlanLimits, Profile } from "@/lib/api/types";

const navigation = vi.hoisted(() => {
  const replace = vi.fn();
  return { replace, search: "tab=edicao", router: { replace, push: vi.fn(), prefetch: vi.fn() } };
});

vi.mock("next/navigation", () => ({
  useRouter: () => navigation.router,
  usePathname: () => "/settings",
  useSearchParams: () => new URLSearchParams(navigation.search),
}));

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

let profile: Profile;
let limits: PlanLimits;
let putBodies: unknown[];
let putResponse: () => Response;
const fetchMock = vi.fn<typeof fetch>();

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

function urlOf(input: Parameters<typeof fetch>[0]): string {
  return typeof input === "string" ? input : input instanceof URL ? input.toString() : input.url;
}

function wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

beforeEach(() => {
  navigation.search = "tab=edicao";
  limits = { ...freeLimits };
  profile = {
    displayName: "Ana",
    locale: "en-US",
    timezone: "America/Sao_Paulo",
    editingPreferences: {
      aspectRatio: "16:9",
      subtitlesEnabled: true,
      subtitleStyle: "bold",
      subtitlePosition: "top",
      minClipSeconds: 20,
      maxClipSeconds: 90,
      targetClipSeconds: 45,
      language: "en",
    },
    notificationPreferences: { emailOnJobCompleted: false, emailOnJobFailed: true, emailOnQuotaWarning: false, emailOnBilling: true },
  };
  putBodies = [];
  putResponse = () => json(profile);
  fetchMock.mockReset();
  fetchMock.mockImplementation((input, init) => {
    const url = urlOf(input);
    if (url.endsWith("/api/account/profile") && init?.method === "PUT") {
      putBodies.push(JSON.parse(String(init.body)));
      return Promise.resolve(putResponse());
    }
    if (url.endsWith("/api/account/profile")) return Promise.resolve(json(profile));
    if (url.endsWith("/api/auth/me")) {
      const me: MeResponse = {
        user: { id: "u1", email: "ana@exemplo.com", displayName: "Ana", emailConfirmed: true, createdAt: "2026-10-01T00:00:00Z" },
        profile,
        subscription: { planCode: "free", planName: "Gratuito", status: "active" },
        limits,
      };
      return Promise.resolve(json(me));
    }
    return Promise.resolve(json({ title: "not found", status: 404 }, 404));
  });
  vi.stubGlobal("fetch", fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("Settings → Preferências de edição", () => {
  it("edits every field of the editing preferences and saves the full contract object", async () => {
    // A plan with custom subtitle styles, so every style and position can be chosen.
    limits = { ...freeLimits, allowCustomSubtitleStyles: true };
    const user = userEvent.setup();
    render(<SettingsPage />, { wrapper });

    const language = await screen.findByLabelText("Idioma falado nas gravações");
    expect(language).toHaveValue("en");
    expect(within(language).getAllByRole("option").map((option) => option.textContent)).toEqual([
      "Automático (detectar)",
      "Português",
      "Inglês",
      "Espanhol",
    ]);
    expect(screen.getByRole("radio", { name: /16:9/ })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByRole("slider", { name: "Duração mínima" })).toHaveAttribute("aria-valuenow", "20");
    expect(screen.getByRole("slider", { name: "Duração máxima" })).toHaveAttribute("aria-valuenow", "90");
    expect(screen.getByRole("slider", { name: "Duração alvo" })).toHaveAttribute("aria-valuenow", "45");

    await user.selectOptions(language, "es");
    await user.click(screen.getByRole("radio", { name: /1:1/ }));
    await user.selectOptions(screen.getByLabelText("Posição da legenda"), "center");
    await user.click(screen.getByRole("switch", { name: "Legendas por padrão" }));
    screen.getByRole("slider", { name: "Duração alvo" }).focus();
    await user.keyboard("{ArrowRight}{ArrowRight}");
    await user.click(screen.getByRole("button", { name: "Salvar preferências" }));

    await waitFor(() => expect(putBodies).toHaveLength(1));
    expect(putBodies[0]).toEqual({
      editingPreferences: {
        aspectRatio: "1:1",
        subtitlesEnabled: false,
        subtitleStyle: "bold",
        subtitlePosition: "center",
        minClipSeconds: 20,
        maxClipSeconds: 90,
        targetClipSeconds: 47,
        language: "es",
      },
    });
  });

  it("disables subtitle styles the plan does not include, with an explanation", async () => {
    render(<SettingsPage />, { wrapper });

    const style = await screen.findByLabelText("Estilo da legenda");
    await waitFor(() => expect(within(style).getByRole("option", { name: /Caixa/ })).toBeDisabled());
    expect(within(style).getByRole("option", { name: "Destaque" })).toBeEnabled();
    expect(within(style).getByRole("option", { name: "Limpo (indisponível no seu plano)" })).toBeDisabled();
    expect(screen.getByText(/não estão incluídos no seu plano/)).toBeInTheDocument();
  });

  it("disables the subtitle positions the plan does not include, flags the saved one and previews what new projects get", async () => {
    const user = userEvent.setup();
    render(<SettingsPage />, { wrapper });

    const position = await screen.findByLabelText("Posição da legenda");
    await waitFor(() => expect(within(position).getByRole("option", { name: "Em cima (indisponível no seu plano)" })).toBeDisabled());
    expect(within(position).getByRole("option", { name: "Centro (indisponível no seu plano)" })).toBeDisabled();
    expect(within(position).getByRole("option", { name: "Embaixo" })).toBeEnabled();
    expect(
      screen.getByText("A preferência salva (Em cima) não está disponível no seu plano atual: os novos projetos usam Embaixo."),
    ).toBeInTheDocument();
    expect(position).toHaveAccessibleDescription(/os novos projetos usam Embaixo/);
    expect(screen.getByText(/as posições Em cima e Centro não estão incluídos no seu plano/)).toBeInTheDocument();
    expect(screen.getByText(/as legendas usam o estilo Destaque, na parte de baixo do quadro/)).toBeInTheDocument();
    // The preview shows what new projects will really get, not the preference the plan overrides.
    expect(screen.getByRole("img", { name: "Prévia da legenda no estilo Destaque, posição embaixo" })).toBeInTheDocument();

    // The stored preference is kept (it applies again after an upgrade) until the person changes it.
    await user.click(screen.getByRole("button", { name: "Salvar preferências" }));
    await waitFor(() => expect(putBodies).toHaveLength(1));
    expect(putBodies[0]).toMatchObject({ editingPreferences: { subtitlePosition: "top" } });

    await user.selectOptions(position, "bottom");
    expect(position).toHaveValue("bottom");
    expect(screen.queryByText(/A preferência salva \(Em cima\)/)).not.toBeInTheDocument();
  });

  it("offers every subtitle position when the plan allows custom styles", async () => {
    limits = { ...freeLimits, allowCustomSubtitleStyles: true };
    render(<SettingsPage />, { wrapper });

    const position = await screen.findByLabelText("Posição da legenda");
    await waitFor(() => expect(within(position).getByRole("option", { name: "Em cima" })).toBeEnabled());
    expect(within(position).getByRole("option", { name: "Centro" })).toBeEnabled();
    expect(screen.queryByText(/A preferência salva/)).not.toBeInTheDocument();
    expect(screen.getByRole("img", { name: "Prévia da legenda no estilo Destaque, posição em cima" })).toBeInTheDocument();
  });

  it("offers every subtitle style when the plan allows custom styles", async () => {
    limits = { ...freeLimits, allowCustomSubtitleStyles: true };
    render(<SettingsPage />, { wrapper });

    const style = await screen.findByLabelText("Estilo da legenda");
    await waitFor(() => expect(within(style).getByRole("option", { name: "Caixa" })).toBeEnabled());
    expect(screen.queryByText(/não estão incluídos no seu plano/)).not.toBeInTheDocument();
  });

  it("shows the API validation errors next to the fields", async () => {
    putResponse = () =>
      new Response(
        JSON.stringify({
          title: "Dados inválidos",
          status: 400,
          code: "validation_failed",
          errors: { "EditingPreferences.TargetClipSeconds": ["A duração alvo deve ficar entre a mínima e a máxima."] },
        }),
        { status: 400, headers: { "content-type": "application/problem+json" } },
      );
    const user = userEvent.setup();
    render(<SettingsPage />, { wrapper });

    await user.click(await screen.findByRole("button", { name: "Salvar preferências" }));

    expect(await screen.findByText("A duração alvo deve ficar entre a mínima e a máxima.")).toBeInTheDocument();
    expect(screen.getByText("Alguns valores não foram aceitos. Revise os campos destacados.")).toBeInTheDocument();
  });
});

describe("Settings → Notificações", () => {
  it("shows the four e-mail switches with honest copy and saves them", async () => {
    navigation.search = "tab=notificacoes";
    const user = userEvent.setup();
    render(<SettingsPage />, { wrapper });

    expect(await screen.findByText("Avisos dentro do produto ficam sempre ativos")).toBeInTheDocument();
    const switches = screen.getAllByRole("switch");
    expect(switches.map((element) => element.getAttribute("aria-checked"))).toEqual(["false", "true", "false", "true"]);
    expect(screen.getByRole("switch", { name: "Análise concluída" })).toHaveAttribute("aria-checked", "false");

    await user.click(screen.getByRole("switch", { name: "Análise concluída" }));
    await user.click(screen.getByRole("switch", { name: "Avisos de cota" }));
    await user.click(screen.getByRole("button", { name: "Salvar notificações" }));

    await waitFor(() => expect(putBodies).toHaveLength(1));
    expect(putBodies[0]).toEqual({
      notificationPreferences: { emailOnJobCompleted: true, emailOnJobFailed: true, emailOnQuotaWarning: true, emailOnBilling: true },
    });
  });
});

describe("Settings → Perfil", () => {
  it("shows the interface language as information (pt-BR only) and keeps sending pt-BR", async () => {
    navigation.search = "";
    const user = userEvent.setup();
    render(<SettingsPage />, { wrapper });

    const name = await screen.findByLabelText(/Nome de exibição/);
    expect(screen.getByText("Português (Brasil)")).toBeInTheDocument();
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
    expect(screen.queryByText("English (US)")).not.toBeInTheDocument();

    await user.clear(name);
    await user.type(name, "Ana Paula");
    await user.click(screen.getByRole("button", { name: "Salvar" }));

    await waitFor(() => expect(putBodies).toHaveLength(1));
    expect(putBodies[0]).toEqual({ displayName: "Ana Paula", locale: "pt-BR", timezone: "America/Sao_Paulo" });
  });
});

describe("Settings → Integrações", () => {
  it("never promises Twitch/YouTube connections and points to the upload", async () => {
    navigation.search = "tab=integracoes";
    render(<SettingsPage />, { wrapper });

    expect(await screen.findAllByText("Não disponível")).toHaveLength(2);
    expect(screen.queryByText(/Em breve/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Conectar" })).not.toBeInTheDocument();
    for (const link of screen.getAllByRole("link", { name: "Enviar gravação" })) expect(link).toHaveAttribute("href", "/new");
  });
});
