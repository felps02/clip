import type {
  AspectRatio,
  AudioLanguage,
  CandidateCategory,
  CandidateStatus,
  ClipStatus,
  FramingMode,
  JobEventLevel,
  JobKind,
  JobStatus,
  LiveSegmentStatus,
  LiveSessionStatus,
  NotificationType,
  PaymentEventStatus,
  ProjectStatus,
  SourceKind,
  SourcePlatform,
  SubscriptionStatus,
  SubtitlePosition,
  SubtitleStyle,
} from "@/lib/api/types";

export type Tone = "neutral" | "accent" | "success" | "warning" | "danger" | "info";

export const JOB_STATUSES: readonly JobStatus[] = [
  "pending",
  "queued",
  "preparing_media",
  "analyzing",
  "detecting_moments",
  "generating_clips",
  "finalizing",
  "completed",
  "failed",
  "cancelled",
] as const;

export const JOB_STATUS_LABELS: Record<JobStatus, string> = {
  pending: "Pendente",
  queued: "Na fila",
  preparing_media: "Preparando mídia",
  analyzing: "Analisando",
  detecting_moments: "Detectando momentos",
  generating_clips: "Gerando clipes",
  finalizing: "Finalizando",
  completed: "Concluído",
  failed: "Falhou",
  cancelled: "Cancelado",
};

export const JOB_STATUS_TONES: Record<JobStatus, Tone> = {
  pending: "neutral",
  queued: "info",
  preparing_media: "accent",
  analyzing: "accent",
  detecting_moments: "accent",
  generating_clips: "accent",
  finalizing: "accent",
  completed: "success",
  failed: "danger",
  cancelled: "warning",
};

const TERMINAL_JOB_STATUSES = new Set<JobStatus>(["completed", "failed", "cancelled"]);

export function isTerminalJobStatus(status: JobStatus | null | undefined): boolean {
  return status ? TERMINAL_JOB_STATUSES.has(status) : true;
}

export function isActiveJobStatus(status: JobStatus | null | undefined): boolean {
  return !!status && !TERMINAL_JOB_STATUSES.has(status);
}

/** Queued or running jobs can be cancelled. */
export function isCancellableJobStatus(status: JobStatus | null | undefined): boolean {
  return isActiveJobStatus(status);
}

export function jobStatusLabel(status: string | null | undefined): string {
  if (!status) return "Desconhecido";
  return JOB_STATUS_LABELS[status as JobStatus] ?? status;
}

/** Spoken prefix for job event icons (the icon itself is decorative). */
export const JOB_EVENT_LEVEL_LABELS: Record<JobEventLevel, string> = {
  info: "Informação",
  warning: "Aviso",
  error: "Erro",
};

export const JOB_KIND_LABELS: Record<JobKind, string> = {
  analyze_source: "Análise da gravação",
  render_clip: "Renderização de clipe",
  live_segment: "Segmento ao vivo",
};

export const PROJECT_STATUS_LABELS: Record<ProjectStatus, string> = {
  created: "Criado",
  processing: "Processando",
  ready: "Pronto",
  failed: "Falhou",
  cancelled: "Cancelado",
};

export const PROJECT_STATUS_TONES: Record<ProjectStatus, Tone> = {
  created: "neutral",
  processing: "accent",
  ready: "success",
  failed: "danger",
  cancelled: "warning",
};

export const CLIP_STATUS_LABELS: Record<ClipStatus, string> = {
  pending: "Aguardando renderização",
  rendering: "Renderizando",
  ready: "Pronto",
  failed: "Falhou",
};

export const CLIP_STATUS_TONES: Record<ClipStatus, Tone> = {
  pending: "neutral",
  rendering: "accent",
  ready: "success",
  failed: "danger",
};

export const CANDIDATE_CATEGORY_LABELS: Record<CandidateCategory, string> = {
  highlight: "destaque",
  reaction: "reação",
  funny: "engraçado",
  hype: "hype",
  tension: "tensão",
  gameplay: "gameplay",
  quote: "frase",
};

export const CANDIDATE_CATEGORIES = Object.keys(CANDIDATE_CATEGORY_LABELS) as CandidateCategory[];

