import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useState } from "react";
import { describe, expect, it } from "vitest";
import { ChoiceGroup } from "@/components/ui/choice-group";
import { FormField } from "@/components/ui/form-field";
import { Input } from "@/components/ui/input";
import { UsageRow } from "@/components/ui/progress";

function StyleChoice({ lockBoxed = false }: { lockBoxed?: boolean }) {
  const [value, setValue] = useState<"bold" | "clean" | "boxed">("bold");
  return (
    <ChoiceGroup
      label="Estilo"
      value={value}
      onChange={setValue}
      options={[
        { value: "bold", label: "Destaque" },
        { value: "clean", label: "Limpo" },
        { value: "boxed", label: "Caixa", disabled: lockBoxed },
      ]}
    />
  );
}

describe("ChoiceGroup (ARIA radio pattern)", () => {
  it("has one tab stop and moves the selection with arrow keys, skipping disabled options", async () => {
    const user = userEvent.setup();
    render(
      <>
        <button type="button">antes</button>
        <StyleChoice lockBoxed />
      </>,
    );

    await user.click(screen.getByRole("button", { name: "antes" }));
    await user.tab();
    expect(screen.getByRole("radio", { name: "Destaque" })).toHaveFocus();
    expect(screen.getByRole("radio", { name: "Limpo" })).toHaveAttribute("tabindex", "-1");

    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("radio", { name: "Limpo" })).toHaveFocus();
    expect(screen.getByRole("radio", { name: "Limpo" })).toHaveAttribute("aria-checked", "true");

    // "Caixa" is disabled: the next arrow wraps around to the first option.
    await user.keyboard("{ArrowRight}");
    expect(screen.getByRole("radio", { name: "Destaque" })).toHaveFocus();
    expect(screen.getByRole("radio", { name: "Destaque" })).toHaveAttribute("aria-checked", "true");

    await user.keyboard("{End}");
    expect(screen.getByRole("radio", { name: "Limpo" })).toHaveAttribute("aria-checked", "true");
  });
});

describe("UsageRow", () => {
  it("names its progressbar and announces the values and the threshold, not only a colour", () => {
    render(<UsageRow label="Renderizações" used={9} limit={10} format={(value) => String(value)} />);

    const bar = screen.getByRole("progressbar", { name: "Renderizações" });
    expect(bar).toHaveAttribute("aria-valuetext", "9 de 10 (90% do limite usado)");
    expect(screen.getByText("90% do limite usado")).toBeInTheDocument();
  });

  it("handles unlimited limits", () => {
    render(<UsageRow label="Projetos ativos" used={4} limit={0} format={(value) => String(value)} />);

    expect(screen.getByRole("progressbar", { name: "Projetos ativos" })).toHaveAttribute("aria-valuetext", "4 de ilimitado");
  });
});

describe("FormField", () => {
  it("exposes required fields in the accessible name", () => {
    render(
      <FormField id="email" label="E-mail" required>
        <Input id="email" />
      </FormField>,
    );

    // The visual asterisk is aria-hidden; the sr-only text carries the requirement
    // (jsdom's name computation drops the space a browser keeps between the two inline texts).
    expect(screen.getByRole("textbox", { name: /^E-mail\s*\(obrigatório\)$/ })).toBeInTheDocument();
  });
});
