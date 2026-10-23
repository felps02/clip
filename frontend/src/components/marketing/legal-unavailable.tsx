import { brand } from "@/config/brand";

/**
 * Rendered in place of a legal document whose identity fields (company, CNPJ, address, forum,
 * DPO contact, review date) are not configured. Placeholders are never shown to the public.
 */
export function LegalUnavailable({ title }: { title: string }) {
  return (
    <article className="legal-prose mx-auto max-w-3xl px-4 py-14 sm:px-6 lg:px-8">
      <p className="text-xs font-medium uppercase tracking-widest text-accent">Documento legal</p>
      <h1 className="mt-2 text-3xl font-semibold tracking-tight sm:text-4xl">{title}</h1>
      <p>
        Este documento ainda não foi publicado nesta instalação do {brand.name}.
        {brand.supportEmail ? (
          <>
            {" "}
            Em caso de dúvidas, escreva para <a href={`mailto:${brand.supportEmail}`}>{brand.supportEmail}</a>.
          </>
        ) : null}
      </p>
    </article>
  );
}
