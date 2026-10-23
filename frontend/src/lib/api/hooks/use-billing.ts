"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  cancelSubscription,
  createCheckout,
  createPortalSession,
  getSubscription,
  listPayments,
  listPlans,
} from "@/lib/api/billing";
import { queryKeys } from "@/lib/api/query-keys";
import type { PageQuery } from "@/lib/api/types";

export function usePlans() {
  return useQuery({
    queryKey: queryKeys.billing.plans,
    queryFn: listPlans,
    staleTime: 5 * 60_000,
    retry: 1,
  });
}

export function useSubscription(options: { refetchInterval?: number | false } = {}) {
  return useQuery({
    queryKey: queryKeys.billing.subscription,
    queryFn: getSubscription,
    staleTime: 30_000,
    refetchInterval: options.refetchInterval ?? false,
  });
}

export function usePayments(query: PageQuery) {
  return useQuery({
    queryKey: queryKeys.billing.payments(query),
    queryFn: () => listPayments(query),
    placeholderData: (previous) => previous,
  });
}

export function useCheckout() {
  return useMutation({ mutationFn: createCheckout });
}

export function usePortalSession() {
  return useMutation({ mutationFn: createPortalSession });
}

export function useCancelSubscription() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: cancelSubscription,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.billing.subscription });
      void queryClient.invalidateQueries({ queryKey: queryKeys.auth.me });
    },
  });
}
