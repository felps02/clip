"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { deleteAccount, getProfile, getUsage, updateProfile } from "@/lib/api/account";
import { queryKeys } from "@/lib/api/query-keys";

export function useProfile() {
  return useQuery({ queryKey: queryKeys.account.profile, queryFn: getProfile, staleTime: 60_000 });
}

export function useUpdateProfile() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: updateProfile,
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.account.profile });
      void queryClient.invalidateQueries({ queryKey: queryKeys.auth.me });
    },
  });
}

export function useUsage() {
  return useQuery({ queryKey: queryKeys.account.usage, queryFn: getUsage, staleTime: 30_000 });
}

export function useDeleteAccount() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: deleteAccount,
    onSuccess: () => {
      queryClient.clear();
    },
  });
}
