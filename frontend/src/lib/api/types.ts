/**
 * DTOs mirroring docs/API.md. Enums are lower-case strings as sent by the API.
 */

// ----------------------------------------------------------------------------- enums
export type JobKind = "analyze_source" | "render_clip" | "live_segment";
export type JobStatus =
  | "pending"
  | "queued"
  | "preparing_media"
  | "analyzing"
  | "detecting_moments"
  | "generating_clips"
  | "finalizing"
  | "completed"
  | "failed"
  | "cancelled";
export type JobEventLevel = "info" | "warning" | "error";
export type ProjectStatus = "created" | "processing" | "ready" | "failed" | "cancelled";
export type SourceKind = "upload" | "link" | "live";
export type SourcePlatform = "upload" | "twitch" | "youtube" | "kick" | "other" | "unknown";
export type ClipStatus = "pending" | "rendering" | "ready" | "failed";
export type RenderStatus = ClipStatus;
export type CandidateCategory = "highlight" | "reaction" | "funny" | "hype" | "tension" | "gameplay" | "quote";
export type CandidateStatus = "detected" | "selected" | "rendered" | "discarded";
export type AspectRatio = "9:16" | "16:9" | "1:1";
export type SubtitleStyle = "bold" | "clean" | "boxed";
export type SubtitlePosition = "bottom" | "center" | "top";
export type FramingMode = "auto" | "center" | "webcam-split";
export type AudioLanguage = "auto" | "pt" | "en" | "es";
export type MediaAssetKind = "source_upload" | "clip_render" | "thumbnail" | "analysis" | "live_segment";
export type MediaAssetStatus = "pending" | "uploaded" | "verified" | "failed" | "deleted";
export type SubscriptionStatus = "trialing" | "active" | "past_due" | "cancelled" | "expired";
export type PaymentEventStatus = "received" | "processed" | "ignored" | "failed";
export type NotificationType = "job_completed" | "job_failed" | "clip_ready" | "quota_warning" | "billing" | "system";
/** `waiting` → `live` → `ending` → `ended`; `error` from any non-terminal state (LIVE_CAPTURE.md §2). */
export type LiveSessionStatus = "waiting" | "live" | "ending" | "ended" | "error";
export type LiveSegmentStatus = "received" | "queued" | "analyzing" | "analyzed" | "failed";

// ----------------------------------------------------------------------------- common
export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface PageQuery {
  page?: number;
  pageSize?: number;
}

/** RFC 9457 problem details as returned by the API. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  errors?: Record<string, string[] | string>;
}

// ----------------------------------------------------------------------------- auth / account
export interface User {
  id: string;
  email: string;
  displayName: string;
  emailConfirmed: boolean;
  createdAt: string;
}

/**
 * `user_profiles.editing_preferences` (docs/API.md → Account). Defaults: `9:16`, subtitles on,
 * `bold`, `bottom`, 15 / 60 / 30 s, `auto`. The new-project form starts from these values.
 */
export interface EditingPreferences {
  aspectRatio: AspectRatio;
  subtitlesEnabled: boolean;
  subtitleStyle: SubtitleStyle;
  subtitlePosition: SubtitlePosition;
  /** Integer 5–300. */
  minClipSeconds: number;
  /** Integer, `minClipSeconds` ≤ value ≤ 300. */
  maxClipSeconds: number;
  /** Integer between `minClipSeconds` and `maxClipSeconds`. */
  targetClipSeconds: number;
  /** Language spoken in the recordings (`auto` = detect per recording); not the interface language. */
  language: AudioLanguage;
}

/**
 * `user_profiles.notification_preferences`: e-mail delivery only. In-app notifications are
 * always created and cannot be turned off. Defaults: false / true / true / true.
 */
export interface NotificationPreferences {
  emailOnJobCompleted: boolean;
  emailOnJobFailed: boolean;
  emailOnQuotaWarning: boolean;
  emailOnBilling: boolean;
}

export interface Profile {
  displayName: string;
  /** The interface is pt-BR only; the web always sends `pt-BR`. */
  locale: string;
  timezone?: string | null;
  editingPreferences: EditingPreferences;
  notificationPreferences: NotificationPreferences;
}

/**
 * `PUT /api/account/profile`. Every field is optional and nested preference objects are merged
 * field by field by the API (omitted fields keep the stored value); invalid values → 400.
 */
export interface ProfileUpdate {
  displayName?: string;
  locale?: string;
  timezone?: string | null;
  editingPreferences?: Partial<EditingPreferences>;
  notificationPreferences?: Partial<NotificationPreferences>;
}

