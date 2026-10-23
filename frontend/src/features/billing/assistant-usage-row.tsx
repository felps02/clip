import { UsageRow } from "@/components/ui/progress";
import type { UsageResponse } from "@/lib/api/types";
import { formatNumber } from "@/lib/format";

/**
 * Assistant chat messages of the period against the plan's monthly limit (0 = unlimited). Hidden
 * when the API does not report them (servers older than the assistant).
 */
export function AssistantUsageRow({ usage }: { usage: UsageResponse }) {
  const used = usage.used.assistantMessages;
  const limit = usage.plan.limits.assistantMessagesPerMonth;
  if (typeof used !== "number" && typeof limit !== "number") return null;
  return <UsageRow label="Mensagens do assistente" used={used ?? 0} limit={limit ?? 0} format={(value) => formatNumber(value)} />;
}
