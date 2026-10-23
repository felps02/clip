"use client";

import { useRef, type KeyboardEvent, type ReactNode } from "react";
import { cn } from "@/lib/utils";

export interface ChoiceOption<T extends string> {
  value: T;
  label: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  disabled?: boolean;
}

export interface ChoiceGroupProps<T extends string> {
  label: string;
  value: T;
  onChange: (value: T) => void;
  options: ChoiceOption<T>[];
  columns?: 2 | 3 | 4;
  className?: string;
  size?: "sm" | "md";
}

/**
 * Accessible radio group rendered as selectable cards (format, subtitle style, framing...).
 * Follows the ARIA radio pattern: one tab stop (the checked option, or the first enabled one),
 * arrow keys move and select, Home/End jump to the first/last enabled option.
 */
export function ChoiceGroup<T extends string>({
  label,
  value,
  onChange,
  options,
  columns = 3,
  className,
  size = "md",
}: ChoiceGroupProps<T>) {
  const cols = { 2: "grid-cols-2", 3: "grid-cols-3", 4: "grid-cols-2 sm:grid-cols-4" }[columns];
  const buttons = useRef<Array<HTMLButtonElement | null>>([]);
  const enabled = options.flatMap((option, index) => (option.disabled ? [] : [index]));
  const checkedIndex = options.findIndex((option) => option.value === value && !option.disabled);
  const tabStop = checkedIndex >= 0 ? checkedIndex : (enabled[0] ?? -1);

  const select = (index: number) => {
    onChange(options[index].value);
    buttons.current[index]?.focus();
  };

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>, index: number) => {
    if (enabled.length === 0) return;
    const position = Math.max(0, enabled.indexOf(index));
    let target: number | undefined;
    if (event.key === "ArrowRight" || event.key === "ArrowDown") target = enabled[(position + 1) % enabled.length];
    else if (event.key === "ArrowLeft" || event.key === "ArrowUp") target = enabled[(position - 1 + enabled.length) % enabled.length];
    else if (event.key === "Home") target = enabled[0];
    else if (event.key === "End") target = enabled[enabled.length - 1];
    if (target === undefined) return;
    event.preventDefault();
    select(target);
  };

  return (
    <div role="radiogroup" aria-label={label} className={cn("grid gap-2", cols, className)}>
      {options.map((option, index) => {
        const selected = option.value === value;
        return (
          <button
            key={option.value}
            ref={(element) => {
              buttons.current[index] = element;
            }}
            type="button"
            role="radio"
            aria-checked={selected}
            disabled={option.disabled}
            tabIndex={index === tabStop ? 0 : -1}
            onClick={() => onChange(option.value)}
            onKeyDown={(event) => onKeyDown(event, index)}
            className={cn(
              "flex flex-col items-center justify-center gap-2 rounded-md border text-center transition-[border-color,background-color,box-shadow] duration-150 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 disabled:cursor-not-allowed disabled:opacity-50",
              size === "md" ? "min-h-20 px-3 py-3" : "min-h-12 px-2 py-2",
              selected
                ? "border-accent/60 bg-accent/10 text-fg shadow-[0_0_0_1px_rgba(179,245,59,0.25)]"
                : "border-line bg-canvas/40 text-fg-muted hover:border-line-strong hover:text-fg",
            )}
          >
            {option.icon ? <span className={cn(selected ? "text-accent" : "text-fg-subtle")}>{option.icon}</span> : null}
            <span className="text-sm font-medium leading-tight">{option.label}</span>
            {option.description ? <span className="text-[11px] leading-snug text-fg-subtle">{option.description}</span> : null}
          </button>
        );
      })}
    </div>
  );
}
