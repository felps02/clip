import type { ReactNode } from "react";
import { Label } from "@/components/ui/label";
import { cn } from "@/lib/utils";

export interface FormFieldProps {
  id: string;
  label: ReactNode;
  hint?: ReactNode;
  error?: string | null;
  required?: boolean;
  className?: string;
  children: ReactNode;
  /** Render the label after the control (checkbox style). */
  inline?: boolean;
}

/** Label + control + hint/error wiring. Pass `aria-describedby={fieldDescribedBy(id, !!error, !!hint)}` to the control. */
export function FormField({ id, label, hint, error, required, className, children, inline }: FormFieldProps) {
  return (
    <div className={cn("space-y-1.5", inline && "flex items-start gap-3 space-y-0", className)}>
      {inline ? (
        <>
          <div className="pt-0.5">{children}</div>
          <div className="space-y-1">
            <Label htmlFor={id} className="leading-snug font-normal">
              {label}
            </Label>
            {hint && !error ? (
              <p id={`${id}-hint`} className="text-xs text-fg-subtle">
                {hint}
              </p>
            ) : null}
            {error ? (
              <p id={`${id}-error`} className="text-xs text-danger" role="alert">
                {error}
              </p>
            ) : null}
          </div>
        </>
      ) : (
        <>
          <Label htmlFor={id} className="flex items-center gap-1">
            {label}
            {required ? (
              <>
                <span className="text-danger" aria-hidden="true">
                  *
                </span>
                {/* Part of the control's accessible name, so screen readers announce the requirement. */}
                <span className="sr-only"> (obrigatório)</span>
              </>
            ) : null}
          </Label>
          {children}
          {hint && !error ? (
            <p id={`${id}-hint`} className="text-xs text-fg-subtle">
              {hint}
            </p>
          ) : null}
          {error ? (
            <p id={`${id}-error`} className="text-xs text-danger" role="alert">
              {error}
            </p>
          ) : null}
        </>
      )}
    </div>
  );
}

export function fieldDescribedBy(id: string, hasError: boolean, hasHint: boolean): string | undefined {
  if (hasError) return `${id}-error`;
  if (hasHint) return `${id}-hint`;
  return undefined;
}
