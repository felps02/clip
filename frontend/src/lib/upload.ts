/**
 * Direct-to-storage upload through XMLHttpRequest (fetch has no upload progress events).
 */
export interface UploadProgress {
  loadedBytes: number;
  totalBytes: number;
  /** 0-100 */
  percent: number;
  /** Moving average over the last few seconds. */
  bytesPerSecond: number;
  /** Estimated seconds remaining (null until the speed is known). */
  etaSeconds: number | null;
}

export interface UploadOptions {
  url: string;
  method?: string;
  headers?: Record<string, string>;
  file: Blob;
  onProgress?: (progress: UploadProgress) => void;
  /** Send cookies (needed for the local storage endpoint on the same host). */
  withCredentials?: boolean;
  timeoutMs?: number;
  /** Injectable clock (tests). */
  now?: () => number;
}

export interface UploadHandle {
  promise: Promise<void>;
  abort: () => void;
}

export class UploadError extends Error {
  readonly status: number | undefined;
  readonly aborted: boolean;

  constructor(message: string, options: { status?: number; aborted?: boolean } = {}) {
    super(message);
    this.name = "UploadError";
    this.status = options.status;
    this.aborted = options.aborted ?? false;
  }
}

const SPEED_WINDOW_MS = 4000;

export function uploadFile(options: UploadOptions): UploadHandle {
  const { url, file, headers = {}, method = "PUT", onProgress, withCredentials = false, timeoutMs = 0 } = options;
  const now = options.now ?? (() => Date.now());
  const xhr = new XMLHttpRequest();
  const samples: Array<{ at: number; loaded: number }> = [];

  const promise = new Promise<void>((resolve, reject) => {
    xhr.open(method, url, true);
    xhr.withCredentials = withCredentials;
    if (timeoutMs > 0) xhr.timeout = timeoutMs;
    for (const [name, value] of Object.entries(headers)) {
      if (value !== undefined && value !== null) xhr.setRequestHeader(name, value);
    }

    xhr.upload.onprogress = (event: ProgressEvent) => {
      if (!onProgress) return;
      const total = event.lengthComputable && event.total > 0 ? event.total : file.size;
      const loaded = Math.min(event.loaded, total);
      const at = now();
      samples.push({ at, loaded });
      while (samples.length > 1 && at - samples[0].at > SPEED_WINDOW_MS) samples.shift();
      const first = samples[0];
      const elapsed = (at - first.at) / 1000;
      const bytesPerSecond = elapsed > 0 ? (loaded - first.loaded) / elapsed : 0;
      const remaining = total - loaded;
      onProgress({
        loadedBytes: loaded,
        totalBytes: total,
        percent: total > 0 ? Math.min(100, Math.round((loaded / total) * 1000) / 10) : 0,
        bytesPerSecond,
        etaSeconds: bytesPerSecond > 0 ? remaining / bytesPerSecond : null,
      });
    };

    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        onProgress?.({
          loadedBytes: file.size,
          totalBytes: file.size,
          percent: 100,
          bytesPerSecond: 0,
          etaSeconds: 0,
        });
        resolve();
      } else {
        reject(new UploadError(`O armazenamento recusou o envio (HTTP ${xhr.status})`, { status: xhr.status }));
      }
    };
    xhr.onerror = () => reject(new UploadError("Falha de rede durante o envio do arquivo"));
    xhr.ontimeout = () => reject(new UploadError("O envio demorou demais e foi interrompido"));
    xhr.onabort = () => reject(new UploadError("Envio cancelado", { aborted: true }));

    xhr.send(file);
  });

  return {
    promise,
    abort: () => xhr.abort(),
  };
}
