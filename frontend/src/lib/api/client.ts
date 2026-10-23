import type { ProblemDetails } from "@/lib/api/types";
import { UploadError } from "@/lib/upload";

/**
 * Thin fetch wrapper for the ClipForge API.
 *
 * - Always sends cookies (`credentials: "include"`); tokens never touch JS storage.
 * - Parses RFC 9457 problem details into `ApiError`.
 * - On 401 it calls `POST /api/auth/refresh` ONCE and retries the original request.
 *   Concurrent 401s share a single refresh request. Credential endpoints (login,
 *   register, refresh, logout, password reset, e-mail verification) never trigger a refresh.
 */

/** Placeholder in `NEXT_PUBLIC_API_BASE_URL` replaced by the host name the page was opened with. */
export const API_HOST_TOKEN = "{host}";

/**
 * Used when `NEXT_PUBLIC_API_BASE_URL` is unset: the API on port 5080 of the host that served the
 * page, so http://localhost:3000 talks to http://localhost:5080 and a LAN demo opened at
 * http://10.0.0.57:3000 talks to http://10.0.0.57:5080 (same site, the auth cookies keep working)
 * without rebuilding the bundle for each address.
 */
export const DEFAULT_API_BASE_URL = `http://${API_HOST_TOKEN}:5080`;

/** Host name that replaces `{host}`: the page's host in the browser, `localhost` during SSR. */
export function currentHostname(): string {
  if (typeof window === "undefined") return "localhost";
  return window.location?.hostname || "localhost";
}

/**
 * Resolves an API base URL template: `{host}` (any number of times) becomes `hostname`, trailing
 * slashes are dropped, and an empty or missing template falls back to `DEFAULT_API_BASE_URL`.
 * IPv6 literals are bracketed (`window.location.hostname` already brackets them).
 */
export function resolveApiBaseUrl(template: string | null | undefined, hostname: string): string {
  const raw = template?.trim() || DEFAULT_API_BASE_URL;
  const safeHost = hostname.includes(":") && !hostname.startsWith("[") ? `[${hostname}]` : hostname || "localhost";
  return raw.split(API_HOST_TOKEN).join(safeHost).replace(/\/+$/, "");
}

/**
 * The API base URL every request, link and download of the web uses. Resolved on each call (cheap),
 * so the same bundle works on every host it is served from.
 */
export function getApiBaseUrl(): string {
  return resolveApiBaseUrl(process.env.NEXT_PUBLIC_API_BASE_URL, currentHostname());
}

export type FieldErrors = Record<string, string[]>;

export interface ApiErrorInit {
  status: number;
  code: string;
  title: string;
  detail?: string;
  errors?: FieldErrors;
  type?: string;
}

export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly title: string;
  readonly detail: string | undefined;
  readonly errors: FieldErrors | undefined;
  readonly type: string | undefined;

  constructor(init: ApiErrorInit) {
    super(init.detail || init.title);
    this.name = "ApiError";
    this.status = init.status;
    this.code = init.code;
    this.title = init.title;
    this.detail = init.detail;
    this.errors = init.errors;
    this.type = init.type;
  }

  get isNetworkError(): boolean {
    return this.status === 0;
  }

  get isUnauthorized(): boolean {
    return this.status === 401;
  }

  /** First message of each field, keyed by camelCase field name. */
  fieldMessages(): Record<string, string> {
    const result: Record<string, string> = {};
    if (!this.errors) return result;
    for (const [field, messages] of Object.entries(this.errors)) {
      if (messages.length > 0) result[field] = messages[0];
    }
    return result;
  }
}

export function isApiError(error: unknown): error is ApiError {
  return error instanceof ApiError;
}

