/**
 * UUID v4 for client-generated identifiers such as the `Idempotency-Key` of `POST /api/projects`.
 *
 * `crypto.randomUUID()` only exists in secure contexts (https or localhost); on a plain-http LAN
 * origin it is undefined. `crypto.getRandomValues()` is available everywhere, so it backs the
 * fallback. `Math.random()` is a last resort for runtimes without Web Crypto: an idempotency key
 * only has to be unique per submission, it is not a secret.
 */
type CryptoLike = Partial<Pick<Crypto, "randomUUID" | "getRandomValues">>;

function formatV4(bytes: Uint8Array): string {
  bytes[6] = (bytes[6] & 0x0f) | 0x40; // version 4
  bytes[8] = (bytes[8] & 0x3f) | 0x80; // RFC 4122 variant
  const hex = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20, 32)}`;
}

/** `cryptoImpl` is injectable for tests; `null` means "no Web Crypto at all". */
export function randomUuid(cryptoImpl: CryptoLike | null = globalThis.crypto ?? null): string {
  if (cryptoImpl && typeof cryptoImpl.randomUUID === "function") {
    try {
      return cryptoImpl.randomUUID();
    } catch {
      // some embedded browsers expose the method but throw outside secure contexts
    }
  }
  const bytes = new Uint8Array(16);
  if (cryptoImpl && typeof cryptoImpl.getRandomValues === "function") {
    cryptoImpl.getRandomValues(bytes);
  } else {
    for (let index = 0; index < bytes.length; index += 1) bytes[index] = Math.floor(Math.random() * 256);
  }
  return formatV4(bytes);
}

export const UUID_V4_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