export interface PlanLimits {
  monthlyProcessingMinutes: number;
  maxSourceDurationMinutes: number;
  maxFileSizeMb: number;
  /** 0 = unlimited */
  maxActiveProjects: number;
  maxCandidatesPerJob: number;
  maxAutoRenderPerJob: number;
  /** 0 = unlimited */
  maxRendersPerMonth: number;
  maxRenderHeight: number;
  maxConcurrentJobs: number;
  priority: number;
  retentionDays: number;
  storageQuotaGb: number;
  transcriptionModel: string;
  allowWebcamLayout: boolean;
  allowCustomSubtitleStyles: boolean;
  /** Assistant chat messages per month (0 = unlimited). Absent on APIs older than round 3. */
  assistantMessagesPerMonth?: number;
}

export interface SubscriptionSummary {
  planCode: string;
  planName: string;
  status: SubscriptionStatus;
  currentPeriodEnd?: string | null;
}

export interface MeResponse {
  user: User;
  profile: Profile;
  subscription: SubscriptionSummary;
  limits: PlanLimits;
}

export interface AuthResponse {
  user: User;
}

export interface RegisterRequest {
  email: string;
  password: string;
  displayName: string;
  acceptedTerms: true;
}

export interface RegisterResult {
  /** 201 = user created and signed in; 202 = confirmation e-mail required. */
  status: 201 | 202;
  user?: User;
  message?: string;
}

export interface UsageCounters {
  processingSeconds: number;
  renders: number;
  storageBytes: number;
  activeProjects: number;
  /** Assistant chat messages sent in the period (usage kind `assistant_message`). */
  assistantMessages?: number;
}

export interface UsageHistoryEntry {
  periodStart: string;
  periodEnd: string;
  processingSeconds: number;
  renders: number;
}

export interface UsageResponse {
  plan: { code: string; name: string; limits: PlanLimits };
  period: { start: string; end: string };
  used: UsageCounters;
  /** What is left in the period; a `null` entry means unlimited. */
  remaining: { [K in keyof UsageCounters]?: number | null };
  history: UsageHistoryEntry[];
}

// ----------------------------------------------------------------------------- media
export interface MediaAsset {
  id: string;
  kind: MediaAssetKind;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  status: MediaAssetStatus;
  durationSeconds?: number | null;
  width?: number | null;
  height?: number | null;
  fps?: number | null;
  container?: string | null;
  videoCodec?: string | null;
  audioCodec?: string | null;
  createdAt: string;
  uploadedAt?: string | null;
}

export interface CreateUploadRequest {
  fileName: string;
  sizeBytes: number;
  contentType: string;
  declaredDurationSeconds?: number;
}

export interface CreateUploadResponse {
  asset: { id: string; status: MediaAssetStatus };
  upload: {
    method: "PUT";
    url: string;
    headers?: Record<string, string> | null;
    expiresAt: string;
  };
  warnings: string[];
}

// ----------------------------------------------------------------------------- sources
export interface SourceAlternative {
  kind: "upload" | "obs_companion" | string;
  title: string;
  description: string;
}

export interface SourceValidation {
  platform: SourcePlatform;
  supported: boolean;
  reason?: string | null;
  alternatives?: SourceAlternative[];
  metadata?: {
    title?: string | null;
    durationSeconds?: number | null;
    thumbnailUrl?: string | null;
  } | null;
}

// ----------------------------------------------------------------------------- settings
export interface SubtitleSettings {
  enabled: boolean;
  style: SubtitleStyle;
  position: SubtitlePosition;
}

export interface FramingSettings {
  mode: FramingMode;
  focusX?: number;
  focusY?: number;
}

export interface AnalysisSettings {
  minClipSeconds: number;
  maxClipSeconds: number;
  targetClipSeconds: number;
  /** 0 = only very strong moments, 100 = many candidates */
  sensitivity: number;
  maxCandidates: number;
  /** How many of the best candidates are rendered automatically (default: all of them, capped by the plan). */
  autoRenderTop: number;
  aspectRatio: AspectRatio;
  subtitles: SubtitleSettings;
  language: AudioLanguage | string;
  framing: FramingSettings;
  title?: string | null;
  /** Categories the analysis favours (weights and ranking); `[]` = no focus. */
  focusCategories: CandidateCategory[];
  /** Extra words matched like the built-in lexicon: up to 20, 1–40 characters each. */
  customKeywords: string[];
  /** What the person asked in their own words (≤ 500 characters); used only when the worker has an LLM. */
  instructions: string | null;
}