/** Labels of `settings.focusCategories` (what the analysis should favour). */
export const FOCUS_CATEGORY_LABELS: Record<CandidateCategory, string> = {
  highlight: "Destaques",
  reaction: "Reações",
  funny: "Engraçados",
  hype: "Hype",
  tension: "Tensão",
  gameplay: "Gameplay",
  quote: "Frases",
};

export function categoryLabel(category: string | null | undefined): string {
  if (!category) return "momento";
  return CANDIDATE_CATEGORY_LABELS[category as CandidateCategory] ?? category;
}

export const CANDIDATE_STATUS_LABELS: Record<CandidateStatus, string> = {
  detected: "Detectado",
  selected: "Selecionado",
  rendered: "Clipe gerado",
  discarded: "Descartado",
};

export const ASPECT_RATIO_LABELS: Record<AspectRatio, string> = {
  "9:16": "Vertical (9:16)",
  "16:9": "Horizontal (16:9)",
  "1:1": "Quadrado (1:1)",
};

export const ASPECT_RATIOS: AspectRatio[] = ["9:16", "16:9", "1:1"];

/** pt-BR names shown in the UI; the enum values (`bold`, `clean`, `boxed`) stay internal. */
export const SUBTITLE_STYLE_LABELS: Record<SubtitleStyle, string> = {
  bold: "Destaque",
  clean: "Limpo",
  boxed: "Caixa",
};

export const SUBTITLE_STYLE_DESCRIPTIONS: Record<SubtitleStyle, string> = {
  bold: "Texto grande, branco, com contorno preto",
  clean: "Texto médio, contorno leve",
  boxed: "Texto sobre uma caixa semitransparente",
};

export const SUBTITLE_POSITION_LABELS: Record<SubtitlePosition, string> = {
  bottom: "Embaixo",
  center: "Centro",
  top: "Em cima",
};

export const FRAMING_LABELS: Record<FramingMode, string> = {
  auto: "Automático",
  center: "Centralizado",
  "webcam-split": "Webcam + gameplay",
};

export const FRAMING_DESCRIPTIONS: Record<FramingMode, string> = {
  auto: "Usa webcam + gameplay quando a webcam é detectada e o plano permite; caso contrário, centraliza.",
  center: "Recorte centralizado na área principal do vídeo.",
  "webcam-split": "Webcam em cima e gameplay embaixo (formato vertical).",
};

/** Language spoken in the recording (transcription), never the interface language. */
export const LANGUAGE_LABELS: Record<AudioLanguage, string> = {
  auto: "Automático (detectar)",
  pt: "Português",
  en: "Inglês",
  es: "Espanhol",
};

export const SOURCE_KIND_LABELS: Record<SourceKind, string> = {
  upload: "Upload",
  link: "Link",
  live: "Ao vivo",
};

export const PLATFORM_LABELS: Record<SourcePlatform, string> = {
  upload: "Arquivo enviado",
  twitch: "Twitch",
  youtube: "YouTube",
  kick: "Kick",
  other: "Outra plataforma",
  unknown: "Plataforma não identificada",
};

export function platformLabel(platform: string | null | undefined): string {
  if (!platform) return PLATFORM_LABELS.unknown;
  return PLATFORM_LABELS[platform as SourcePlatform] ?? platform;
}

export const SUBSCRIPTION_STATUS_LABELS: Record<SubscriptionStatus, string> = {
  trialing: "Período de teste",
  active: "Ativa",
  past_due: "Pagamento pendente",
  cancelled: "Cancelada",
  expired: "Expirada",
};

export const SUBSCRIPTION_STATUS_TONES: Record<SubscriptionStatus, Tone> = {
  trialing: "info",
  active: "success",
  past_due: "warning",
  cancelled: "warning",
  expired: "danger",
};

export const PAYMENT_STATUS_LABELS: Record<PaymentEventStatus, string> = {
  received: "Recebido",
  processed: "Processado",
  ignored: "Ignorado",
  failed: "Falhou",
};

