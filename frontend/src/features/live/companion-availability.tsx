import { Download } from "lucide-react";
import { Alert } from "@/components/ui/alert";
import { buttonVariants } from "@/components/ui/button";
import { cn } from "@/lib/utils";

/**
 * The OBS companion is only announced as installable when a download link is configured
 * (`NEXT_PUBLIC_COMPANION_DOWNLOAD_URL`); until then the page says so instead of sending
 * people to install something that is not distributed yet.
 */
export function CompanionAvailability({ downloadUrl, className }: { downloadUrl: string | null; className?: string }) {
  if (downloadUrl) {
    return (
      <Alert
        variant="info"
        className={className}
        title="Companion disponível para download"
        action={
          <a href={downloadUrl} target="_blank" rel="noopener noreferrer" className={cn(buttonVariants({ variant: "secondary", size: "sm" }))}>
            <Download aria-hidden="true" />
            Baixar o companion
          </a>
        }
      >
        Instale no computador da transmissão e siga os passos abaixo.
      </Alert>
    );
  }
  return (
    <Alert variant="warning" className={className} title="Companion ainda em desenvolvimento">
      O instalador do aplicativo que envia os segmentos da sua gravação ainda não foi publicado: por enquanto ele roda com Python a
      partir do repositório do projeto, como nos passos abaixo, e o link de download aparecerá nesta página quando existir. Os tokens
      de dispositivo já funcionam: eles ficam listados em “Dispositivos” e podem ser revogados a qualquer momento.
    </Alert>
  );
}
