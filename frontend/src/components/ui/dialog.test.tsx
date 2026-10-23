import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEffect, useRef, useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { ConfirmDialog } from "@/components/ui/dialog";

interface HarnessProps {
  showOpener?: boolean;
  initiallyOpen?: boolean;
  /** After a confirmation the page focuses a field of its own (as the token form does after hiding the token). */
  focusFieldAfterConfirm?: boolean;
  onConfirm?: () => void;
}

/** A controlled ConfirmDialog (no trigger), opened by a button that the page may remove. */
function Harness({ showOpener = true, initiallyOpen = false, focusFieldAfterConfirm = false, onConfirm }: HarnessProps) {
  const [open, setOpen] = useState(initiallyOpen);
  const [confirmed, setConfirmed] = useState(false);
  const listRef = useRef<HTMLUListElement | null>(null);
  const fallbackRef = useRef<HTMLButtonElement | null>(null);
  const fieldRef = useRef<HTMLInputElement | null>(null);

  useEffect(() => {
    if (confirmed && focusFieldAfterConfirm) fieldRef.current?.focus();
  }, [confirmed, focusFieldAfterConfirm]);

  return (
    <>
      <ul ref={listRef} tabIndex={-1} aria-label="Itens">
        <li>Item</li>
      </ul>
      <button type="button" ref={fallbackRef}>
        Atualizar
      </button>
      <input ref={fieldRef} aria-label="Nome" />
      {showOpener ? (
        <button type="button" onClick={() => setOpen(true)}>
          Excluir item
        </button>
      ) : null}
      <ConfirmDialog
        open={open}
        onOpenChange={setOpen}
        title="Excluir o item?"
        confirmLabel="Excluir"
        destructive
        onConfirm={() => {
          onConfirm?.();
          setOpen(false);
          setConfirmed(true);
        }}
        returnFocus={{ afterConfirm: listRef, fallback: fallbackRef }}
      />
    </>
  );
}

describe("ConfirmDialog focus", () => {
  it("returns focus to the button that opened it after 'Cancelar', Esc or the close button", async () => {
    const user = userEvent.setup();
    render(<Harness />);
    const opener = screen.getByRole("button", { name: "Excluir item" });

    await user.click(opener);
    const dialog = await screen.findByRole("dialog", { name: "Excluir o item?" });
    // Focus starts inside the dialog, on the safe choice.
    expect(within(dialog).getByRole("button", { name: "Cancelar" })).toHaveFocus();
    await user.click(within(dialog).getByRole("button", { name: "Cancelar" }));
    await waitFor(() => expect(opener).toHaveFocus());
    expect(document.activeElement).not.toBe(document.body);

    await user.click(opener);
    await screen.findByRole("dialog");
    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    await waitFor(() => expect(opener).toHaveFocus());

    await user.click(opener);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Fechar" }));
    await waitFor(() => expect(opener).toHaveFocus());
  });

  it("moves focus to the 'afterConfirm' element after a confirmation", async () => {
    const user = userEvent.setup();
    const onConfirm = vi.fn();
    render(<Harness onConfirm={onConfirm} />);

    await user.click(screen.getByRole("button", { name: "Excluir item" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Excluir" }));

    expect(onConfirm).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.getByRole("list", { name: "Itens" })).toHaveFocus());
  });

  it("uses the fallback when the opener left the page while the dialog was open", async () => {
    const user = userEvent.setup();
    const { rerender } = render(<Harness />);

    await user.click(screen.getByRole("button", { name: "Excluir item" }));
    const dialog = await screen.findByRole("dialog");
    rerender(<Harness showOpener={false} />);
    await user.click(within(dialog).getByRole("button", { name: "Cancelar" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Atualizar" })).toHaveFocus());
  });

  it("uses the fallback when nothing had focus when it opened (Safari does not focus clicked buttons)", async () => {
    const user = userEvent.setup();
    render(<Harness initiallyOpen />);

    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Cancelar" }));

    await waitFor(() => expect(screen.getByRole("button", { name: "Atualizar" })).toHaveFocus());
  });

  it("keeps the focus the page placed itself after closing", async () => {
    const user = userEvent.setup();
    render(<Harness focusFieldAfterConfirm />);

    await user.click(screen.getByRole("button", { name: "Excluir item" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Excluir" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    // Let the dialog's deferred close handling run, then check it did not take the focus back.
    await new Promise((resolve) => setTimeout(resolve, 20));
    expect(screen.getByRole("textbox", { name: "Nome" })).toHaveFocus();
  });
});
