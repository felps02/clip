"use client";

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  changePassword,
  forgotPassword,
  getMe,
  login,
  logout,
  register,
  resendVerification,
  resetPassword,
  verifyEmail,
} from "@/lib/api/auth";
import { queryKeys } from "@/lib/api/query-keys";

export function useMe(options: { enabled?: boolean } = {}) {
  return useQuery({
    queryKey: queryKeys.auth.me,
    queryFn: getMe,
    staleTime: 5 * 60_000,
    retry: false,
    enabled: options.enabled ?? true,
  });
}

export function useLogin() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: login,
    onSuccess: () => {
      queryClient.clear();
    },
  });
}

export function useRegister() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: register,
    onSuccess: () => {
      queryClient.clear();
    },
  });
}

/**
 * Explicit sign-out. The cache is only dropped once the API confirmed that the cookies are
 * cleared: after a failed logout the session is still alive and the app keeps working as before.
 */
export function useLogout() {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: logout,
    onSuccess: () => {
      queryClient.clear();
    },
  });
}

export function useForgotPassword() {
  return useMutation({ mutationFn: forgotPassword });
}

export function useResetPassword() {
  return useMutation({ mutationFn: resetPassword });
}

export function useVerifyEmail() {
  return useMutation({ mutationFn: verifyEmail });
}

export function useResendVerification() {
  return useMutation({ mutationFn: resendVerification });
}

export function useChangePassword() {
  return useMutation({ mutationFn: changePassword });
}