const DEFAULT_TITLES: Record<number, string> = {
  0: "Não foi possível conectar ao servidor",
  400: "Requisição inválida",
  401: "Sessão expirada ou credenciais inválidas",
  403: "Você não tem permissão para esta ação",
  404: "Recurso não encontrado",
  409: "Conflito ao processar a requisição",
  413: "Arquivo grande demais para o seu plano",
  422: "Dados inválidos",
  423: "Conta temporariamente bloqueada",
  429: "Muitas requisições. Tente novamente em instantes",
  500: "Erro interno do servidor",
  502: "Servidor indisponível",
  503: "Servidor indisponível",
  504: "O servidor demorou demais para responder",
};

const DEFAULT_CODES: Record<number, string> = {
  0: "network_error",
  400: "bad_request",
  401: "unauthorized",
  403: "forbidden",
  404: "not_found",
  409: "conflict",
  413: "plan_limit",
  422: "validation_failed",
  423: "locked_out",
  429: "rate_limited",
  500: "server_error",
  501: "not_implemented",
  502: "unavailable",
  503: "unavailable",
  504: "timeout",
};

function defaultTitle(status: number): string {
  if (DEFAULT_TITLES[status]) return DEFAULT_TITLES[status];
  if (status >= 500) return "Erro no servidor";
  return "Erro na requisição";
}

function defaultCode(status: number): string {
  if (DEFAULT_CODES[status]) return DEFAULT_CODES[status];
  return status >= 500 ? "server_error" : "request_failed";
}

function lowerFirst(value: string): string {
  return value.length > 0 ? value[0].toLowerCase() + value.slice(1) : value;
}

/** ASP.NET returns `errors: { "Email": ["..."] }`; normalise keys to camelCase and values to arrays. */
export function normalizeFieldErrors(errors: ProblemDetails["errors"] | undefined): FieldErrors | undefined {
  if (!errors || typeof errors !== "object") return undefined;
  const result: FieldErrors = {};
  for (const [rawKey, value] of Object.entries(errors)) {
    const key = rawKey.includes(".") ? rawKey.split(".").map(lowerFirst).join(".") : lowerFirst(rawKey);
    const messages = Array.isArray(value)
      ? value.filter((m): m is string => typeof m === "string")
      : typeof value === "string"
        ? [value]
        : [];
    if (messages.length > 0) result[key] = messages;
  }
  return Object.keys(result).length > 0 ? result : undefined;
}

export function problemToApiError(status: number, problem: ProblemDetails | undefined): ApiError {
  return new ApiError({
    status,
    code: problem?.code || defaultCode(status),
    title: problem?.title || defaultTitle(status),
    detail: problem?.detail || undefined,
    errors: normalizeFieldErrors(problem?.errors),
    type: problem?.type,
  });
}

/**
 * Only JSON bodies (the API's problem documents) are read. HTML or plain-text bodies — a reverse
 * proxy's "502 Bad Gateway" page, for instance — are never shown to the user: the error then
 * carries the pt-BR default title for its status.
 */
async function readProblem(response: Response): Promise<ProblemDetails | undefined> {
  const contentType = response.headers.get("content-type") ?? "";
  try {
    const text = await response.text();
    if (!text) return undefined;
    if (contentType.includes("json") || text.trimStart().startsWith("{")) {
      const parsed: unknown = JSON.parse(text);
      if (parsed && typeof parsed === "object" && !Array.isArray(parsed)) return parsed as ProblemDetails;
    }
    return undefined;
  } catch {
    return undefined;
  }
}

export async function toApiError(response: Response): Promise<ApiError> {
  const problem = await readProblem(response);
  return problemToApiError(response.status, problem);
}

// ----------------------------------------------------------------------------- refresh handling

/** Endpoints that must never trigger an automatic token refresh. */
const CREDENTIAL_ENDPOINTS = [
  "/api/auth/login",
  "/api/auth/register",
  "/api/auth/refresh",
  "/api/auth/logout",
  "/api/auth/forgot-password",
  "/api/auth/reset-password",
  "/api/auth/verify-email",
];

