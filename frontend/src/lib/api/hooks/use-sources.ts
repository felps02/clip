"use client";

import { useMutation } from "@tanstack/react-query";
import { validateSource } from "@/lib/api/sources";

export function useValidateSource() {
  return useMutation({ mutationFn: (url: string) => validateSource(url) });
}
