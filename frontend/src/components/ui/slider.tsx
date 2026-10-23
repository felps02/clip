"use client";

import * as SliderPrimitive from "@radix-ui/react-slider";
import { forwardRef, type ComponentPropsWithoutRef, type ComponentRef } from "react";
import { cn } from "@/lib/utils";

export interface SliderProps extends ComponentPropsWithoutRef<typeof SliderPrimitive.Root> {
  /** One label per thumb (accessibility). */
  thumbLabels?: string[];
  /** Visual tone of the filled range. */
  tone?: "accent" | "neutral";
}

/** Single (one value) or range (two values) slider built on Radix. */
export const Slider = forwardRef<ComponentRef<typeof SliderPrimitive.Root>, SliderProps>(function Slider(
  { className, thumbLabels, tone = "accent", value, defaultValue, ...props },
  ref,
) {
  const thumbs = value ?? defaultValue ?? [0];
  return (
    <SliderPrimitive.Root
      ref={ref}
      className={cn(
        "relative flex w-full touch-none select-none items-center data-[disabled]:opacity-50",
        props.orientation === "vertical" ? "h-full w-5 flex-col" : "h-5",
        className,
      )}
      value={value}
      defaultValue={defaultValue}
      {...props}
    >
      <SliderPrimitive.Track className="relative h-1.5 w-full grow overflow-hidden rounded-full bg-line">
        <SliderPrimitive.Range
          className={cn("absolute h-full rounded-full", tone === "accent" ? "bg-accent" : "bg-fg-muted")}
        />
      </SliderPrimitive.Track>
      {thumbs.map((_, index) => (
        <SliderPrimitive.Thumb
          key={index}
          aria-label={thumbLabels?.[index]}
          className="block size-4 rounded-full border-2 border-accent bg-canvas shadow-[0_0_0_3px_rgba(11,13,16,0.9)] transition-[box-shadow,transform] duration-150 hover:scale-110 focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60 focus-visible:ring-offset-2 focus-visible:ring-offset-canvas"
        />
      ))}
    </SliderPrimitive.Root>
  );
});
