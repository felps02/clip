import { afterEach, describe, expect, it, vi } from "vitest";
import { copyText } from "@/features/live/copy-text";

afterEach(() => {
  vi.restoreAllMocks();
  Reflect.deleteProperty(document, "execCommand");
  Reflect.deleteProperty(window.navigator, "clipboard");
  document.body.innerHTML = "";
});

function field(value: string): HTMLInputElement {
  const input = document.createElement("input");
  input.readOnly = true;
  input.value = value;
  document.body.appendChild(input);
  return input;
}

function stubExecCommand(result: boolean) {
  const execCommand = vi.fn(() => result);
  Object.defineProperty(document, "execCommand", { configurable: true, value: execCommand });
  return execCommand;
}

function withClipboard(writeText: (text: string) => Promise<void>) {
  Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: { writeText } });
}

describe("copyText", () => {
  it("uses the Clipboard API where it exists (https, localhost)", async () => {
    const writeText = vi.fn(() => Promise.resolve());
    withClipboard(writeText);
    const execCommand = stubExecCommand(true);

    await expect(copyText("cfd_abc", field("cfd_abc"))).resolves.toBe("clipboard");
    expect(writeText).toHaveBeenCalledWith("cfd_abc");
    expect(execCommand).not.toHaveBeenCalled();
  });

  it("copies the selected field with execCommand when there is no Clipboard API (http LAN address)", async () => {
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
    const input = field("cfd_abc");
    const execCommand = stubExecCommand(true);

    await expect(copyText("cfd_abc", input)).resolves.toBe("selection");
    expect(execCommand).toHaveBeenCalledWith("copy");
    expect(input).toHaveFocus();
    expect(input.value.slice(input.selectionStart ?? 0, input.selectionEnd ?? 0)).toBe("cfd_abc");
  });

  it("falls back to the selection when the Clipboard API refuses", async () => {
    withClipboard(() => Promise.reject(new DOMException("denied", "NotAllowedError")));
    const execCommand = stubExecCommand(true);

    await expect(copyText("cfd_abc", field("cfd_abc"))).resolves.toBe("selection");
    expect(execCommand).toHaveBeenCalledWith("copy");
  });

  it("reports a failure (field left selected for a manual copy) when nothing works", async () => {
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
    const input = field("cfd_abc");
    stubExecCommand(false);

    await expect(copyText("cfd_abc", input)).resolves.toBe("failed");
    expect(input.value.slice(input.selectionStart ?? 0, input.selectionEnd ?? 0)).toBe("cfd_abc");
    // Without execCommand at all (jsdom) it is a failure too, never an exception.
    Reflect.deleteProperty(document, "execCommand");
    await expect(copyText("cfd_abc", input)).resolves.toBe("failed");
  });

  it("selects the text of a non-input element (a command block) before copying it", async () => {
    Object.defineProperty(window.navigator, "clipboard", { configurable: true, value: undefined });
    const code = document.createElement("code");
    code.textContent = "python -m clipforge_companion run";
    document.body.appendChild(code);
    const selected: string[] = [];
    const execCommand = vi.fn(() => {
      selected.push(window.getSelection()?.toString() ?? "");
      return true;
    });
    Object.defineProperty(document, "execCommand", { configurable: true, value: execCommand });

    await expect(copyText("python -m clipforge_companion run", code)).resolves.toBe("selection");
    expect(selected).toEqual(["python -m clipforge_companion run"]);
  });
});
