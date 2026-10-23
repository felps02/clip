import { api } from "@/lib/api/client";
import type { AssistantChatRequest, AssistantChatResponse, AssistantStatus } from "@/lib/api/types";

/** `GET /api/assistant/status` → `{ available, provider, model, reason? }`. */
export function getAssistantStatus(): Promise<AssistantStatus> {
  return api.get<AssistantStatus>("/api/assistant/status");
}

/**
 * `POST /api/assistant/chat`. Errors: `503 assistant_unavailable`, `429 rate_limited`,
 * `409 plan_limit` (monthly assistant messages).
 */
export function sendAssistantChat(body: AssistantChatRequest, signal?: AbortSignal): Promise<AssistantChatResponse> {
  return api.post<AssistantChatResponse>("/api/assistant/chat", body, { signal });
}
