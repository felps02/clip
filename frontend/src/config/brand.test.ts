import { describe, expect, it } from "vitest";
import { resolveLegal, type LegalIdentity } from "@/config/brand";

const complete: LegalIdentity = {
  companyName: "Exemplo Mídia Ltda.",
  cnpj: "12.345.678/0001-90",
  address: "Rua Exemplo, 100, São Paulo/SP",
  forum: "São Paulo/SP",
  dpoEmail: "dpo@exemplo.com.br",
  updatedAt: "2026-10-22",
};

describe("resolveLegal", () => {
  it("publishes the identity only when every field and the support e-mail are set", () => {
    expect(resolveLegal(complete, "suporte@exemplo.com.br")).toEqual({ ...complete, supportEmail: "suporte@exemplo.com.br" });
  });

  it("returns null when any field is missing, so placeholders are never rendered", () => {
    for (const key of Object.keys(complete) as Array<keyof LegalIdentity>) {
      expect(resolveLegal({ ...complete, [key]: null }, "suporte@exemplo.com.br")).toBeNull();
    }
    expect(resolveLegal(complete, null)).toBeNull();
  });

  it("rejects an unparseable review date", () => {
    expect(resolveLegal({ ...complete, updatedAt: "[DATA]" }, "suporte@exemplo.com.br")).toBeNull();
  });

  it("defaults to the build-time configuration, which is empty in tests", () => {
    expect(resolveLegal()).toBeNull();
  });
});
