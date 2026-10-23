import { api } from "@/lib/api/client";
import type {
  CheckoutRequest,
  CheckoutResponse,
  PageQuery,
  Paged,
  PaymentEvent,
  Plan,
  PlansResponse,
  PortalResponse,
  SubscriptionResponse,
} from "@/lib/api/types";

/** Public endpoint (no auth). */
export async function listPlans(): Promise<Plan[]> {
  const data = await api.get<PlansResponse | Plan[]>("/api/plans", { skipAuthRefresh: true });
  return Array.isArray(data) ? data : (data?.items ?? []);
}

export function getSubscription(): Promise<SubscriptionResponse> {
  return api.get<SubscriptionResponse>("/api/billing/subscription");
}

export function createCheckout(body: CheckoutRequest): Promise<CheckoutResponse> {
  return api.post<CheckoutResponse>("/api/billing/checkout", body);
}

export function createPortalSession(): Promise<PortalResponse> {
  return api.post<PortalResponse>("/api/billing/portal");
}

/** Cancels at the end of the current period (202). */
export function cancelSubscription(): Promise<void> {
  return api.post<void>("/api/billing/cancel");
}

export function listPayments(query: PageQuery = {}): Promise<Paged<PaymentEvent>> {
  return api.get<Paged<PaymentEvent>>("/api/billing/payments", {
    query: { page: query.page ?? 1, pageSize: query.pageSize ?? 20 },
  });
}
