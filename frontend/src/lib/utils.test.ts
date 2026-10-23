import { describe, expect, it } from "vitest";
import { safeNextPath } from "@/lib/utils";

describe("safeNextPath", () => {
  it("keeps same-origin paths with their query and fragment", () => {
    expect(safeNextPath("/projects/abc?tab=clips#candidatos")).toBe("/projects/abc?tab=clips#candidatos");
    expect(safeNextPath("/dashboard")).toBe("/dashboard");
    expect(safeNextPath("/library?q=final%20boss")).toBe("/library?q=final%20boss");
  });

  it("falls back for missing, relative, absolute and protocol-relative targets", () => {
    expect(safeNextPath(null)).toBe("/dashboard");
    expect(safeNextPath(undefined)).toBe("/dashboard");
    expect(safeNextPath("")).toBe("/dashboard");
    expect(safeNextPath("dashboard")).toBe("/dashboard");
    expect(safeNextPath("https://evil.example/x")).toBe("/dashboard");
    expect(safeNextPath("//evil.example")).toBe("/dashboard");
  });

  it("refuses backslashes, which browsers read as slashes", () => {
    // The reason: "/\evil.example" resolves to another host.
    expect(new URL("/\\evil.example", "http://localhost:3000").host).toBe("evil.example");
    expect(safeNextPath("/\\evil.example")).toBe("/dashboard");
    expect(safeNextPath("/projects\\..\\..\\evil")).toBe("/dashboard");
  });

  it("refuses control characters and whitespace, which URL parsers drop or trim", () => {
    // The reason: "/<tab>/evil.example" resolves to another host.
    expect(new URL("/\t/evil.example", "http://localhost:3000").host).toBe("evil.example");
    expect(safeNextPath("/\t/evil.example")).toBe("/dashboard");
    expect(safeNextPath("/\n/evil.example")).toBe("/dashboard");
    expect(safeNextPath("/\r/evil.example")).toBe("/dashboard");
    expect(safeNextPath("/\u0000/evil.example")).toBe("/dashboard");
    expect(safeNextPath("/ /evil.example")).toBe("/dashboard");
    expect(safeNextPath("/ /evil.example")).toBe("/dashboard");
    expect(safeNextPath("/dashboard\u007f")).toBe("/dashboard");
  });

  it("resolves dot segments and refuses those that collapse into a //host reference", () => {
    expect(safeNextPath("/..//evil.example")).toBe("/dashboard");
    expect(safeNextPath("/.//evil.example")).toBe("/dashboard");
    expect(safeNextPath("/%2e%2e//evil.example")).toBe("/dashboard");
    expect(safeNextPath("/projects/../library?q=1")).toBe("/library?q=1");
  });

  it("never sends the person back to the login page", () => {
    expect(safeNextPath("/login")).toBe("/dashboard");
    expect(safeNextPath("/login?next=%2Fdashboard")).toBe("/dashboard");
    expect(safeNextPath("/./login")).toBe("/dashboard");
    expect(safeNextPath("/login", "/projects")).toBe("/projects");
  });
});
