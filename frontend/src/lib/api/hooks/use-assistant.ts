"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { getAssistantStatus, sendAssistantChat } from "@/lib/api/assistant";
import { queryKeys } from "@/lib/api/query-keys";
import type { AssistantChatRequest } from "@/lib/api/types";

/** The API caches the status for a few seconds; the web re-checks at most every 30 s. */
export function useAssistantStatus(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.assistant.status,
    queryFn: getAssistantStatus,
    staleTime: 30_000,
    retry: false,
    enabled: options.enabled ?? true,
  });
}

export function useAssistantChat() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (body: AssistantChatRequest) => sendAssistantChat(body),
    onSettled: () => {
      // Each answered message counts in the plan's monthly assistant usage.
      void queryClient.invalidateQueries({ queryKey: queryKeys.account.usage });
    },
  });
}
