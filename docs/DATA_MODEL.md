# Data model

PostgreSQL via EF Core 10 (Npgsql). Conventions: table and column names in `snake_case`
(use `EFCore.NamingConventions` or explicit configuration), primary keys are `uuid`
(`Guid.CreateVersion7()`), timestamps are `timestamptz` in UTC, enums stored as `text`,
JSON documents as `jsonb`. Every user-owned table carries `user_id` for cheap ownership
checks. Soft delete (`deleted_at`) on projects, clips and media assets; a cleanup service
purges storage objects and hard-deletes rows after the retention window.

"Project" in this model is the "MediaSource" of the product brief: one analysed source with
its settings, jobs, candidates and clips.

## Entities

### users (ASP.NET Identity, `AppUser : IdentityUser<Guid>`)
Identity columns (email, normalized email, password hash, lockout, email_confirmed, …) plus:
`display_name`, `created_at`, `deleted_at`, `deletion_requested_at`.

### user_profiles (1:1 users)
`user_id` PK/FK · `locale` (default `pt-BR`; the interface exists only in pt-BR) · `timezone` ·
`editing_preferences` jsonb `{ aspectRatio, subtitlesEnabled, subtitleStyle, subtitlePosition,
minClipSeconds, maxClipSeconds, targetClipSeconds, language }` (defaults `9:16`, `true`, `bold`,
`bottom`, 15, 60, 30, `auto`; 5 ≤ min ≤ target ≤ max ≤ 300; `language` is the language spoken in
the recordings: `auto|pt|en|es`) · `notification_preferences` jsonb `{ emailOnJobCompleted,
emailOnJobFailed, emailOnQuotaWarning, emailOnBilling }` (defaults `false`, `true`, `true`,
`true`; they control e-mail delivery only, which goes to a confirmed address only; in-app
notifications are always created) · `accepted_terms_at` ·
`marketing_consent` bool · `updated_at`. `PUT /api/account/profile` merges each preference
object field by field (docs/API.md → Account). Documents written before a field existed are read
with that field at its default (`EditingPreferences.Normalized` also widens min/max around an
older stored target); notification documents of the previous shape (`emailBilling`, `inApp`),
which never held a user's choice, were rewritten to the defaults by the
`20261023124846_NotificationPreferencesContract` migration.

### refresh_tokens
`id` · `user_id` FK · `token_hash` (SHA-256, unique) · `expires_at` · `created_at` ·
`created_by_ip` · `revoked_at` · `replaced_by_token_hash`. Index `(user_id)`, `(token_hash)`.
Rotation revokes the presented token with a conditional `UPDATE … WHERE id = @id AND revoked_at IS
NULL` in the transaction that inserts its successor, so a token is rotated at most once; a
request that loses that race (or presents an already rotated token) revokes all of the user's
tokens.

### plans
`id` · `code` (unique: `free`, `creator`, `pro`) · `name` · `description` · `is_active` ·
`sort_order` · `price_monthly_cents` (nullable) · `currency` · `provider_price_id`
(nullable, e.g. Stripe price) · `limits` jsonb (see `PlanLimits` below) · `features` jsonb
(string list) · `updated_at`.

