import { act, render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { hydrateRoot } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { toast } from "sonner";
import { afterEach, describe, expect, it, vi } from "vitest";
import { API_URL_PLACEHOLDER, CompanionSetupGuide } from "@/features/live/companion-setup-guide";

const RUN_COMMAND =
  "python -m clipforge_companion run --api http://api.test --token <seu-token> --obs-password <senha-do-obs-websocket>";

afterEach(() => {
  vi.restoreAllMocks();
  Reflect.deleteProperty(document, "execCommand");
  Reflect.deleteProperty(window.navigator, "clipboard");
});

describe("CompanionSetupGuide", () => {
  it("is honest about how the companion runs today and what it needs", () => {
    render(<CompanionSetupGuide />);

    expect(screen.getByText("Prepare o companion (ainda não há instalador)")).toBeInTheDocument();
    expect(screen.getByText(/Ele roda com Python 3\.11 ou superior a partir da pasta/)).toBeInTheDocument();
    expect(screen.getByText(/python -m pip install -r requirements\.txt/)).toHaveTextContent(
      ["python -m venv .venv", String.raw`.venv\Scripts\activate`, "python -m pip install -r requirements.txt"].join(" "),
    );
    expect(screen.getByText("Ative o obs-websocket (OBS 28 ou superior)")).toBeInTheDocument();
    expect(screen.getByText(/marque “Ativar servidor WebSocket” e defina uma senha/)).toBeInTheDocument();
    expect(screen.getByText(/use o formato MKV \(padrão do OBS\) ou MP4 híbrido\/fragmentado/)).toBeInTheDocument();
    expect(screen.getByText(/MP4 comum só fica legível no fim e é recusado/)).toBeInTheDocument();
    expect(screen.getByText(/cerca de um segmento depois do momento, mais o tempo de processamento/)).toBeInTheDocument();
    expect(screen.getByText(/Nada é capturado da Twitch, do YouTube ou de outra plataforma/)).toBeInTheDocument();
    expect(screen.getByText(RUN_COMMAND)).toBeInTheDocument();
    // No download link is configured in tests: the companion is never announced as installable.
    expect(screen.queryByRole("link", { name: /baixar o companion/i })).not.toBeInTheDocument();
  });

  it("fills the run command with this page's API address only after hydration (placeholder on the server)", async () => {
    const html = renderToString(<CompanionSetupGuide />);
    expect(html).toContain("--api &lt;endereço-da-api&gt; --token &lt;seu-token&gt;");
    expect(html).not.toContain("http://api.test");

    const container = document.createElement("div");
    container.innerHTML = html;
    document.body.appendChild(container);
    const errors = vi.spyOn(console, "error").mockImplementation(() => undefined);
    let root: ReturnType<typeof hydrateRoot> | undefined;
    await act(async () => {
      root = hydrateRoot(container, <CompanionSetupGuide />);
    });

    expect(container).toHaveTextContent(RUN_COMMAND);
    expect(container).not.toHaveTextContent(API_URL_PLACEHOLDER);
    // No hydration mismatch was reported.
    expect(errors).not.toHaveBeenCalled();
    act(() => root?.unmount());
    container.remove();
  });

  it("copies a command, and asks for a manual copy when the page cannot", async () => {
    const user = userEvent.setup();
    const success = vi.spyOn(toast, "success");
    const error = vi.spyOn(toast, "error");
    render(<CompanionSetupGuide />);

    await user.click(screen.getByRole("button", { name: "Copiar: comando para rodar o companion" }));
    expect(await navigator.clipboard.readText()).toBe(RUN_COMMAND);
    expect(success).toHaveBeenCalledWith("Comando copiado");

    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
    Object.defineProperty(document, "execCommand", { configurable: true, value: vi.fn(() => false) });
    await user.click(screen.getByRole("button", { name: "Copiar: comandos de instalação" }));
    expect(error).toHaveBeenCalledWith("Não foi possível copiar automaticamente. O comando está selecionado: copie manualmente (Ctrl+C).");
  });
});
