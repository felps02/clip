import { act, render, screen } from "@testing-library/react";
import { hydrateRoot } from "react-dom/client";
import { renderToString } from "react-dom/server";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ElapsedTime, RelativeTime } from "@/components/ui/relative-time";
import { formatDateTime } from "@/lib/format";

const T0 = new Date("2026-10-23T21:00:00Z").getTime();
const secondsBefore = (seconds: number) => new Date(T0 - seconds * 1000).toISOString();

async function advance(ms: number) {
  await act(async () => {
    await vi.advanceTimersByTimeAsync(ms);
  });
}

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(T0);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("RelativeTime", () => {
  it("follows the clock (every 15 s) while the value stays the same", async () => {
    render(<RelativeTime value={secondsBefore(30)} />);
    expect(screen.getByText("agora")).toBeInTheDocument();

    await advance(15_000);
    expect(screen.getByText("há 45 segundos")).toBeInTheDocument();
    await advance(15_000);
    expect(screen.getByText("há 1 minuto")).toBeInTheDocument();
    await advance(5 * 60_000);
    expect(screen.getByText("há 6 minutos")).toBeInTheDocument();
  });
});

describe("ElapsedTime", () => {
  it("counts the seconds since the event and never says 'agora'", async () => {
    render(<ElapsedTime value={secondsBefore(35)} />);
    const time = screen.getByText("há 35 segundos");
    expect(time.tagName).toBe("TIME");
    expect(time).toHaveAttribute("title", formatDateTime(secondsBefore(35)));

    await advance(1_000);
    expect(time).toHaveTextContent("há 36 segundos");
    await advance(24_000);
    expect(time).toHaveTextContent("há 1 minuto");
    await advance(10 * 60_000);
    expect(time).toHaveTextContent("há 11 minutos");
  });

  it("says 'há 1 segundo' for an event that is not in the past by this clock", () => {
    render(
      <>
        <ElapsedTime value={new Date(T0).toISOString()} />
        <ElapsedTime value={new Date(T0 + 20_000).toISOString()} />
      </>,
    );
    expect(screen.getAllByText("há 1 segundo")).toHaveLength(2);
    expect(screen.queryByText("agora")).not.toBeInTheDocument();
  });

  it("renders the absolute date on the server (relative text only after hydration)", () => {
    const html = renderToString(<ElapsedTime value={secondsBefore(35)} />);
    expect(html).toContain(formatDateTime(secondsBefore(35)));
    expect(html).not.toContain("segundos");
  });
});

describe("shared clock", () => {
  it("runs a timer only while something on screen reads it", async () => {
    expect(vi.getTimerCount()).toBe(0);
    const { unmount } = render(
      <>
        <RelativeTime value={secondsBefore(30)} />
        <RelativeTime value={secondsBefore(90)} />
        <ElapsedTime value={secondsBefore(35)} />
        <ElapsedTime value={secondsBefore(40)} />
      </>,
    );
    // One timer per clock, however many components read it.
    expect(vi.getTimerCount()).toBe(2);

    unmount();
    expect(vi.getTimerCount()).toBe(0);
  });

  it("shows the current time, not the last tick, when it is read again after a pause", async () => {
    const first = render(<ElapsedTime value={new Date(T0).toISOString()} />);
    expect(screen.getByText("há 1 segundo")).toBeInTheDocument();
    first.unmount();

    // Ten minutes go by with nothing on screen (no tick).
    vi.setSystemTime(T0 + 10 * 60_000);
    render(<ElapsedTime value={new Date(T0).toISOString()} />);
    expect(screen.getByText("há 10 minutos")).toBeInTheDocument();
  });

  it("is current right after hydration, even when the clock was last read long before", async () => {
    const value = new Date(T0).toISOString();
    render(<RelativeTime value={value} />).unmount();
    vi.setSystemTime(T0 + 10 * 60_000);

    // Hydration subscribes to the clock before reading it.
    const container = document.createElement("div");
    container.innerHTML = renderToString(<RelativeTime value={value} />);
    expect(container).toHaveTextContent(formatDateTime(value));
    document.body.appendChild(container);
    let root: ReturnType<typeof hydrateRoot> | undefined;
    await act(async () => {
      root = hydrateRoot(container, <RelativeTime value={value} />);
    });

    expect(container).toHaveTextContent("há 10 minutos");
    act(() => root?.unmount());
    container.remove();
  });
});
