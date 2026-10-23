import { describe, expect, it } from "vitest";
import {
  formatBytes,
  formatCurrency,
  formatDuration,
  formatElapsed,
  formatEta,
  formatMinutes,
  formatRelativeTime,
  formatTimecode,
  parseTimecode,
  truncate,
} from "@/lib/format";

describe("formatDuration", () => {
  it("formats mm:ss and h:mm:ss", () => {
    expect(formatDuration(0)).toBe("00:00");
    expect(formatDuration(32.4)).toBe("00:32");
    expect(formatDuration(754)).toBe("12:34");
    expect(formatDuration(3661)).toBe("1:01:01");
  });

  it("handles invalid input", () => {
    expect(formatDuration(null)).toBe("--:--");
    expect(formatDuration(Number.NaN)).toBe("--:--");
    expect(formatDuration(-5)).toBe("00:00");
  });
});

describe("timecodes", () => {
  it("formats tenths of a second", () => {
    expect(formatTimecode(0)).toBe("00:00.0");
    expect(formatTimecode(83.45)).toBe("01:23.5");
    expect(formatTimecode(3600.26)).toBe("1:00:00.3");
  });

  it("parses mm:ss.s, ss and h:mm:ss with comma decimals", () => {
    expect(parseTimecode("01:23.5")).toBeCloseTo(83.5);
    expect(parseTimecode("1:23,5")).toBeCloseTo(83.5);
    expect(parseTimecode("45")).toBe(45);
    expect(parseTimecode("1:00:00")).toBe(3600);
    expect(parseTimecode("")).toBeNull();
    expect(parseTimecode("abc")).toBeNull();
    expect(parseTimecode("1:2:3:4")).toBeNull();
  });
});

describe("formatBytes", () => {
  it("uses pt-BR separators and binary units", () => {
    expect(formatBytes(0)).toBe("0 B");
    expect(formatBytes(512)).toBe("512 B");
    expect(formatBytes(1536)).toBe("1,5 KB");
    expect(formatBytes(2 * 1024 ** 3)).toBe("2 GB");
    expect(formatBytes(null)).toBe("—");
  });
});

describe("minutes, currency and eta", () => {
  it("formats minutes from seconds", () => {
    expect(formatMinutes(90)).toBe("2 min");
    expect(formatMinutes(3900)).toBe("1 h 05 min");
    expect(formatMinutes(undefined)).toBe("—");
  });

  it("formats BRL currency from cents", () => {
    expect(formatCurrency(2990).replace(/ /g, " ")).toBe("R$ 29,90");
    expect(formatCurrency(null)).toBe("—");
  });

  it("formats an ETA", () => {
    expect(formatEta(30)).toBe("~30 s");
    expect(formatEta(150)).toBe("~3 min");
    expect(formatEta(7200)).toBe("~2 h");
    expect(formatEta(null)).toBe("—");
  });
});

describe("formatRelativeTime", () => {
  const now = new Date("2026-10-22T12:00:00Z").getTime();

  it("renders pt-BR relative phrases", () => {
    expect(formatRelativeTime("2026-10-22T11:59:50Z", now)).toBe("agora");
    expect(formatRelativeTime("2026-10-22T11:55:00Z", now)).toBe("há 5 minutos");
    expect(formatRelativeTime("2026-10-20T12:00:00Z", now)).toBe("anteontem");
    expect(formatRelativeTime("2026-10-24T12:00:00Z", now)).toBe("depois de amanhã");
    expect(formatRelativeTime(null, now)).toBe("—");
  });
});

describe("formatElapsed", () => {
  const now = new Date("2026-10-22T12:00:00Z").getTime();

  it("counts seconds under a minute and never says 'agora'", () => {
    expect(formatElapsed("2026-10-22T11:59:25Z", now)).toBe("há 35 segundos");
    expect(formatElapsed("2026-10-22T11:59:59Z", now)).toBe("há 1 segundo");
    // Not in the past by this clock (skew between browser and server): one second, not "agora" or "em …".
    expect(formatElapsed("2026-10-22T12:00:00Z", now)).toBe("há 1 segundo");
    expect(formatElapsed("2026-10-22T12:00:20Z", now)).toBe("há 1 segundo");
  });

  it("uses the relative wording from one minute on", () => {
    expect(formatElapsed("2026-10-22T11:59:00Z", now)).toBe("há 1 minuto");
    expect(formatElapsed("2026-10-22T11:55:00Z", now)).toBe("há 5 minutos");
    expect(formatElapsed("2026-10-20T12:00:00Z", now)).toBe("anteontem");
    expect(formatElapsed(null, now)).toBe("—");
  });
});

describe("truncate", () => {
  it("adds an ellipsis only when needed", () => {
    expect(truncate("curto", 10)).toBe("curto");
    expect(truncate("uma frase bem longa", 10)).toBe("uma frase…");
  });
});