export const NOTIFICATION_TYPE_LABELS: Record<NotificationType, string> = {
  job_completed: "Processamento concluído",
  job_failed: "Processamento falhou",
  clip_ready: "Clipe pronto",
  quota_warning: "Aviso de cota",
  billing: "Assinatura",
  system: "Sistema",
};

/** Label of the `clip_ready` notification created for each live highlight (LIVE_CAPTURE.md §4). */
export const LIVE_HIGHLIGHT_NOTIFICATION_LABEL = "Novo destaque na live";

/** Every session status of the contract (LIVE_CAPTURE.md §2), in lifecycle order. */
export const LIVE_SESSION_STATUSES: readonly LiveSessionStatus[] = ["waiting", "live", "ending", "ended", "error"] as const;

export const LIVE_SESSION_STATUS_LABELS: Record<LiveSessionStatus, string> = {
  waiting: "Aguardando",
  live: "Ao vivo",
  ending: "Encerrando",
  ended: "Encerrada",
  error: "Erro",
};

export const LIVE_SESSION_STATUS_TONES: Record<LiveSessionStatus, Tone> = {
  waiting: "info",
  live: "success",
  ending: "accent",
  ended: "neutral",
  error: "danger",
};

const ACTIVE_LIVE_SESSION_STATUSES = new Set<string>(["waiting", "live", "ending"]);

/** `waiting`, `live` and `ending` sessions still change (segments arrive or are being analysed). */
export function isActiveLiveSessionStatus(status: string | null | undefined): boolean {
  return !!status && ACTIVE_LIVE_SESSION_STATUSES.has(status);
}

export function liveSessionStatusLabel(status: string | null | undefined): string {
  if (!status) return "Desconhecido";
  return LIVE_SESSION_STATUS_LABELS[status as LiveSessionStatus] ?? status;
}

export const LIVE_SEGMENT_STATUSES: readonly LiveSegmentStatus[] = ["received", "queued", "analyzing", "analyzed", "failed"] as const;

export const LIVE_SEGMENT_STATUS_LABELS: Record<LiveSegmentStatus, string> = {
  received: "Recebido",
  queued: "Na fila",
  analyzing: "Analisando",
  analyzed: "Analisado",
  failed: "Falhou",
};

export const LIVE_SEGMENT_STATUS_TONES: Record<LiveSegmentStatus, Tone> = {
  received: "neutral",
  queued: "info",
  analyzing: "accent",
  analyzed: "success",
  failed: "danger",
};

export function liveSegmentStatusLabel(status: string | null | undefined): string {
  if (!status) return "Desconhecido";
  return LIVE_SEGMENT_STATUS_LABELS[status as LiveSegmentStatus] ?? status;
}

/** Signal contributor ids (audio.energy, transcript.keyword, ...) to human labels. */
const CONTRIBUTOR_LABELS: Record<string, string> = {
  "audio.energy": "energia de áudio",
  "audio.burst": "pico de áudio",
  "audio.scream": "grito",
  "audio.scream_like": "grito",
  "audio.laughter": "risada",
  "audio.laughter_like": "risada",
  "audio.sustained": "áudio sustentado",
  "transcript.keyword": "palavra-chave",
  "transcript.keywords": "palavras-chave",
  "transcript.speech_rate": "fala rápida",
  "transcript.exclamation": "exclamação",
  "transcript.exclamations": "exclamações",
  "transcript.pause_before": "pausa seguida de reação",
  "visual.motion": "movimento na tela",
  "visual.scene_cut": "corte de cena",
  "visual.scene_cuts": "cortes de cena",
  "visual.flash": "flash",
  "llm.score": "avaliação por IA",
  llm: "avaliação por IA",
};

export function contributorLabel(id: string): string {
  if (CONTRIBUTOR_LABELS[id]) return CONTRIBUTOR_LABELS[id];
  const [group, name] = id.split(".");
  const groupLabel =
    group === "audio" ? "áudio" : group === "transcript" ? "transcrição" : group === "visual" ? "visual" : group;
  return name ? `${groupLabel}: ${name.replaceAll("_", " ")}` : groupLabel;
}

export const SIGNAL_LABELS = {
  audio: "Áudio",
  transcript: "Transcrição",
  visual: "Visual",
} as const;
