import { api } from "@/lib/api/client";
import type { CreateUploadRequest, CreateUploadResponse, MediaAsset } from "@/lib/api/types";

export function createUpload(body: CreateUploadRequest): Promise<CreateUploadResponse> {
  return api.post<CreateUploadResponse>("/api/media/uploads", body);
}

export function completeUpload(assetId: string): Promise<{ asset: MediaAsset }> {
  return api.post<{ asset: MediaAsset }>(`/api/media/uploads/${encodeURIComponent(assetId)}/complete`);
}

export function getAsset(assetId: string): Promise<{ asset: MediaAsset }> {
  return api.get<{ asset: MediaAsset }>(`/api/media/${encodeURIComponent(assetId)}`);
}

export function deleteAsset(assetId: string): Promise<void> {
  return api.delete<void>(`/api/media/${encodeURIComponent(assetId)}`);
}
