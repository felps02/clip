"use client";

import { useQueryClient } from "@tanstack/react-query";
import { useRef, useState } from "react";
import { describeError, isApiError } from "@/lib/api/client";
import { completeUpload, createUpload } from "@/lib/api/media";
import { createProject } from "@/lib/api/projects";
import { queryKeys } from "@/lib/api/query-keys";
import type { AnalysisSettings } from "@/lib/api/types";
import { inferContentType } from "@/lib/media";
import { uploadFile, UploadError, type UploadHandle, type UploadProgress } from "@/lib/upload";
import { randomUuid } from "@/lib/uuid";

export type UploadStep = "create" | "upload" | "complete" | "project";

export type UploadPhase =
  | { kind: "idle" }
  | { kind: "creating" }
  | { kind: "uploading"; progress: UploadProgress }
  | { kind: "completing" }
  | { kind: "creating-project" }
  | { kind: "done"; projectId: string }
  | { kind: "error"; step: UploadStep; message: string; code?: string; retryable: boolean };

export interface SubmitInput {
  file: File;
  settings: AnalysisSettings;
  title?: string;
  declaredDurationSeconds?: number | null;
}

const STEP_LABELS: Record<UploadStep, string> = {
  create: "Preparando o envio",
  upload: "Enviando o arquivo",
  complete: "Validando o arquivo",
  project: "Criando o projeto",
};

export function uploadStepLabel(step: UploadStep): string {
  return STEP_LABELS[step];
}

/**
 * Upload → complete → create project. The `Idempotency-Key` is generated once per submission
 * and reused if the project-creation step is retried.
 */
export function useUploadFlow() {
  const queryClient = useQueryClient();
  const [phase, setPhase] = useState<UploadPhase>({ kind: "idle" });
  const [warnings, setWarnings] = useState<string[]>([]);
  const uploadRef = useRef<UploadHandle | null>(null);
  const pendingProjectRef = useRef<{ assetId: string; idempotencyKey: string; input: SubmitInput } | null>(null);

  const createProjectStep = async (assetId: string, idempotencyKey: string, input: SubmitInput) => {
    setPhase({ kind: "creating-project" });
    const result = await createProject(
      {
        title: input.title?.trim() || undefined,
        source: { kind: "upload", mediaAssetId: assetId },
        settings: input.settings,
      },
      idempotencyKey,
    );
    pendingProjectRef.current = null;
    void queryClient.invalidateQueries({ queryKey: queryKeys.projects.all });
    void queryClient.invalidateQueries({ queryKey: queryKeys.dashboard.summary });
    setPhase({ kind: "done", projectId: result.project.id });
    return result.project.id;
  };

  const fail = (step: UploadStep, error: unknown, retryable: boolean) => {
    setPhase({
      kind: "error",
      step,
      message: describeError(error),
      code: isApiError(error) ? error.code : undefined,
      retryable,
    });
  };

  const submit = async (input: SubmitInput): Promise<string | null> => {
    // randomUuid falls back to crypto.getRandomValues on plain-http origins (no crypto.randomUUID there).
    const idempotencyKey = randomUuid();
    setWarnings([]);
    setPhase({ kind: "creating" });

    let assetId: string;
    let target: { url: string; method: string; headers: Record<string, string> };
    try {
      const created = await createUpload({
        fileName: input.file.name,
        sizeBytes: input.file.size,
        contentType: inferContentType(input.file),
        declaredDurationSeconds:
          input.declaredDurationSeconds && input.declaredDurationSeconds > 0
            ? Math.round(input.declaredDurationSeconds)
            : undefined,
      });
      assetId = created.asset.id;
      target = {
        url: created.upload.url,
        method: created.upload.method || "PUT",
        headers: created.upload.headers ?? {},
      };
      setWarnings(created.warnings ?? []);
    } catch (error) {
      fail("create", error, true);
      return null;
    }

    try {
      setPhase({
        kind: "uploading",
        progress: { loadedBytes: 0, totalBytes: input.file.size, percent: 0, bytesPerSecond: 0, etaSeconds: null },
      });
      const handle = uploadFile({
        url: target.url,
        method: target.method,
        headers: target.headers,
        file: input.file,
        onProgress: (progress) => setPhase({ kind: "uploading", progress }),
      });
      uploadRef.current = handle;
      await handle.promise;
    } catch (error) {
      uploadRef.current = null;
      if (error instanceof UploadError && error.aborted) {
        setPhase({ kind: "idle" });
        return null;
      }
      fail("upload", error, true);
      return null;
    }
    uploadRef.current = null;

    try {
      setPhase({ kind: "completing" });
      await completeUpload(assetId);
    } catch (error) {
      fail("complete", error, false);
      return null;
    }

    pendingProjectRef.current = { assetId, idempotencyKey, input };
    try {
      return await createProjectStep(assetId, idempotencyKey, input);
    } catch (error) {
      fail("project", error, true);
      return null;
    }
  };

  /** Retries only the project-creation step (same asset, same Idempotency-Key). */
  const retryProject = async (): Promise<string | null> => {
    const pending = pendingProjectRef.current;
    if (!pending) return null;
    try {
      return await createProjectStep(pending.assetId, pending.idempotencyKey, pending.input);
    } catch (error) {
      fail("project", error, true);
      return null;
    }
  };

  const cancel = () => {
    uploadRef.current?.abort();
  };

  const reset = () => {
    uploadRef.current?.abort();
    uploadRef.current = null;
    pendingProjectRef.current = null;
    setWarnings([]);
    setPhase({ kind: "idle" });
  };

  const busy =
    phase.kind === "creating" || phase.kind === "uploading" || phase.kind === "completing" || phase.kind === "creating-project";

  return {
    phase,
    warnings,
    busy,
    canRetryProject: phase.kind === "error" && phase.step === "project",
    submit,
    retryProject,
    cancel,
    reset,
  };
}
