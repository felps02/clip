import { afterEach, describe, expect, it, vi } from "vitest";
import { openDownload } from "@/lib/download";

afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

describe("openDownload", () => {
  it("navigates once, through a temporary link, and never opens a second tab", () => {
    // With `noopener` window.open always returns null: the old "popup blocked" fallback fired on every click.
    const open = vi.fn(() => null);
    vi.stubGlobal("open", open);
    const hrefBefore = window.location.href;
    const clicked: HTMLAnchorElement[] = [];
    const click = vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(function (this: HTMLAnchorElement) {
      clicked.push(this);
    });

    openDownload("http://api.test/media/c1.mp4?download=1");

    expect(click).toHaveBeenCalledTimes(1);
    expect(clicked[0].href).toBe("http://api.test/media/c1.mp4?download=1");
    expect(clicked[0].hasAttribute("download")).toBe(true);
    expect(clicked[0].target).toBe("");
    expect(open).not.toHaveBeenCalled();
    expect(window.location.href).toBe(hrefBefore);
    // The temporary link does not stay in the page.
    expect(clicked[0].isConnected).toBe(false);
    expect(document.querySelectorAll("a[download]")).toHaveLength(0);
  });

  it("removes the temporary link even when the click throws", () => {
    vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => {
      throw new Error("blocked");
    });

    expect(() => openDownload("http://api.test/media/c2.mp4")).toThrow("blocked");
    expect(document.querySelectorAll("a[download]")).toHaveLength(0);
  });
});