// ----------------------------------------------------------------------------- jobs
export interface SignalAvailability {
  available: boolean;
  /** Transcript only: `false` when voice activity detection found no speech at all. */
  speechDetected?: boolean;
  [key: string]: unknown;
}

export interface SignalsAvailability {
  transcript: SignalAvailability;
  audio: SignalAvailability;
  visual: SignalAvailability;
  llm?: SignalAvailability;
}

export interface WebcamRegion {
  x: number;
  y: number;
  w: number;
  h: number;
  confidence?: number;
}

export interface JobSummary {
  id: string;
  /** Always sent by the API (docs/API.md → JobSummary); optional here so older payloads degrade to plain text. */
  projectId?: string;
  projectTitle?: string | null;
  kind: JobKind;
  status: JobStatus;
  progress: number;
  statusMessage?: string | null;
  updatedAt?: string;
}

export interface JobEvent {
  id?: string;
  createdAt: string;
  level: JobEventLevel;
  fromStatus?: JobStatus | null;
  toStatus?: JobStatus | null;
  message: string;
  data?: Record<string, unknown> | null;
}

export interface JobResult {
  candidatesFound?: number;
  clipsRendered?: number;
  processedSeconds?: number;
  signals?: Partial<SignalsAvailability>;
  webcamRegion?: WebcamRegion | null;
}

export interface Job {
  id: string;
  projectId: string;
  kind: JobKind;
  status: JobStatus;
  progress: number;
  statusMessage?: string | null;
  attempt: number;
  errorCode?: string | null;
  errorMessage?: string | null;
  queuedAt?: string | null;
  startedAt?: string | null;
  finishedAt?: string | null;
  result?: JobResult | null;
  events?: JobEvent[];
}

// ----------------------------------------------------------------------------- projects
export interface Project {
  id: string;
  title: string;
  sourceKind: SourceKind;
  sourcePlatform: SourcePlatform;
  sourceUrl?: string | null;
  status: ProjectStatus;
  settings?: AnalysisSettings | null;
  createdAt: string;
  updatedAt: string;
  durationSeconds?: number | null;
}

export interface ProjectSummary {
  id: string;
  title: string;
  sourceKind: SourceKind;
  sourcePlatform: SourcePlatform;
  status: ProjectStatus;
  createdAt: string;
  updatedAt: string;
  durationSeconds?: number | null;
  latestJob?: JobSummary | null;
  candidateCount: number;
  clipCount: number;
  thumbnailUrl?: string | null;
}

export interface ProjectSource {
  asset?: MediaAsset | null;
  previewable: boolean;
  previewUrl?: string | null;
}

export interface ProjectDetail {
  project: Project;
  jobs: JobSummary[];
  candidateCount: number;
  clipCount: number;
  source: ProjectSource;
}

export type CreateProjectSource = { kind: "upload"; mediaAssetId: string } | { kind: "link"; url: string };

export interface CreateProjectRequest {
  title?: string;
  source: CreateProjectSource;
  settings?: Partial<AnalysisSettings>;
}

export interface CreateProjectResponse {
  project: Project;
  job: Job;
}

export interface ProjectsQuery extends PageQuery {
  status?: ProjectStatus | "";
}

// ----------------------------------------------------------------------------- candidates
export interface CandidateSignals {
  audio?: Record<string, unknown> & { available?: boolean };
  transcript?: Record<string, unknown> & { available?: boolean };
  visual?: Record<string, unknown> & { available?: boolean };
  llm?: Record<string, unknown> & { available?: boolean };
  composite?: number;
  threshold?: number;
  weights?: Record<string, number>;
  contributors?: string[];
}

export interface Candidate {
  id: string;
  rank: number;
  startSeconds: number;
  endSeconds: number;
  durationSeconds: number;
  category: CandidateCategory;
  /** Internal relevance estimate 0-100 (never a virality probability). */
  score: number;
  reasoning?: string | null;
  transcript?: string | null;
  signals?: CandidateSignals | null;
  status: CandidateStatus;
  clipId?: string | null;
  previewUrl?: string | null;
  previewable: boolean;
}

export interface CandidatesResponse {
  items: Candidate[];
  signals: SignalsAvailability;
}

export interface CandidatesQuery {
  sort?: "score" | "time";
  category?: CandidateCategory | "";
}

