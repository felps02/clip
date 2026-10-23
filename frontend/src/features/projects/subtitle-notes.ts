import type { Render, SignalsAvailability } from "@/lib/api/types";

/** Shown wherever subtitles were requested but the render came out without them. */
export const NO_SPEECH_SUBTITLES_TEXT = "Nenhuma fala detectada — legendas não geradas";

/** The analysis found no speech in the whole recording (transcript signal `speechDetected: false`). */
export function analysisFoundNoSpeech(signals?: Partial<SignalsAvailability> | null): boolean {
  return signals?.transcript?.speechDetected === false;
}

/**
 * Subtitles were requested, the render is ready and it has none: no words were transcribed in the
 * clip (silent gameplay, music only), so nothing could be burnt in.
 */
export function renderMissingSubtitles(
  subtitlesRequested: boolean | null | undefined,
  render?: Pick<Render, "status" | "hasSubtitles"> | null,
): boolean {
  return !!subtitlesRequested && render?.status === "ready" && render.hasSubtitles === false;
}
