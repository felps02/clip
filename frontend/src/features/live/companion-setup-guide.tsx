"use client";

import { Copy } from "lucide-react";
import { useRef, type ReactNode } from "react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card";
import { brand } from "@/config/brand";
import { CompanionAvailability } from "@/features/live/companion-availability";
import { copyText, selectContents } from "@/features/live/copy-text";
import { getApiBaseUrl } from "@/lib/api/client";
import { useHydrated } from "@/lib/hooks/use-hydrated";

/** Placeholder of the API address until the page knows the host it was opened with. */
export const API_URL_PLACEHOLDER = "<endereço-da-api>";

/** Windows first (the streamers' platform); the Linux/macOS activation is mentioned below the block. */
export const INSTALL_COMMANDS = [
  "python -m venv .venv",
  String.raw`.venv\Scripts\activate`,
  "python -m pip install -r requirements.txt",
].join("\n");

export function companionRunCommand(apiUrl: string): string {
  return `python -m clipforge_companion run --api ${apiUrl} --token <seu-token> --obs-password <senha-do-obs-websocket>`;
}

export function companionCheckCommand(apiUrl: string): string {
  return `python -m clipforge_companion check --api ${apiUrl} --token <seu-token>`;
}

function CommandBlock({ command, label }: { command: string; label: string }) {
  const codeRef = useRef<HTMLElement | null>(null);
  const copy = async () => {
    const result = await copyText(command, codeRef.current);
    if (result === "failed") {
      selectContents(codeRef.current);
      toast.error("Não foi possível copiar automaticamente. O comando está selecionado: copie manualmente (Ctrl+C).");
    } else {
      toast.success("Comando copiado");
    }
  };
  return (
    <div className="mt-2 flex min-w-0 items-start gap-1 rounded-md border border-line bg-canvas/60 py-2 pl-3 pr-1">
      <pre className="min-w-0 flex-1 whitespace-pre-wrap font-mono text-xs leading-5 text-fg [overflow-wrap:anywhere]">
        <code ref={codeRef}>{command}</code>
      </pre>
      <Button variant="ghost" size="icon-sm" onClick={copy} aria-label={`Copiar: ${label}`}>
        <Copy aria-hidden="true" />
      </Button>
    </div>
  );
}

function Step({ index, title, children }: { index: number; title: string; children: ReactNode }) {
  return (
    <li className="flex gap-3">
      <span
        className="flex size-7 shrink-0 items-center justify-center rounded-full bg-accent/10 font-mono text-xs font-semibold text-accent"
        aria-hidden="true"
      >
        {index}
      </span>
      <div className="min-w-0 flex-1">
        <p className="text-sm font-medium">{title}</p>
        <div className="mt-0.5 space-y-1 text-sm text-fg-muted">{children}</div>
      </div>
    </li>
  );
}

/**
 * How to run the companion today: no installer yet, Python from the project repository, OBS 28+
 * with obs-websocket and a password, recording in MKV or hybrid MP4. Says what is never captured
 * and how long a highlight takes to show up.
 */
export function CompanionSetupGuide() {
  // The API address depends on the host the page was opened with: known only in the browser.
  const hydrated = useHydrated();
  const apiUrl = hydrated ? getApiBaseUrl() : API_URL_PLACEHOLDER;
  const downloadUrl = brand.companionDownloadUrl;

  return (
    <Card>
      <CardHeader>
        <CardTitle>Como configurar o companion</CardTitle>
        <CardDescription>
          O companion lê a sua própria gravação do OBS e a envia em segmentos. Nada é capturado da Twitch, do YouTube ou de outra
          plataforma, e o chat não é lido.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5">
        <CompanionAvailability downloadUrl={downloadUrl} />
        <ol className="space-y-5">
          <Step index={1} title={downloadUrl ? "Instale o companion" : "Prepare o companion (ainda não há instalador)"}>
            {downloadUrl ? (
              <p>Baixe o companion pelo link acima e instale no computador que grava a live.</p>
            ) : (
              <>
                <p>
                  Ele roda com Python 3.11 ou superior a partir da pasta <code className="font-mono text-xs text-fg">companion</code> do
                  repositório do projeto e usa o FFmpeg. Nessa pasta, no computador que grava a live:
                </p>
                <CommandBlock command={INSTALL_COMMANDS} label="comandos de instalação" />
                <p className="text-xs">
                  No Linux ou no macOS, ative o ambiente com <code className="font-mono">source .venv/bin/activate</code>.
                </p>
              </>
            )}
          </Step>
          <Step index={2} title="Ative o obs-websocket (OBS 28 ou superior)">
            <p>
              No OBS, abra Ferramentas → Configurações do servidor WebSocket, marque “Ativar servidor WebSocket” e defina uma senha.
            </p>
          </Step>
          <Step index={3} title="Grave em MKV ou MP4 híbrido">
            <p>
              Em Configurações → Saída → Gravação, use o formato MKV (padrão do OBS) ou MP4 híbrido/fragmentado: o companion lê o
              arquivo enquanto ele é gravado. MP4 comum só fica legível no fim e é recusado.
            </p>
          </Step>
          <Step index={4} title="Gere um token e rode o companion">
            <p>Gere o token em “Dispositivos” (ele aparece uma única vez) e rode, com o OBS aberto:</p>
            <CommandBlock command={companionRunCommand(apiUrl)} label="comando para rodar o companion" />
            <p className="text-xs">
              Para não deixar segredos no histórico do terminal, prefira as variáveis{" "}
              <code className="font-mono">CLIPFORGE_DEVICE_TOKEN</code> e <code className="font-mono">OBS_WEBSOCKET_PASSWORD</code>. Para
              só testar o token e a conexão: <code className="font-mono [overflow-wrap:anywhere]">{companionCheckCommand(apiUrl)}</code>.
            </p>
          </Step>
          <Step index={5} title="Inicie a gravação no OBS">
            <p>
              A sessão aparece em “Sessões” quando a gravação começa, e um segmento é enviado a cada 60 s (padrão). Ao parar a gravação, o
              companion envia o último segmento e encerra a sessão.
            </p>
          </Step>
          <Step index={6} title="Acompanhe os destaques">
            <p>
              Um destaque aparece cerca de um segmento depois do momento, mais o tempo de processamento (≈ 1,5 a 3 min com segmentos de 60 s
              em uma CPU de 4 núcleos). “Destaque detectado”, “renderizando” e “clipe pronto” aparecem como estados separados.
            </p>
          </Step>
        </ol>
      </CardContent>
    </Card>
  );
}
