import { forwardRef, type InputHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { cn } from "@/lib/utils";

export const inputClassName =
  "flex h-10 w-full min-w-0 rounded-md border border-line bg-canvas/60 px-3 py-2 text-sm text-fg shadow-[inset_0_1px_0_rgba(255,255,255,0.02)] transition-[border-color,box-shadow] duration-150 placeholder:text-fg-subtle hover:border-line-strong focus-visible:border-accent/60 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/30 disabled:cursor-not-allowed disabled:opacity-50 aria-[invalid=true]:border-danger/70 aria-[invalid=true]:focus-visible:ring-danger/30";

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  invalid?: boolean;
}

export const Input = forwardRef<HTMLInputElement, InputProps>(function Input({ className, invalid, ...props }, ref) {
  return <input ref={ref} className={cn(inputClassName, className)} aria-invalid={invalid || undefined} {...props} />;
});

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  invalid?: boolean;
}

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaProps>(function Textarea(
  { className, invalid, ...props },
  ref,
) {
  return (
    <textarea
      ref={ref}
      className={cn(inputClassName, "h-auto min-h-24 resize-y leading-relaxed", className)}
      aria-invalid={invalid || undefined}
      {...props}
    />
  );
});