export interface CreateClipRequest {
  title?: string;
  aspectRatio?: AspectRatio;
  subtitles?: SubtitleSettings;
  framing?: FramingSettings;
  startSeconds?: number;
  endSeconds?: number;
}

// ----------------------------------------------------------------------------- clips
export interface ClipRenderSummary {
  id: string;
  version: number;
  width?: number | null;
  height?: number | null;
  sizeBytes?: number | null;
  status: RenderStatus;
}

export interface Render extends ClipRenderSummary {
  aspectRatio?: AspectRatio;
  durationSeconds?: number | null;
  hasSubtitles?: boolean;
  error?: string | null;
  createdAt?: string;
  completedAt?: string | null;
  streamUrl?: string | null;
  downloadUrl?: string | null;
}

export interface ClipSummary {
  id: string;
  projectId: string;
  projectTitle?: string | null;
  title: string;
  category?: CandidateCategory | null;
  score?: number | null;
  status: ClipStatus;
  aspectRatio: AspectRatio;
  durationSeconds: number;
  startSeconds: number;
  endSeconds: number;
  thumbnailUrl?: string | null;
  createdAt: string;
  updatedAt: string;
  currentRender?: Render | null;
}

export interface ClipDetail extends ClipSummary {
  subtitles: SubtitleSettings;
  framing: FramingSettings;
  reasoning?: string | null;
  transcript?: string | null;
  currentRender?: Render | null;
  renders: Render[];
  source: { previewable: boolean; previewUrl?: string | null; durationSeconds: number };
  activeJob?: JobSummary | null;
}

export interface ClipsQuery extends PageQuery {
  projectId?: string;
  category?: CandidateCategory | "";
  status?: ClipStatus | "";
  q?: string;
  sort?: "createdAt" | "score" | "duration";
  order?: "asc" | "desc";
}

export interface UpdateClipRequest {
  title?: string;
  startSeconds?: number;
  endSeconds?: number;
  aspectRatio?: AspectRatio;
  subtitles?: SubtitleSettings;
  framing?: FramingSettings;
}

export interface UpdateClipResponse {
  clip: ClipDetail;
  needsRender: boolean;
}

export interface RenderClipResponse {
  clip: ClipDetail;
  job: Job;
  render: Render;
}

export interface DownloadResponse {
  url: string;
  expiresAt: string;
  fileName: string;
}

// ----------------------------------------------------------------------------- dashboard
export interface DashboardUsage {
  processingSecondsUsed: number;
  processingSecondsLimit: number;
  rendersUsed: number;
  rendersLimit: number;
  storageBytesUsed: number;
  storageBytesLimit: number;
}

export interface DashboardWarning {
  code: string;
  message: string;
}

export interface DashboardSummary {
  clipsTotal: number;
  clipsThisPeriod: number;
  activeJobs: JobSummary[];
  minutesProcessedThisPeriod: number;
  recentClips: ClipSummary[];
  usage: DashboardUsage;
  warnings: DashboardWarning[];
}

// ----------------------------------------------------------------------------- notifications
export interface Notification {
  id: string;
  type: NotificationType;
  title: string;
  body?: string | null;
  data?: Record<string, unknown> | null;
  readAt?: string | null;
  createdAt: string;
}

export interface UnreadCountResponse {
  count?: number;
  unreadCount?: number;
  unread?: number;
}

// ----------------------------------------------------------------------------- plans & billing
export interface Plan {
  code: string;
  name: string;
  description?: string | null;
  priceMonthlyCents?: number | null;
  currency?: string | null;
  limits: PlanLimits;
  features?: string[];
  isCurrent?: boolean;
  /** `false` for free plans (price 0) and plans without a provider price: never sent to checkout. */
  purchasable?: boolean;
}

export interface PlansResponse {
  items: Plan[];
}

export interface Subscription {
  id?: string;
  planCode?: string;
  planName?: string;
  status: SubscriptionStatus;
  provider?: string;
  currentPeriodStart?: string | null;
  currentPeriodEnd?: string | null;
  cancelAtPeriodEnd?: boolean;
  cancelledAt?: string | null;
}

export interface SubscriptionResponse {
  subscription: Subscription;
  plan: Plan;
  paymentsConfigured: boolean;
  portalAvailable: boolean;
}

export interface CheckoutRequest {
  planCode: string;
  successUrl?: string;
  cancelUrl?: string;
}

