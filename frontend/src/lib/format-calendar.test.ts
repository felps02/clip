import { describe, expect, it } from "vitest";
import { formatCalendarDate } from "@/lib/format";

describe("formatCalendarDate", () => {
  it("renders a date-only value as that calendar day regardless of the local timezone", () => {
    expect(formatCalendarDate("2026-10-22")).toBe("22/10/2026");
    expect(formatCalendarDate(" 2026-01-01 ")).toBe("01/01/2026");
  });

  it("falls back to formatDate for full timestamps and dashes for empty values", () => {
    expect(formatCalendarDate("2026-10-22T15:00:00Z")).toMatch(/^\d{2}\/\d{2}\/2026$/);
    expect(formatCalendarDate(null)).toBe("—");
    expect(formatCalendarDate("")).toBe("—");
  });
});
