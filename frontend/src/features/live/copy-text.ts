/**
 * Selects the whole content of `target`: the value of an input/textarea (focused, so the selection
 * shows and Ctrl+C works) or the text of any other element.
 */
export function selectContents(target: HTMLElement | null): boolean {
  if (!target) return false;
  if (target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement) {
    target.focus();
    target.select();
    target.setSelectionRange(0, target.value.length);
    return true;
  }
  const selection = typeof window !== "undefined" ? window.getSelection() : null;
  if (!selection) return false;
  const range = document.createRange();
  range.selectNodeContents(target);
  selection.removeAllRanges();
  selection.addRange(range);
  return true;
}

export type CopyResult = "clipboard" | "selection" | "failed";

/**
 * Copies `text`. `navigator.clipboard` only exists in secure contexts (https or localhost), so on a
 * LAN demo (http://<ip>:3000) the selected `target` is copied with `document.execCommand("copy")`.
 * `failed` means nothing was copied: `target` is left selected for a manual copy.
 */
export async function copyText(text: string, target: HTMLElement | null): Promise<CopyResult> {
  try {
    if (typeof navigator !== "undefined" && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return "clipboard";
    }
  } catch {
    // Not allowed (permissions, document not focused): try the selection below.
  }
  if (!selectContents(target) || typeof document.execCommand !== "function") return "failed";
  try {
    return document.execCommand("copy") ? "selection" : "failed";
  } catch {
    return "failed";
  }
}