export interface CheckoutResponse {
  /** Provider checkout page, or the success page when the plan was changed in place. */
  checkoutUrl: string;
  /**
   * `true` when an active paid subscription was switched to the new plan in place (prorated):
   * there is no checkout page to visit, the entitlement changes when the provider's webhook arrives.
   */
  planChanged?: boolean;
}

export interface PortalResponse {
  url: string;
}

export interface PaymentEvent {
  id: string;
  type: string;
  amountCents?: number | null;
  currency?: string | null;
  status: PaymentEventStatus | string;
  createdAt: string;
}

// ----------------------------------------------------------------------------- live (phase 5)
/** A computer running the OBS companion (`/api/live/companion/devices`). */
export interface CompanionDevice {
  id: string;
  name: string;
  /** First 8 characters after `cfd_`, the only part of the token kept in clear. */
  tokenPrefix: string;
  createdAt: string;
  lastSeenAt?: string | null;
  revokedAt?: string | null;
  /** Last request within 60 s and not revoked (computed by the API). */
  online: boolean;
}

/** `POST /api/live/companion/tokens` → `201`. The token is shown once and never returned again. */
export interface CompanionTokenResponse {
  device: CompanionDevice;
  token: string;
}

/** What the companion reported in its last heartbeat. */
export interface LiveCompanionState {
  obsConnected: boolean;
  streaming: boolean;
  recording: boolean;
  uploading: boolean;
  queueLength: number;
  lastError?: string | null;
}

export interface LiveSessionStats {
  segmentsReceived: number;
  segmentsAnalyzed: number;
  segmentsFailed: number;
  highlights: number;
  recordedSeconds: number;
}

export interface LiveSession {
  id: string;
  /** Every session owns one project (`sourceKind: "live"`). */
  projectId: string;
  title: string;
  status: LiveSessionStatus;
  deviceId?: string | null;
  deviceName?: string | null;
  segmentSeconds: number;
  startedAt?: string | null;
  endedAt?: string | null;
  lastSegmentAt?: string | null;
  lastHeartbeatAt?: string | null;
  /** Heartbeat within the last 30 s (computed by the API). */
  online: boolean;
  /** "Encerrar sessão" was requested; the companion ends the session on its next heartbeat. */
  stopRequested: boolean;
  endReason?: string | null;
  /** Last heartbeat state; meaningful only once `lastHeartbeatAt` is set. */
  companionState?: LiveCompanionState | null;
  stats: LiveSessionStats;
}

export interface LiveSegment {
  id: string;
  sequence: number;
  /** Position of the segment in the recording (seconds from the start of the session). */
  startOffsetSeconds: number;
  durationSeconds: number;
  status: LiveSegmentStatus;
  jobId?: string | null;
  error?: string | null;
  createdAt: string;
}

/** `GET /api/live/sessions/{id}`. */
export interface LiveSessionDetail {
  session: LiveSession;
  settings?: AnalysisSettings | null;
  /** Latest 50, newest first. */
  segments: LiveSegment[];
  /** Clips of the session's project, newest first, at most 50. */
  highlights: ClipSummary[];
  candidateCount: number;
}

export interface LiveSessionsQuery extends PageQuery {
  status?: LiveSessionStatus | "";
}

/** `POST /api/live/sessions/{id}/stop` → `202`. */
export interface StopLiveSessionResponse {
  session: LiveSession;
}

// ----------------------------------------------------------------------------- assistant (round 3)
export type AssistantProvider = "local" | "gemini" | "none";

/** `GET /api/assistant/status` (never carries keys). */
export interface AssistantStatus {
  available: boolean;
  provider: AssistantProvider | string;
  model?: string | null;
  reason?: string | null;
}

export interface AssistantMessage {
  role: "user" | "assistant";
  content: string;
}

export interface AssistantChatContext {
  fileName?: string;
  durationSeconds?: number;
}

/** `POST /api/assistant/chat`: 1–20 messages, each ≤ 2000 characters, the last one from the user. */
export interface AssistantChatRequest {
  messages: AssistantMessage[];
  currentSettings: Partial<AnalysisSettings>;
  context?: AssistantChatContext;
}

/** One validated change of the reply: `field` is the settings field name, `label` its pt-BR description. */
export interface AssistantChange {
  field: string;
  label: string;
  value: unknown;
}

export interface AssistantChatResponse {
  /** Composed by the server from the validated changes; the web never writes replies itself. */
  reply: string;
  settingsPatch?: Partial<AnalysisSettings> | null;
  changes?: AssistantChange[] | null;
  provider?: string;
  model?: string | null;
}