export function isCredentialEndpoint(path: string): boolean {
  const clean = path.split("?")[0].replace(/\/+$/, "");
  return CREDENTIAL_ENDPOINTS.includes(clean);
}

let refreshInFlight: Promise<boolean> | null = null;

/** Calls POST /api/auth/refresh. Concurrent callers share one request. */
export function refreshSession(): Promise<boolean> {
  if (!refreshInFlight) {
    refreshInFlight = fetch(`${getApiBaseUrl()}/api/auth/refresh`, {
      method: "POST",
      credentials: "include",
      headers: { Accept: "application/json" },
    })
      .then((response) => response.ok)
      .catch(() => false)
      .finally(() => {
        refreshInFlight = null;
      });
  }
  return refreshInFlight;
}

type UnauthorizedListener = () => void;
const unauthorizedListeners = new Set<UnauthorizedListener>();

/** Notified when a request stays 401 after the refresh attempt (session is gone). */
export function onUnauthorized(listener: UnauthorizedListener): () => void {
  unauthorizedListeners.add(listener);
  return () => {
    unauthorizedListeners.delete(listener);
  };
}

function emitUnauthorized() {
  for (const listener of unauthorizedListeners) {
    try {
      listener();
    } catch {
      // listeners must not break the request pipeline
    }
  }
}

// ----------------------------------------------------------------------------- request

export type QueryValue = string | number | boolean | null | undefined;

export interface RequestOptions {
  method?: "GET" | "POST" | "PUT" | "PATCH" | "DELETE";
  /** Serialised as JSON unless it is FormData/Blob/string. */
  body?: unknown;
  headers?: Record<string, string>;
  query?: Record<string, QueryValue>;
  signal?: AbortSignal;
  /** Skip the automatic refresh-and-retry on 401. */
  skipAuthRefresh?: boolean;
}

export interface ApiResponse<T> {
  status: number;
  data: T;
  headers: Headers;
}

export function buildUrl(path: string, query?: Record<string, QueryValue>): string {
  const base = getApiBaseUrl();
  const url = path.startsWith("http") ? path : `${base}${path.startsWith("/") ? path : `/${path}`}`;
  if (!query) return url;
  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === undefined || value === null || value === "") continue;
    params.set(key, String(value));
  }
  const qs = params.toString();
  return qs ? `${url}${url.includes("?") ? "&" : "?"}${qs}` : url;
}

function buildInit(options: RequestOptions): RequestInit {
  const headers: Record<string, string> = {
    Accept: "application/json, application/problem+json",
    ...options.headers,
  };
  let body: BodyInit | undefined;
  if (options.body !== undefined && options.body !== null) {
    if (
      typeof options.body === "string" ||
      (typeof FormData !== "undefined" && options.body instanceof FormData) ||
      (typeof Blob !== "undefined" && options.body instanceof Blob)
    ) {
      body = options.body as BodyInit;
    } else {
      body = JSON.stringify(options.body);
      headers["Content-Type"] ??= "application/json";
    }
  }
  return {
    method: options.method ?? "GET",
    credentials: "include",
    headers,
    body,
    signal: options.signal,
  };
}

async function doFetch(url: string, init: RequestInit): Promise<Response> {
  try {
    return await fetch(url, init);
  } catch (error) {
    if (error instanceof DOMException && error.name === "AbortError") throw error;
    throw new ApiError({
      status: 0,
      code: "network_error",
      title: DEFAULT_TITLES[0],
      detail: "Verifique sua conexão ou tente novamente em instantes.",
    });
  }
}

async function parseBody<T>(response: Response): Promise<T> {
  if (response.status === 204 || response.status === 205) return undefined as T;
  const contentLength = response.headers.get("content-length");
  if (contentLength === "0") return undefined as T;
  const text = await response.text();
  if (!text) return undefined as T;
  const contentType = response.headers.get("content-type") ?? "";
  const trimmed = text.trimStart();
  if (contentType.includes("json") || trimmed.startsWith("{") || trimmed.startsWith("[")) {
    try {
      return JSON.parse(text) as T;
    } catch {
      return text as T;
    }
  }
  return text as T;
}

