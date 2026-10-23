# Database migrations

Migrations live in `backend/src/ClipForge.Infrastructure/Migrations` (EF Core 10, Npgsql). They are applied
automatically on API startup when `Database:MigrateOnStartup=true` (Development) and with
`dotnet ef database update --project src/ClipForge.Infrastructure --startup-project src/ClipForge.Api`
elsewhere. Create a new migration with

```
cd backend
dotnet ef migrations add <Name> --project src/ClipForge.Infrastructure --startup-project src/ClipForge.Api
```

(`dotnet-ef` is a local tool: `dotnet tool restore` installs it from `.config/dotnet-tools.json`.)

## 20261022214655_InitialCreate

First schema, everything from `docs/DATA_MODEL.md` except the phase-5 tables (`live_sessions`,
`live_segments`, `companion_devices`). Conventions: snake_case names (EFCore.NamingConventions), `uuid`
keys generated as version-7 GUIDs by the application, `timestamptz` timestamps, enums stored as
`character varying(40)` snake_case strings, JSON documents as `jsonb`.

Tables:

- Identity: `users` (`AspNetUsers` renamed; adds `display_name`, `created_at`, `deleted_at`,
  `deletion_requested_at`, partial index on `deletion_requested_at`), `user_claims`, `user_logins`,
  `user_tokens` (roles are not used).
- `user_profiles` (PK = `user_id`, jsonb `editing_preferences` / `notification_preferences`).
- `refresh_tokens` (unique `token_hash`, index `user_id`).
- `plans` (unique `code`, jsonb `limits` and `features`).
- `subscriptions` (index `user_id`, `provider_customer_id`; unique partial index on
  `provider_subscription_id`), `subscription_events` (index `(subscription_id, created_at)`).
- `usage_records` (`user_id` nullable with `ON DELETE SET NULL` so records survive account purge
  anonymised; index `(user_id, kind, period_start)`, `job_id`).
- `payment_events` (unique `(provider, provider_event_id)`, index `user_id`; no FK to users so the audit
  trail survives purges).
- `media_assets` (unique `storage_key`, indexes `(user_id, status)`, `expires_at`).
- `projects` (unique partial `(user_id, client_request_id)` for idempotent creation, `(user_id, created_at desc)`,
  `media_asset_id`; FK to `media_assets` with `SET NULL`).
- `processing_jobs` (indexes `(status, priority desc, queued_at)` for claiming, `(user_id, created_at desc)`,
  `project_id`, partial `ix_processing_jobs_active` on `(user_id, status)` for active statuses, partial on
  `lease_expires_at`), `processing_job_events` (index `(job_id, created_at)`).
- `clip_candidates` (stored generated column `duration_seconds = end_seconds - start_seconds`, indexes
  `(project_id, score desc)`, `job_id`, `user_id`).
- `clips` (indexes `(user_id, created_at desc)`, `project_id`, `candidate_id`, `current_render_id`; FK to
  `clip_candidates` with `SET NULL`). `current_render_id` is an indexed plain column without a database
  foreign key here (added by `20261023124808_ClipCurrentRenderForeignKey`).
- `clip_renders` (unique `(clip_id, version)`, index `job_id`; FK to `processing_jobs` with `SET NULL`).
- `integration_connections` (unique `(user_id, provider, external_account_id)`, `scopes text[]`).
- `notifications` (index `(user_id, read_at, created_at desc)`).

All user-owned tables cascade on user delete; the account purge service deletes rows explicitly before the
user row anyway (storage objects first).

## 20261023124808_ClipCurrentRenderForeignKey

`clips.current_render_id` becomes a real foreign key to `clip_renders(id)` with `ON DELETE SET NULL`
(`fk_clips_clip_renders_current_render_id`, on the existing index). Before the constraint is added, pointers
that are dangling or name a render of another clip are cleared (`UPDATE clips SET current_render_id = NULL
WHERE … NOT EXISTS (render with that id and this clip_id)`), so the migration also applies to databases
written by earlier versions.

`clips` ↔ `clip_renders` now form a cycle (`clip_renders.clip_id` cascades from `clips`). PostgreSQL resolves
it in every delete path (deleting a render clears the pointer; deleting a clip, project or user removes the
renders in the same statement), and the API already inserts a clip and its first render before setting
`current_render_id` (two saves). The test-suite reset truncates all tables in one `TRUNCATE … CASCADE`
(`TruncateCascadePostgresAdapter` for Respawn) because Respawn's default PostgreSQL adapter disables the
triggers of cyclic tables, which needs a superuser.

Down: drops the constraint (the column and index stay).

## 20261023124846_NotificationPreferencesContract

Data only. `user_profiles.notification_preferences` documents of the previous shape (`emailOnJobCompleted`,
`emailOnJobFailed`, `emailBilling`, `inApp`) never held a user's choice — the old settings page sent keys the
API dropped — and are rewritten to the defaults of the preference contract
(`{"emailOnJobCompleted": false, "emailOnJobFailed": true, "emailOnQuotaWarning": true, "emailOnBilling": true}`).
A document is considered old when it is not a JSON object or has no `emailOnQuotaWarning` key; documents in the
new shape are untouched. `editing_preferences` needs no rewrite: missing fields are read at their defaults
(docs/DATA_MODEL.md → user_profiles).

Down: no-op (the replaced documents only held defaults; the new shape reads fine with older code).

## 20261023144842_WebhookEventOrdering

Payment webhooks arrive out of order and failed ones are redelivered later (docs/PLANS_AND_BILLING.md →
Subscriptions), so the newest provider state must win:

- `subscriptions.last_provider_event_at` (nullable `timestamptz`): provider time (Stripe `Event.Created`) of the
  newest `customer.subscription.*` event applied to the row. Existing rows start at `NULL`, i.e. the next dated
  event applies and sets it.
- `payment_events.kind` (nullable `varchar(40)`, provider-neutral kind such as `subscription_deleted`),
  `payment_events.provider_subscription_id` (nullable `varchar(200)`) and `payment_events.occurred_at`
  (nullable `timestamptz`), plus index `ix_payment_events_provider_provider_subscription_id` on
  `(provider, provider_subscription_id)`: a stored deletion keeps a late activation of that subscription from
  being bound, even if it was never bound before.
- Data: stored Stripe `customer.subscription.deleted` events get the three new columns from their payload
  (`data.object.id`, `created`), so deletions received before this migration count too. Other events keep
  `NULL` (nothing reads them for older events).

Down: drops the index and the four columns.
