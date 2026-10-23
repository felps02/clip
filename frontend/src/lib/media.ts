/** Client-side helpers for picking and inspecting media files before upload. */

export const ACCEPTED_EXTENSIONS = ["mp4", "mov", "mkv", "webm", "mp3", "wav"] as const;
export type AcceptedExtension = (typeof ACCEPTED_EXTENSIONS)[number];

export const ACCEPT_ATTRIBUTE =
  ".mp4,.mov,.mkv,.webm,.mp3,.wav,video/mp4,video/quicktime,video/x-matroska,video/webm,audio/mpeg,audio/wav";

const CONTENT_TYPES: Record<AcceptedExtension, string> = {
  mp4: "video/mp4",
  mov: "video/quicktime",
  mkv: "video/x-matroska",
  webm: "video/webm",
  mp3: "audio/mpeg",
  wav: "audio/wav",
};

export function fileExtension(fileName: string): string {
  const index = fileName.lastIndexOf(".");
  return index === -1 ? "" : fileName.slice(index + 1).toLowerCase();
}

export function isAcceptedFile(file: File): boolean {
  return (ACCEPTED_EXTENSIONS as readonly string[]).includes(fileExtension(file.name));
}

/** Content type accepted by the API, inferred from the extension (browsers often report an empty type). */
export function inferContentType(file: File): string {
  const ext = fileExtension(file.name) as AcceptedExtension;
  return CONTENT_TYPES[ext] ?? (file.type || "application/octet-stream");
}

export function isAudioOnly(file: File): boolean {
  const ext = fileExtension(file.name);
  return ext === "mp3" || ext === "wav";
}

/**
 * Read the media duration through a hidden <video>/<audio> element.
 * Resolves `null` when the browser cannot decode the container (e.g. MKV) or when it times out.
 */
export function readMediaDuration(file: File, timeoutMs = 15_000): Promise<number | null> {
  if (typeof document === "undefined" || typeof URL === "undefined" || !("createObjectURL" in URL)) {
    return Promise.resolve(null);
  }
  return new Promise((resolve) => {
    const element = document.createElement(isAudioOnly(file) ? "audio" : "video");
    const url = URL.createObjectURL(file);
    let settled = false;

    const finish = (value: number | null) => {
      if (settled) return;
      settled = true;
      window.clearTimeout(timer);
      element.removeAttribute("src");
      element.load();
      URL.revokeObjectURL(url);
      element.remove();
      resolve(value);
    };

    const timer = window.setTimeout(() => finish(null), timeoutMs);

    element.preload = "metadata";
    element.muted = true;
    element.style.position = "absolute";
    element.style.width = "0";
    element.style.height = "0";
    element.style.opacity = "0";
    element.setAttribute("aria-hidden", "true");
    element.addEventListener("loadedmetadata", () => {
      const duration = element.duration;
      finish(Number.isFinite(duration) && duration > 0 ? duration : null);
    });
    element.addEventListener("error", () => finish(null));
    document.body.appendChild(element);
    element.src = url;
  });
}
