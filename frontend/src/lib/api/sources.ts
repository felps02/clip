import { api } from "@/lib/api/client";
import type { SourceValidation } from "@/lib/api/types";

/** Identifies the platform of a link and tells honestly whether it can be processed. */
export function validateSource(url: string): Promise<SourceValidation> {
  return api.post<SourceValidation>("/api/sources/validate", { url });
}
