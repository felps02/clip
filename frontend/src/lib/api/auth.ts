import { api, requestRaw } from "@/lib/api/client";
import type { AuthResponse, MeResponse, RegisterRequest, RegisterResult, User } from "@/lib/api/types";

export function login(body: { email: string; password: string }): Promise<AuthResponse> {
  return api.post<AuthResponse>("/api/auth/login", body);
}

/** 201 → signed in; 202 → e-mail confirmation required. */
export async function register(body: RegisterRequest): Promise<RegisterResult> {
  const response = await requestRaw<{ user?: User; message?: string } | string | undefined>("/api/auth/register", {
    method: "POST",
    body,
  });
  if (response.status === 202) {
    const message = typeof response.data === "string" ? response.data : response.data?.message;
    return { status: 202, message };
  }
  const user = typeof response.data === "object" ? response.data?.user : undefined;
  return { status: 201, user };
}

export function logout(): Promise<void> {
  return api.post<void>("/api/auth/logout", undefined, { skipAuthRefresh: true });
}

export function getMe(): Promise<MeResponse> {
  return api.get<MeResponse>("/api/auth/me");
}

export function forgotPassword(body: { email: string }): Promise<void> {
  return api.post<void>("/api/auth/forgot-password", body);
}

export function resetPassword(body: { email: string; token: string; newPassword: string }): Promise<void> {
  return api.post<void>("/api/auth/reset-password", body);
}

export function verifyEmail(body: { userId: string; token: string }): Promise<void> {
  return api.post<void>("/api/auth/verify-email", body);
}

export function resendVerification(): Promise<void> {
  return api.post<void>("/api/auth/resend-verification");
}

export function changePassword(body: { currentPassword: string; newPassword: string }): Promise<void> {
  return api.post<void>("/api/auth/change-password", body);
}
