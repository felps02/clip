import { z } from "zod";

/** Password policy from the API contract: at least 8 chars, one letter and one digit. */
export const passwordSchema = z
  .string()
  .min(8, { error: "A senha precisa ter pelo menos 8 caracteres" })
  .regex(/[A-Za-z]/, { error: "A senha precisa ter pelo menos uma letra" })
  .regex(/\d/, { error: "A senha precisa ter pelo menos um número" });

export const emailSchema = z
  .string()
  .trim()
  .min(1, { error: "Informe o seu e-mail" })
  .pipe(z.email({ error: "Informe um e-mail válido" }));

export const displayNameSchema = z
  .string()
  .trim()
  .min(2, { error: "O nome precisa ter pelo menos 2 caracteres" })
  .max(60, { error: "O nome pode ter no máximo 60 caracteres" });

export const loginSchema = z.object({
  email: emailSchema,
  password: z.string().min(1, { error: "Informe a sua senha" }),
});
export type LoginInput = z.infer<typeof loginSchema>;

export const registerSchema = z.object({
  displayName: displayNameSchema,
  email: emailSchema,
  password: passwordSchema,
  acceptedTerms: z.boolean().refine((value) => value === true, {
    error: "Você precisa aceitar os termos e confirmar que tem direito de processar o conteúdo",
  }),
});
export type RegisterInput = z.infer<typeof registerSchema>;

export const forgotPasswordSchema = z.object({ email: emailSchema });
export type ForgotPasswordInput = z.infer<typeof forgotPasswordSchema>;

export const resetPasswordSchema = z
  .object({
    newPassword: passwordSchema,
    confirmPassword: z.string(),
  })
  .refine((data) => data.newPassword === data.confirmPassword, {
    error: "As senhas não coincidem",
    path: ["confirmPassword"],
  });
export type ResetPasswordInput = z.infer<typeof resetPasswordSchema>;

export const changePasswordSchema = z
  .object({
    currentPassword: z.string().min(1, { error: "Informe a senha atual" }),
    newPassword: passwordSchema,
    confirmPassword: z.string(),
  })
  .refine((data) => data.newPassword === data.confirmPassword, {
    error: "As senhas não coincidem",
    path: ["confirmPassword"],
  });
export type ChangePasswordInput = z.infer<typeof changePasswordSchema>;

/** The interface language is not editable (pt-BR only); see INTERFACE_LOCALE in src/lib/preferences.ts. */
export const profileSchema = z.object({
  displayName: displayNameSchema,
  timezone: z.string().optional(),
});
export type ProfileInput = z.infer<typeof profileSchema>;

export const deleteAccountSchema = z.object({
  password: z.string().min(1, { error: "Informe a sua senha para confirmar" }),
  confirmation: z.boolean().refine((value) => value === true, {
    error: "Confirme que entende que a exclusão é definitiva",
  }),
});
export type DeleteAccountInput = z.infer<typeof deleteAccountSchema>;

export const linkSchema = z.object({
  url: z
    .string()
    .trim()
    .min(1, { error: "Cole o link da transmissão" })
    .pipe(z.url({ error: "Informe um link válido (começando com http:// ou https://)" })),
});
export type LinkInput = z.infer<typeof linkSchema>;