`PlanLimits` (all configurable):
`monthlyProcessingMinutes`, `maxSourceDurationMinutes`, `maxFileSizeMb`,
`maxActiveProjects` (0 = unlimited), `maxCandidatesPerJob`, `maxAutoRenderPerJob`,
`maxRendersPerMonth` (0 = unlimited), `maxRenderHeight` (720/1080), `maxConcurrentJobs`,
`priority` (int, higher first), `retentionDays`, `storageQuotaGb`, `transcriptionModel`
(`tiny`/`base`/`small`), `allowWebcamLayout` bool, `allowCustomSubtitleStyles` bool,
`assistantMessagesPerMonth` (0 = unlimited). A document stored before a limit existed reads it at the
class default (the free plan's value, e.g. `assistantMessagesPerMonth` 100); `PlanSeeder` rewrites
every configured plan on startup anyway.

### subscriptions
`id` · `user_id` FK (index; at most one `active|trialing|past_due` per user) · `plan_id` FK ·
`status` (`trialing|active|past_due|cancelled|expired`) · `provider` (`none|stripe|mercadopago`) ·
`provider_customer_id` · `provider_subscription_id` (unique when not null) ·
`current_period_start` · `current_period_end` · `cancel_at_period_end` bool · `cancelled_at` ·
`last_provider_event_at` (nullable: provider time — Stripe `Event.Created` — of the newest
`customer.subscription.*` event applied to the row; an older event never overwrites it, and it
restarts when the row is bound to another provider subscription) · `created_at` · `updated_at`.
Every user gets a `free` subscription on registration (`provider=none`, period = calendar month
rolling from creation). A `cancelled` provider row is final (docs/PLANS_AND_BILLING.md).

### subscription_events (history)
`id` · `subscription_id` FK · `user_id` · `type` (`created|plan_changed|renewed|payment_failed|cancelled|reactivated|expired`) ·
`from_plan_code` · `to_plan_code` · `source` (`system|user|webhook|admin`) · `data` jsonb · `created_at`.

### usage_records (auditable consumption)
`id` · `user_id` FK · `subscription_id` FK (nullable) · `kind` (`processing_seconds|render|storage_bytes|assistant_message`) ·
`quantity` numeric · `period_start` · `period_end` · `job_id` (nullable) · `project_id` (nullable) ·
`clip_id` (nullable) · `metadata` jsonb · `created_at`.
Index `(user_id, kind, period_start)`. Quota check = Σ quantity in current period +
Σ `reserved_seconds` of the user's active jobs + requested amount ≤ limit.
`assistant_message` (quantity 1, `metadata { provider }`): one answered message of the settings
assistant, checked against `assistantMessagesPerMonth`. It is inserted under the per-user lock before
the model is called and deleted again when the call produces no answer (provider unavailable, aborted
request), so only answered messages count.

### payment_events (webhook audit, idempotent)
`id` · `provider` · `provider_event_id` (unique with provider) · `type` · `kind` (nullable:
provider-neutral kind — `checkout_completed`, `subscription_created`, `subscription_updated`,
`subscription_deleted`, `invoice_paid`, `invoice_payment_failed`, `unknown`) ·
`provider_subscription_id` (nullable; index `(provider, provider_subscription_id)` — a stored
`subscription_deleted` keeps that subscription from ever being bound) · `occurred_at` (nullable:
when the provider created the event) · `payload` jsonb ·
`received_at` · `processed_at` · `status` (`received|processed|ignored|failed`) · `error` ·
`user_id` (nullable) · `subscription_id` (nullable). `processed` and `ignored` are final (a
redelivery is acknowledged without being applied again); `received` (stored, not yet processed)
and `failed` are processed again on the next delivery. Deliveries of one event serialise on
the row (`SELECT … FOR UPDATE`).

### media_assets (file references; bytes live in object storage)
`id` · `user_id` FK · `kind` (`source_upload|clip_render|thumbnail|analysis|live_segment`) ·
`storage_key` (unique) · `original_file_name` · `content_type` · `size_bytes` ·
`status` (`pending|uploaded|verified|failed|deleted`) · `sha256` (nullable) ·
`duration_seconds` · `width` · `height` · `fps` · `container` · `video_codec` · `audio_codec` ·
`has_audio` · `created_at` · `uploaded_at` · `expires_at` (pending uploads expire; on a
`deleted` asset: when its storage key is deleted once more, after the signed upload URL expired)
· `deleted_at`. Index `(user_id, status)`, `(expires_at)`.

### projects (= MediaSource)
`id` · `user_id` FK · `title` · `source_kind` (`upload|link|live`) · `source_url` (nullable) ·
`source_platform` (`upload|twitch|youtube|kick|other|obs`; `obs` for live projects, whose `media_asset_id` stays null — their media are the session's `live_segments`) · `media_asset_id` FK (nullable) ·
`status` (`created|processing|ready|failed|cancelled`) · `settings` jsonb (`AnalysisSettings`) ·
`client_request_id` (nullable; unique `(user_id, client_request_id)`) · `created_at` ·
`updated_at` · `deleted_at`. Index `(user_id, created_at desc)`.

`AnalysisSettings`: `minClipSeconds` 5–300 (default 15) · `maxClipSeconds` min–300 (60) ·
`targetClipSeconds` between min and max (30) · `sensitivity` 0–100 (50) · `maxCandidates` (20, capped by plan) ·
`autoRenderTop` (default = `maxCandidates`, i.e. every candidate becomes a clip; capped by the plan's
`maxAutoRenderPerJob` and by `maxCandidates`; always stored as a number) · `aspectRatio`
(`9:16|16:9|1:1`) · `subtitles` {`enabled`, `style`: `bold|clean|boxed`, `position`:
`bottom|center|top`} · `language` (`auto|pt|en|es|…`) · `framing` {`mode`: `auto|center|webcam-split`,
`focusX`, `focusY` (0–1, stored explicitly, default 0.5), `webcamRegion?`} · `title?` ·
`focusCategories` (string[], subset of `highlight|reaction|funny|hype|tension|gameplay|quote`,
default `[]`) · `customKeywords` (string[], ≤ 20 entries of 1–40 characters, trimmed, deduplicated
case-insensitively, default `[]`) · `instructions` (string ≤ 500 characters or `null`, default
`null`). The same document is the `input` of the project's `analyze_source` jobs and the `settings` of
the worker's `JobPayload`. Documents written before a field existed read it at its default; the new
fields live inside the `settings` jsonb, so they needed no migration.

### processing_jobs
`id` · `user_id` FK · `project_id` FK · `kind` (`analyze_source|render_clip|live_segment`) ·
`status` (`pending|queued|preparing_media|analyzing|detecting_moments|generating_clips|finalizing|completed|failed|cancelled`) ·
`progress` smallint 0–100 · `status_message` · `priority` int · `attempt` int ·
`max_attempts` int · `worker_id` · `lease_expires_at` · `cancel_requested` bool ·
`input` jsonb (kind-specific payload, e.g. clip id + render id for `render_clip`) ·
`result` jsonb (summary: candidates found, clips rendered, processed seconds, webcam region, signal availability) ·
`error_code` · `error_message` · `reserved_seconds` int (set at probe, cleared at finish) ·
`transcript_storage_key` · `signals_storage_key` · `queued_at` · `started_at` · `finished_at` ·
`created_at` · `updated_at`.
Index `(status, priority desc, queued_at)` for claiming; `(user_id, created_at desc)`;
`(project_id)`; partial index on active statuses for concurrency checks.

### processing_job_events
`id` · `job_id` FK · `created_at` · `level` (`info|warning|error`) · `from_status` ·
`to_status` · `message` · `data` jsonb. Index `(job_id, created_at)`.

### clip_candidates
`id` · `job_id` FK · `project_id` FK · `user_id` FK · `rank` int · `start_seconds` numeric ·
`end_seconds` numeric · `duration_seconds` numeric (stored) · `category`
(`highlight|reaction|funny|hype|tension|gameplay|quote`) · `score` smallint 0–100 ·
`reasoning` text · `transcript` text · `signals` jsonb · `status`
(`detected|selected|rendered|discarded`) · `created_at`.
Index `(project_id, score desc)`, `(job_id)`.

### clips
`id` · `user_id` FK · `project_id` FK · `candidate_id` FK (nullable) · `title` ·
`start_seconds` · `end_seconds` · `aspect_ratio` · `subtitles` jsonb · `framing` jsonb ·
`category` · `score` (copied from candidate) · `status` (`pending|rendering|ready|failed`) ·
`current_render_id` (nullable FK → `clip_renders`, `ON DELETE SET NULL`) · `thumbnail_storage_key` ·
`created_at` · `updated_at` · `deleted_at`. Index `(user_id, created_at desc)`, `(project_id)`,
`(current_render_id)`. `clips` and `clip_renders` reference each other (`clip_renders.clip_id`
cascades): a clip and its first render are inserted first and `current_render_id` is set in a
second step; deleting a clip deletes its renders, deleting a render clears the pointer.

### clip_renders
`id` · `clip_id` FK · `user_id` · `job_id` FK (nullable) · `version` int ·
`aspect_ratio` · `width` · `height` · `storage_key` · `size_bytes` · `duration_seconds` ·
`has_subtitles` bool · `status` (`pending|rendering|ready|failed`) · `error` · `created_at` ·
`completed_at`. Unique `(clip_id, version)`.

### integration_connections
`id` · `user_id` FK · `provider` (`twitch|youtube|obs_companion`) · `status`
(`connected|revoked|error`) · `external_account_id` · `display_name` · `scopes` text[] ·
`access_token_encrypted` · `refresh_token_encrypted` · `token_expires_at` · `metadata` jsonb ·
`created_at` · `updated_at`. Unique `(user_id, provider, external_account_id)`.

### notifications
`id` · `user_id` FK · `type` (`job_completed|job_failed|clip_ready|quota_warning|billing|system`) ·
`title` · `body` · `data` jsonb · `read_at` · `created_at`. Index `(user_id, read_at, created_at desc)`.
`quota_warning` rows carry `{ threshold (80|100), usedSeconds, limitSeconds, periodStart,
periodEnd }` and exist at most once per threshold and billing period. E-mail copies are not
stored (docs/API.md → Notifications).

### companion_devices (live capture, phase 5)
A computer running the OBS companion (docs/LIVE_CAPTURE.md). `id` · `user_id` FK (cascade) ·
`name` (1–60) · `token_hash` (SHA-256 hex of the whole `cfd_…` token, unique; the token itself is
never stored) · `token_prefix` (the 8 characters after `cfd_`, for display) · `created_at` ·
`last_seen_at` (last authenticated request, written at most every 30 s) · `revoked_at`.
Index `(user_id, created_at)`.

### live_sessions (live capture)
`id` · `user_id` FK (cascade) · `device_id` FK → `companion_devices` (cascade) · `project_id`
FK → `projects` (cascade; unique — each session owns one project with `source_kind = live`,
`source_platform = obs`) · `status` (`waiting|live|ending|ended|error`) · `segment_seconds` int
(20–120) · `settings` jsonb (`AnalysisSettings`, capped by the plan) · `companion_state` jsonb
(nullable: the last heartbeat `{ obsConnected, streaming, recording, uploading, queueLength,
lastError }`) · `stop_requested` bool · `end_reason` (≤ 500, pt-BR: "dispositivo revogado",
"companion offline", "minutos de processamento do mês esgotados", "projeto excluído", "encerrada
pelo companion", …) · `obs` jsonb (nullable: `{ version, recordingPath }` sent at creation) ·
`started_at` · `ended_at` · `last_segment_at` · `last_heartbeat_at` · `updated_at`.
Indexes: `(user_id, started_at desc)`; unique `(device_id)` filtered on
`status IN ('waiting','live','ending')` — at most one non-terminal session per device;
`(status, last_heartbeat_at)` with the same filter (the reaper).

### live_segments (live capture)
`id` · `session_id` FK (cascade) · `user_id` FK (cascade) · `sequence` int · `media_asset_id`
FK → `media_assets` (kind `live_segment`; set null) · `job_id` FK → `processing_jobs` (its
`live_segment` job; set null) · `start_offset_seconds` numeric(12,3) (session time) ·
`duration_seconds` numeric(12,3) · `is_final` bool · `status`
(`received|queued|analyzing|analyzed|failed`, mirrors the job) · `error` (≤ 2000, the job's
message when it failed) · `live_context` jsonb (nullable: the `liveContext` the job reported, the
`context` of the next segment's payload) · `created_at` · `updated_at`.
Unique `(session_id, sequence)` (uploads are idempotent per sequence); indexes `(job_id)`,
`(media_asset_id)`, `(user_id)`. The claim of a `live_segment` job checks, through `job_id`,
that every lower-sequence job of the session is terminal.

All three tables were added by the `20261023182826_LiveCapture` migration (new tables only, so it
applies to a database that already has data).

## Relationships

```
users 1─1 user_profiles
users 1─* refresh_tokens, subscriptions, usage_records, media_assets, projects,
          processing_jobs, clips, notifications, integration_connections, companion_devices
companion_devices 1─* live_sessions (at most one waiting/live/ending)
projects 1─0..1 live_sessions 1─* live_segments ─ media_assets (live_segment), processing_jobs (live_segment)
plans 1─* subscriptions
subscriptions 1─* subscription_events
projects 1─* processing_jobs 1─* processing_job_events
processing_jobs 1─* clip_candidates
projects 1─* clips 1─* clip_renders
clip_candidates 1─0..1 clips (candidate_id)
media_assets 1─0..1 projects (source), 1─* clip_renders (via storage_key only)
```

## Deletion strategy

- **Project delete** → soft delete project; cascade soft-delete clips; jobs cancelled if
  running; a cleanup job purges storage objects (source, analysis, renders, thumbnails) and
  hard-deletes rows after `retentionDays`.
- **Clip delete** → soft delete; renders purged by cleanup.
- **Account delete** → user marked `deletion_requested_at`, sessions revoked immediately,
  all projects soft-deleted; purge job removes storage, hard-deletes user-owned rows and
  anonymises `payment_events`/`usage_records` (kept for accounting, without PII).
- **Pending uploads** expire after `Retention:TempUploadHours`; the cleanup job marks the asset
  `deleted` and deletes the storage object. An asset deleted while its signed upload URL was still
  valid gets `expires_at` = the URL's expiry and the cleanup job deletes its storage key once more
  after it (a presigned S3 PUT cannot be revoked; the Local PUT refuses non-pending targets).

## Migrations

`backend/src/ClipForge.Infrastructure/Migrations`, applied automatically on API startup in
development (`Database:MigrateOnStartup=true`) and by `dotnet ef database update` in
production pipelines. Each migration is documented in `docs/migrations/CHANGELOG.md`.
