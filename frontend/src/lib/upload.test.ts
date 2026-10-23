import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { uploadFile, UploadError, type UploadProgress } from "@/lib/upload";

class MockXHR {
  static instances: MockXHR[] = [];
  upload: { onprogress: ((event: ProgressEvent) => void) | null } = { onprogress: null };
  onload: (() => void) | null = null;
  onerror: (() => void) | null = null;
  onabort: (() => void) | null = null;
  ontimeout: (() => void) | null = null;
  status = 200;
  timeout = 0;
  withCredentials = false;
  open = vi.fn();
  send = vi.fn();
  setRequestHeader = vi.fn();
  abort = vi.fn(() => {
    this.onabort?.();
  });

  constructor() {
    MockXHR.instances.push(this);
  }

  emitProgress(loaded: number, total: number) {
    this.upload.onprogress?.({ lengthComputable: true, loaded, total } as ProgressEvent);
  }
}

beforeEach(() => {
  MockXHR.instances = [];
  vi.stubGlobal("XMLHttpRequest", MockXHR);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const file = new Blob([new Uint8Array(1000)], { type: "video/mp4" });

describe("uploadFile", () => {
  it("PUTs the file with headers, reports progress and resolves on 2xx", async () => {
    const progress: UploadProgress[] = [];
    let clock = 1000;
    const handle = uploadFile({
      url: "https://storage.test/upload?sig=1",
      headers: { "Content-Type": "video/mp4", "x-amz-acl": "private" },
      file,
      onProgress: (p) => progress.push(p),
      now: () => clock,
    });
    const xhr = MockXHR.instances[0];

    expect(xhr.open).toHaveBeenCalledWith("PUT", "https://storage.test/upload?sig=1", true);
    expect(xhr.setRequestHeader).toHaveBeenCalledWith("Content-Type", "video/mp4");
    expect(xhr.setRequestHeader).toHaveBeenCalledWith("x-amz-acl", "private");
    expect(xhr.send).toHaveBeenCalledWith(file);

    xhr.emitProgress(0, 1000);
    clock = 2000;
    xhr.emitProgress(500, 1000);
    xhr.status = 200;
    xhr.onload?.();

    await expect(handle.promise).resolves.toBeUndefined();
    expect(progress.map((p) => p.percent)).toEqual([0, 50, 100]);
    expect(progress[1].bytesPerSecond).toBe(500);
    expect(progress[1].etaSeconds).toBe(1);
    expect(progress.at(-1)).toMatchObject({ loadedBytes: 1000, totalBytes: 1000, percent: 100 });
  });

  it("rejects with the HTTP status when storage refuses the upload", async () => {
    const handle = uploadFile({ url: "https://storage.test/upload", file });
    const xhr = MockXHR.instances[0];
    xhr.status = 403;
    xhr.onload?.();

    await expect(handle.promise).rejects.toSatisfy((error: unknown) => {
      expect(error).toBeInstanceOf(UploadError);
      expect((error as UploadError).status).toBe(403);
      expect((error as UploadError).aborted).toBe(false);
      return true;
    });
  });

  it("aborts through the handle and rejects with aborted=true", async () => {
    const handle = uploadFile({ url: "https://storage.test/upload", file });
    const xhr = MockXHR.instances[0];

    handle.abort();

    expect(xhr.abort).toHaveBeenCalledTimes(1);
    await expect(handle.promise).rejects.toMatchObject({ name: "UploadError", aborted: true });
  });

  it("rejects on network errors", async () => {
    const handle = uploadFile({ url: "https://storage.test/upload", file });
    MockXHR.instances[0].onerror?.();

    await expect(handle.promise).rejects.toBeInstanceOf(UploadError);
  });
});
