import { render, screen } from "@testing-library/react";
import { renderToString } from "react-dom/server";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Sidebar } from "@/components/layout/sidebar";
import { FlowDemo } from "@/components/marketing/flow-demo";
import { RelativeTime } from "@/components/ui/relative-time";
import { StatCard } from "@/components/ui/stat-card";
import { DEFAULT_TIME_ZONE, formatDate, formatDateTime, getDisplayTimeZone, setDisplayTimeZone } from "@/lib/format";

vi.mock("next/navigation", () => ({ usePathname: () => "/dashboard" }));

afterEach(() => {
  setDisplayTimeZone(null);
});

describe("dates without hydration mismatches", () => {
  it("formats absolute dates in an explicit time zone (São Paulo by default, the profile's when set)", () => {
    expect(getDisplayTimeZone()).toBe(DEFAULT_TIME_ZONE);
    expect(formatDateTime("2026-10-23T12:00:00Z")).toBe("23/10/2026, 09:00");
    expect(formatDate("2026-10-23T01:00:00Z")).toBe("22/10/2026");
    setDisplayTimeZone("UTC");
    expect(formatDateTime("2026-10-23T12:00:00Z")).toBe("23/10/2026, 12:00");
    setDisplayTimeZone("Not/AZone");
    expect(getDisplayTimeZone()).toBe(DEFAULT_TIME_ZONE);
  });

  it("renders relative times only after hydration: the server HTML carries the absolute date", () => {
    const value = new Date(Date.now() - 2 * 24 * 60 * 60 * 1000).toISOString();
    const html = renderToString(<RelativeTime value={value} />);
    expect(html).toContain(formatDateTime(value));
    expect(html).not.toContain("anteontem");

    render(<RelativeTime value={value} />);
    const time = screen.getByText("anteontem");
    expect(time.tagName).toBe("TIME");
    expect(time).toHaveAttribute("dateTime", value);
    expect(time).toHaveAttribute("title", formatDateTime(value));
  });
});

describe("layout fixes", () => {
  it("keeps the desktop sidebar sticky and as tall as the viewport, in a column as tall as the page", () => {
    render(<Sidebar open={false} onOpenChange={() => undefined} />);
    const aside = screen.getByTestId("desktop-sidebar");
    for (const name of ["sticky", "top-0", "h-dvh", "overflow-y-auto"]) expect(aside).toHaveClass(name);
    expect(aside).not.toHaveClass("fixed");
    const column = screen.getByTestId("desktop-sidebar-column");
    expect(column).toContainElement(aside);
    for (const name of ["shrink-0", "bg-surface", "border-r", "w-64"]) expect(column).toHaveClass(name);
  });

  it("reserves two label lines on stat cards so values line up", () => {
    render(<StatCard label="Minutos processados no período" value="12" />);
    expect(screen.getByText("Minutos processados no período")).toHaveClass("min-h-8", "line-clamp-2", "leading-4");
  });

  it("lays the hero demo out by its container width and never positions the chips by hand", () => {
    const { container } = render(<FlowDemo />);
    const root = container.firstElementChild as HTMLElement;
    expect(root).toHaveClass("@container", "min-w-0");
    for (const label of ["reação", "hype", "engraçado"]) {
      const chip = screen.getByText(label);
      expect(chip.getAttribute("style")).toBeNull();
      expect(chip).toHaveClass("truncate");
    }
    expect(container.querySelector(".lg\\:flex-row")).toBeNull();
  });
});
