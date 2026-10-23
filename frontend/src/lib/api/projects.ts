import { api, buildUrl } from "@/lib/api/client";
import type {
  AnalysisSettings,
  Candidate,
  CandidatesQuery,
  CandidatesResponse,
  CreateClipRequest,
  CreateProjectRequest,
  CreateProjectResponse,
  Job,
  JobEvent,
  Paged,
  Project,
  ProjectDetail,
  ProjectSummary,
  ProjectsQuery,
  ClipDetail,
} from "@/lib/api/types";

const base = "/api/projects";
const id = (value: string) => encodeURIComponent(value);

/** `Idempotency-Key` must be generated once per form submission and reused on retries. */
export function createProject(body: CreateProjectRequest, idempotencyKey: string): Promise<CreateProjectResponse> {
  return api.post<CreateProjectResponse>(base, body, { headers: { "Idempotency-Key": idempotencyKey } });
}

export function listProjects(query: ProjectsQuery = {}): Promise<Paged<ProjectSummary>> {
  return api.get<Paged<ProjectSummary>>(base, {
    query: { page: query.page ?? 1, pageSize: query.pageSize ?? 20, status: query.status || undefined },
  });
}

/** Page size cap of the API (`Paging.MaxPageSize`). */
export const PROJECT_OPTIONS_PAGE_SIZE = 100;
/** Upper bound of pages fetched for a filter list (500 projects). */
export const PROJECT_OPTIONS_MAX_PAGES = 5;

export interface ProjectOption {
  id: string;
  title: string;
}

/** Lightweight project list for filters: ids and titles only, several pages, `total` for truncation notices. */
export async function listProjectOptions(): Promise<{ items: ProjectOption[]; total: number }> {
  const first = await listProjects({ page: 1, pageSize: PROJECT_OPTIONS_PAGE_SIZE });
  const items = [...first.items];
  let page = 1;
  while (items.length < first.total && page < PROJECT_OPTIONS_MAX_PAGES) {
    page += 1;
    const next = await listProjects({ page, pageSize: PROJECT_OPTIONS_PAGE_SIZE });
    if (next.items.length === 0) break;
    items.push(...next.items);
  }
  return { items: items.map((project) => ({ id: project.id, title: project.title })), total: Math.max(first.total, items.length) };
}

export function getProject(projectId: string): Promise<ProjectDetail> {
  return api.get<ProjectDetail>(`${base}/${id(projectId)}`);
}

export function updateProject(projectId: string, body: { title: string }): Promise<{ project: Project }> {
  return api.patch<{ project: Project }>(`${base}/${id(projectId)}`, body);
}

export function deleteProject(projectId: string): Promise<void> {
  return api.delete<void>(`${base}/${id(projectId)}`);
}

export function createJob(projectId: string, settings?: Partial<AnalysisSettings>): Promise<{ job: Job }> {
  return api.post<{ job: Job }>(`${base}/${id(projectId)}/jobs`, settings ? { settings } : {});
}

export function getJob(projectId: string, jobId: string): Promise<Job> {
  return api.get<Job>(`${base}/${id(projectId)}/jobs/${id(jobId)}`);
}

export function getJobEvents(projectId: string, jobId: string): Promise<{ items: JobEvent[] }> {
  return api.get<{ items: JobEvent[] }>(`${base}/${id(projectId)}/jobs/${id(jobId)}/events`);
}

export function cancelJob(projectId: string, jobId: string): Promise<{ job: Job }> {
  return api.post<{ job: Job }>(`${base}/${id(projectId)}/jobs/${id(jobId)}/cancel`);
}

export function listCandidates(projectId: string, query: CandidatesQuery = {}): Promise<CandidatesResponse> {
  return api.get<CandidatesResponse>(`${base}/${id(projectId)}/candidates`, {
    query: { sort: query.sort ?? "score", category: query.category || undefined },
  });
}

export function createClipFromCandidate(
  projectId: string,
  candidateId: string,
  body: CreateClipRequest,
): Promise<{ clip: ClipDetail; job: Job; candidate?: Candidate }> {
  return api.post<{ clip: ClipDetail; job: Job; candidate?: Candidate }>(
    `${base}/${id(projectId)}/candidates/${id(candidateId)}/clips`,
    body,
  );
}

/**
 * `GET /api/projects/{id}/clips/archive`: every ready clip of the project in one `.zip`, streamed
 * by the API with `Content-Disposition: attachment`. Used as a plain link (top-level navigation
 * carries the SameSite=Lax auth cookie), so it is built from the resolved API base URL.
 */
export function clipsArchiveUrl(projectId: string): string {
  return buildUrl(`${base}/${id(projectId)}/clips/archive`);
}
