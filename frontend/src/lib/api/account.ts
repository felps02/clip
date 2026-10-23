import { api } from "@/lib/api/client";
import type { Profile, ProfileUpdate, UsageResponse } from "@/lib/api/types";

export function getProfile(): Promise<Profile> {
  return api.get<Profile>("/api/account/profile");
}

export function updateProfile(body: ProfileUpdate): Promise<Profile> {
  return api.put<Profile>("/api/account/profile", body);
}

export function getUsage(): Promise<UsageResponse> {
  return api.get<UsageResponse>("/api/account/usage");
}

/** Schedules account deletion (202) and revokes sessions. */
export function deleteAccount(body: { password: string }): Promise<void> {
  return api.delete<void>("/api/account", body);
}
