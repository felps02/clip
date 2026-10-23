import Link from "next/link";
import { buttonVariants } from "@/components/ui/button";
import { BrandMark } from "@/components/brand-mark";

export default function NotFound() {
  return (
    <div className="flex flex-1 flex-col items-center justify-center px-4 py-20 text-center">
      <BrandMark className="mb-8" />
      <p className="text-xs font-medium uppercase tracking-widest text-accent">Erro 404</p>
      <h1 className="mt-2 text-3xl font-semibold tracking-tight">Página não encontrada</h1>
      <p className="mt-3 max-w-md text-fg-muted">
        O endereço pode ter mudado ou o conteúdo foi removido. Volte para o início e continue de lá.
      </p>
      <div className="mt-8 flex flex-wrap justify-center gap-3">
        <Link href="/" className={buttonVariants({ variant: "secondary" })}>
          Página inicial
        </Link>
        <Link href="/dashboard" className={buttonVariants({ variant: "primary" })}>
          Ir para o dashboard
        </Link>
      </div>
    </div>
  );
}
