import { api } from "@/lib/api/client";
import type {
  ClipDetail,
  ClipSummary,
  ClipsQuery,
  DownloadResponse,
  Paged,
  RenderClipResponse,
  UpdateClipRequest,
  UpdateClipResponse,
} from "@/lib/api/types";

const base = "/api/clips";
const id = (value: string) => encodeURIComponent(value);

export function listClips(query: ClipsQuery = {}): Promise<Paged<ClipSummary>> {
  return api.get<Paged<ClipSummary>>(base, {
    query: {
      page: query.page ?? 1,
      pageSize: query.pageSize ?? 24,
      projectId: query.projectId || undefined,
      category: query.category || undefined,
      status: query.status || undefined,
      q: query.q?.trim() || undefined,
      sort: query.sort ?? "createdAt",
      order: query.order ?? "desc",
    },
  });
}

export function getClip(clipId: string): Promise<ClipDetail> {
  return api.get<ClipDetail>(`${base}/${id(clipId)}`);
}

export function updateClip(clipId: string, body: UpdateClipRequest): Promise<UpdateClipResponse> {
  return api.patch<UpdateClipResponse>(`${base}/${id(clipId)}`, body);
}

export function renderClip(clipId: string): Promise<RenderClipResponse> {
  return api.post<RenderClipResponse>(`${base}/${id(clipId)}/render`);
}

export function getClipDownload(clipId: string, renderId?: string): Promise<DownloadResponse> {
  return api.get<DownloadResponse>(`${base}/${id(clipId)}/download`, { query: { renderId } });
}

export function deleteClip(clipId: string): Promise<void> {
  return api.delete<void>(`${base}/${id(clipId)}`);
}
