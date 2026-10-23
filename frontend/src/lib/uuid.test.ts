import { describe, expect, it, vi } from "vitest";
import { randomUuid, UUID_V4_PATTERN } from "@/lib/uuid";

describe("randomUuid", () => {
  it("uses crypto.randomUUID when the context provides it", () => {
    const randomUUID = vi.fn(() => "11111111-2222-4333-8444-555555555555" as const);
    expect(randomUuid({ randomUUID, getRandomValues: vi.fn() as unknown as Crypto["getRandomValues"] })).toBe(
      "11111111-2222-4333-8444-555555555555",
    );
    expect(randomUUID).toHaveBeenCalledTimes(1);
  });

  it("falls back to crypto.getRandomValues outside secure contexts (no randomUUID)", () => {
    const getRandomValues = vi.fn(<T extends ArrayBufferView | null>(array: T): T => {
      const bytes = array as unknown as Uint8Array;
      for (let index = 0; index < bytes.length; index += 1) bytes[index] = 0xff;
      return array;
    });

    const id = randomUuid({ getRandomValues: getRandomValues as unknown as Crypto["getRandomValues"] });

    expect(getRandomValues).toHaveBeenCalledTimes(1);
    expect(id).toMatch(UUID_V4_PATTERN);
    // version and variant bits are forced even when every random byte is 0xff
    expect(id).toBe("ffffffff-ffff-4fff-bfff-ffffffffffff");
  });

  it("falls back when randomUUID throws and still yields distinct v4 ids", () => {
    const real = globalThis.crypto;
    const cryptoImpl = {
      randomUUID: () => {
        throw new TypeError("not available in insecure contexts");
      },
      getRandomValues: real.getRandomValues.bind(real),
    } as unknown as Crypto;

    const ids = new Set(Array.from({ length: 50 }, () => randomUuid(cryptoImpl)));

    expect(ids.size).toBe(50);
    for (const id of ids) expect(id).toMatch(UUID_V4_PATTERN);
  });

  it("still produces a v4-shaped id without Web Crypto", () => {
    expect(randomUuid(null)).toMatch(UUID_V4_PATTERN);
    expect(randomUuid({})).toMatch(UUID_V4_PATTERN);
  });
});