/** Performs a request and returns status + parsed body. Throws `ApiError` for non-2xx responses. */
export async function requestRaw<T>(path: string, options: RequestOptions = {}): Promise<ApiResponse<T>> {
  const url = buildUrl(path, options.query);
  const init = buildInit(options);

  let response = await doFetch(url, init);

  if (response.status === 401 && !options.skipAuthRefresh && !isCredentialEndpoint(path)) {
    const refreshed = await refreshSession();
    if (refreshed) {
      response = await doFetch(url, init);
    }
    if (response.status === 401) emitUnauthorized();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }

  const data = await parseBody<T>(response);
  return { status: response.status, data, headers: response.headers };
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await requestRaw<T>(path, options);
  return response.data;
}

type BodylessOptions = Omit<RequestOptions, "method" | "body">;

export const api = {
  get: <T>(path: string, options: BodylessOptions = {}) => request<T>(path, { ...options, method: "GET" }),
  post: <T>(path: string, body?: unknown, options: BodylessOptions = {}) =>
    request<T>(path, { ...options, method: "POST", body }),
  put: <T>(path: string, body?: unknown, options: BodylessOptions = {}) =>
    request<T>(path, { ...options, method: "PUT", body }),
  patch: <T>(path: string, body?: unknown, options: BodylessOptions = {}) =>
    request<T>(path, { ...options, method: "PATCH", body }),
  delete: <T>(path: string, body?: unknown, options: BodylessOptions = {}) =>
    request<T>(path, { ...options, method: "DELETE", body }),
};

export const ERROR_MESSAGES_BY_CODE: Record<string, string> = {
  invalid_credentials: "E-mail ou senha incorretos.",
  locked_out: "Conta temporariamente bloqueada por excesso de tentativas. Tente novamente mais tarde.",
  email_not_confirmed: "Confirme o seu e-mail antes de entrar. Verifique a sua caixa de entrada.",
  unauthorized: "Sua sessão expirou. Entre novamente.",
  forbidden: "Você não tem permissão para esta ação.",
  not_found: "Não encontramos o que você procura.",
  conflict: "Esta ação conflita com o estado atual. Atualize a página e tente novamente.",
  quota_exceeded: "Você atingiu o limite de processamento do seu plano neste período.",
  plan_limit: "Esta ação excede os limites do seu plano atual.",
  invalid_media: "O arquivo enviado não é um vídeo ou áudio válido.",
  source_not_supported: "Esta fonte não é suportada para processamento direto.",
  payments_not_configured: "Pagamentos ainda não configurados neste ambiente.",
  payment_provider_error: "O provedor de pagamento recusou a operação e nada foi alterado. Tente novamente em instantes.",
  rate_limited: "Muitas requisições em pouco tempo. Aguarde alguns instantes.",
  job_not_cancellable: "Este trabalho não pode mais ser cancelado.",
  validation_failed: "Alguns campos precisam de atenção.",
  network_error: "Não foi possível conectar ao servidor. Verifique sua conexão.",
  assistant_unavailable: "Assistente indisponível neste ambiente.",
};

/**
 * Human-readable pt-BR message for an error. API problem documents carry their own pt-BR
 * `detail`; upload errors are written in pt-BR by `src/lib/upload.ts`. Any other error (a
 * runtime `TypeError`, a library message in English) is replaced by the generic fallback.
 */
export function describeError(error: unknown, fallback = "Algo deu errado. Tente novamente."): string {
  if (isApiError(error)) {
    if (error.detail) return error.detail;
    const byCode = ERROR_MESSAGES_BY_CODE[error.code];
    if (byCode) return byCode;
    return error.title || fallback;
  }
  if (error instanceof UploadError && error.message) return error.message;
  return fallback;
}
