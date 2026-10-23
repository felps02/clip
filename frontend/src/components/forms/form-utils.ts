import type { FieldValues, Path, UseFormSetError } from "react-hook-form";
import { describeError, isApiError } from "@/lib/api/client";

/**
 * Maps ProblemDetails field errors (`errors: { email: ["..."] }`) onto react-hook-form fields.
 * Returns a form-level message when something is left to show (or null when every error landed on a field).
 */
export function applyApiErrors<T extends FieldValues>(
  error: unknown,
  setError: UseFormSetError<T>,
  knownFields: Path<T>[],
  aliases: Partial<Record<string, Path<T>>> = {},
): string | null {
  if (!isApiError(error)) return describeError(error);
  const messages = error.fieldMessages();
  let applied = false;
  for (const [field, message] of Object.entries(messages)) {
    const normalized = field.toLowerCase();
    const alias = aliases[field] ?? aliases[normalized];
    const match = alias ?? knownFields.find((known) => known.toLowerCase() === normalized);
    if (match) {
      setError(match, { type: "server", message });
      applied = true;
    }
  }
  if (applied && (error.code === "validation_failed" || error.status === 400 || error.status === 422)) {
    return null;
  }
  return describeError(error);
}
