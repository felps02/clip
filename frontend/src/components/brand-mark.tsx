import Link from "next/link";
import { brand } from "@/config/brand";
import { cn } from "@/lib/utils";

export function BrandLogo({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 32 32"
      className={cn("size-8", className)}
      aria-hidden="true"
      focusable="false"
    >
      <rect x="2" y="2" width="28" height="28" rx="8" fill="var(--color-accent)" />
      <path d="M12 10.5v11l9-5.5-9-5.5z" fill="var(--color-accent-fg)" />
      <path d="M8 7l16 18" stroke="var(--color-accent-fg)" strokeWidth="2.2" strokeLinecap="round" opacity="0.55" />
    </svg>
  );
}

export function BrandMark({ className, href = "/", compact = false }: { className?: string; href?: string; compact?: boolean }) {
  return (
    <Link
      href={href}
      className={cn(
        "inline-flex items-center gap-2.5 rounded-md text-fg focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-accent/60",
        className,
      )}
      aria-label={`${brand.name} — ${href === "/" ? "página inicial" : href === "/dashboard" ? "dashboard" : "início"}`}
    >
      <BrandLogo />
      {compact ? null : <span className="text-base font-semibold tracking-tight">{brand.name}</span>}
    </Link>
  );
}
