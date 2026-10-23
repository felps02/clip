/**
 * Public branding and legal identity. The product codename (ClipForge) is only a fallback:
 * the real brand name is injected through NEXT_PUBLIC_BRAND_NAME at build time.
 *
 * Everything that identifies the operating company (name, CNPJ, address, forum, DPO contact,
 * date of the last legal review) is configuration too. The Terms and Privacy pages are only
 * published when every field is present — never with bracket placeholders — and the footer
 * omits the company line until then. Optional values are `null` when unset so the UI can
 * hide the corresponding element instead of printing an example address.
 */
function optional(value: string | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

export interface LegalIdentity {
  companyName: string | null;
  cnpj: string | null;
  address: string | null;
  /** Comarca/UF elected in the terms. */
  forum: string | null;
  dpoEmail: string | null;
  /** ISO date of the last legal review (shown as "Última atualização"). */
  updatedAt: string | null;
}

export const brand = {
  name: process.env.NEXT_PUBLIC_BRAND_NAME || "ClipForge",
  tagline:
    "Transforme horas de live em clipes incríveis, sem precisar assistir a tudo ou editar manualmente.",
  shortDescription:
    "Envie a gravação da sua live, deixe a análise encontrar os melhores momentos e baixe clipes prontos para TikTok, Reels, Shorts e YouTube.",
  /** Support e-mail shown in the footer and legal pages; hidden while unset. */
  supportEmail: optional(process.env.NEXT_PUBLIC_SUPPORT_EMAIL),
  /** Download link of the OBS companion; `null` while it is not distributed yet. */
  companionDownloadUrl: optional(process.env.NEXT_PUBLIC_COMPANION_DOWNLOAD_URL),
  legal: {
    companyName: optional(process.env.NEXT_PUBLIC_LEGAL_COMPANY_NAME),
    cnpj: optional(process.env.NEXT_PUBLIC_LEGAL_CNPJ),
    address: optional(process.env.NEXT_PUBLIC_LEGAL_ADDRESS),
    forum: optional(process.env.NEXT_PUBLIC_LEGAL_FORUM),
    dpoEmail: optional(process.env.NEXT_PUBLIC_LEGAL_DPO_EMAIL),
    updatedAt: optional(process.env.NEXT_PUBLIC_LEGAL_UPDATED_AT),
  } satisfies LegalIdentity,
};

export type Brand = typeof brand;

/** A legal identity with every field filled in, plus the support contact the documents cite. */
export type PublishedLegalIdentity = { [K in keyof LegalIdentity]: string } & { supportEmail: string };

/**
 * The legal documents can only be rendered when nothing would be left blank. Returns `null`
 * when any field (or the support e-mail) is missing or the review date is not a valid date.
 */
export function resolveLegal(
  legal: LegalIdentity = brand.legal,
  supportEmail: string | null = brand.supportEmail,
): PublishedLegalIdentity | null {
  const { companyName, cnpj, address, forum, dpoEmail, updatedAt } = legal;
  if (!companyName || !cnpj || !address || !forum || !dpoEmail || !updatedAt || !supportEmail) return null;
  if (Number.isNaN(Date.parse(updatedAt))) return null;
  return { companyName, cnpj, address, forum, dpoEmail, updatedAt, supportEmail };
}
