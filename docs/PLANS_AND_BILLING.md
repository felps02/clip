# Plans, quotas and billing

## Plans

Plans are configuration, upserted into `plans` on API startup (`PlanSeeder`). Defaults live
in `backend/src/ClipForge.Api/appsettings.json` under `Plans` and can be overridden by
environment (`Plans__pro__Limits__MonthlyProcessingMinutes=…`). Prices are nullable: a plan
without a `PriceMonthlyCents` is shown without a price ("sob consulta") and cannot be bought;
`PriceMonthlyCents: 0` means the plan is free (shown as "Grátis"): `GET /api/plans` reports it with
`purchasable: false` even when a provider price id is configured, and `POST /api/billing/checkout`
refuses it (`409 conflict`) without calling the payment provider — going back to free is a
cancellation. The free plan uses the `base` Whisper model: `tiny` mis-detects the spoken language too
often to be the default. Creator and pro use `small`, which transcribes noticeably better (accents,
names, overlapping game audio) at about 3× the CPU time of `base` on the worker (~480 MB model, which
`scripts/dev-setup.ps1` downloads; the worker's `WHISPER_MODEL` is the largest model it will load and
defaults to `small`).

| Limit | free | creator | pro |
|---|---|---|---|
| monthlyProcessingMinutes | 60 | 600 | 2400 |
| maxSourceDurationMinutes | 120 | 360 | 720 |
| maxFileSizeMb | 2048 | 8192 | 20480 |
| maxActiveProjects | 3 | 20 | 0 (unlimited) |
| maxCandidatesPerJob | 15 | 30 | 50 |
| maxAutoRenderPerJob | 15 | 30 | 50 |
| maxRendersPerMonth | 10 | 200 | 0 (unlimited) |
| maxRenderHeight | 720 | 1080 | 1080 |
| maxConcurrentJobs | 1 | 2 | 3 |
| priority | 0 | 1 | 2 |
| retentionDays | 7 | 30 | 90 |
| storageQuotaGb | 5 | 50 | 200 |
| transcriptionModel | base | small | small |
| allowWebcamLayout | false | true | true |
| allowCustomSubtitleStyles | false | true | true |
| assistantMessagesPerMonth | 100 | 1000 | 5000 |

These are starting values for the commercial team to tune; nothing depends on them in code.
`maxAutoRenderPerJob` equals `maxCandidatesPerJob` so that "every moment found becomes a clip" (the
default when a project does not send `autoRenderTop`) holds on every plan; auto-rendered clips of an
analysis do not count as renders (`maxRendersPerMonth` counts the renders the user requests).
`assistantMessagesPerMonth` bounds the settings assistant (0 = unlimited); it runs on the product's own
local model or Gemini's free tier, so the limit protects CPU time rather than a paid bill.

## Quota enforcement (server side, `QuotaService`)

- `CanStartJob(user, durationSeconds)`: active project count, concurrent jobs, source
  duration, and `used + reserved + requested ≤ monthlyProcessingMinutes·60` for the current
  period. Called at project creation (duration unknown → only count checks), at re-analysis
  (concurrent jobs; plus the active-project count when the project is `failed` or `cancelled`,
  which do not count as active — the new analysis makes the project active again) and at
  `/probe` (duration known → reserve; a re-sent probe or a later attempt replaces the job's
  reservation instead of adding to it).
- `CanRender(user)`: renders in period < `maxRendersPerMonth` **and** active jobs <
  `maxConcurrentJobs` (a `render_clip` job counts like any other job, also when it is created
  from a candidate). At most 10 clips can be cut from one detected moment.
- `CanUpload(user, sizeBytes)`: file size and storage quota (`Σ media_assets.size_bytes`
  not deleted).
- Assistant messages (`POST /api/assistant/chat`): answered messages in the period (usage kind
  `assistant_message`) < `assistantMessagesPerMonth`, else `409 plan_limit`. The message is recorded
  under the per-user lock before the model call and removed when no answer is produced; a per-user rate
  limit (`RateLimiting:AssistantPerMinute`, 20) applies on top.
- Every check-then-act sequence (project creation, re-analysis, render request, upload
  creation) runs under a per-user PostgreSQL advisory lock inside the transaction that inserts
  the row (`QuotaService.WithUserLockAsync`), so parallel requests of one user queue up and
  cannot exceed the caps.
- Usage is written as `usage_records` when a job finishes: `completed` → the seconds reserved
  at probe time; `cancelled` after the analysis started (status past `preparing_media`) → the
  reserved seconds too (the expensive work ran and the candidates stay usable, so cancelling at
  90 % is never a free analysis; a cancel while still preparing the media releases the
  reservation); terminal `failed` → the `processedSeconds` the worker reports, capped by the
  reservation (nothing when the worker reports none, e.g. a lease that expired). A render
  request writes a `render` record, an answered assistant message an `assistant_message` record.
  Records are append-only and carry the reason in `metadata` (the only exception: an assistant
  reservation whose call produced no answer is removed).
- The period is the subscription's `current_period_start/end`; for `free` it rolls monthly
  from the subscription creation date.
- Warnings: the dashboard shows a warning at 80 % and 100 % of processing minutes and when
  the plan blocks an action (clear message + upgrade link). Whenever minutes are charged (a job
  completes, or a cancelled/failed analysis is charged) the API also checks the thresholds and
  creates one `quota_warning` notification per threshold and period (80 %, then 100 %; a jump
  straight past 100 % creates only the 100 % one), e-mailed when the user keeps
  `emailOnQuotaWarning` on and has confirmed the address (docs/API.md → Notifications).

## Subscriptions

- Registration creates `subscription(plan=free, status=active, provider=none)`.
- Paid plans go through the `IPaymentProvider` abstraction:
  `CreateCheckoutSession(user, plan, successUrl, cancelUrl)`, `CreatePortalSession(user)`,
  `CancelAtPeriodEnd(subscription)`, `ChangePlan(subscription, price, planCode)` (in-place
  upgrade/downgrade), `ParseWebhook(rawBody, signatureHeader)`.
- Providers: `StripePaymentProvider` (implemented: Checkout Sessions in `subscription`
  mode, Billing Portal, webhooks) and `MercadoPagoPaymentProvider` (planned: Preapproval
  API for recurring payments + PIX/boleto through Checkout Pro; same abstraction).
  `NonePaymentProvider` makes billing endpoints answer `409 payments_not_configured` so the UI
  can show "pagamentos ainda não configurados" instead of a broken button.
- **Entitlements are granted only by webhooks** (`checkout.session.completed`,
  `customer.subscription.created/updated/deleted`, `invoice.paid`, `invoice.payment_failed`),
  **and only for a confirmed payment**. The success redirect never changes the plan; the web
  polls `/api/billing/subscription` after returning from checkout.
  - `checkout.session.completed` grants the plan only with `payment_status` `paid` or
    `no_payment_required` (100 % coupon, trial). `unpaid` — an abandoned or declined payment,
    or boleto/PIX still clearing — is recorded as `ignored`: nothing is granted nor bound.
  - A provider subscription is bound to the user's row (and grants its plan) only while it is
    `active` or `trialing`. Checkout creates the subscription as `incomplete` before the first
    invoice is paid, so `customer.subscription.created` usually arrives `incomplete` and is
    ignored; the plan arrives with `customer.subscription.updated` (`active`) or the paid
    `checkout.session.completed`, whichever comes first. A late `incomplete` delivery never
    changes a bound row.
  - Status mapping of a bound row: `active`/`trialing` → entitled; `past_due` → still entitled
    (grace period while the provider retries the renewal); `unpaid` (retries exhausted),
    `incomplete_expired`, `paused` → `expired`, `canceled` → `cancelled`: the user returns to a
    free row. If the subscription becomes active again (the open invoice is paid) the row is
    entitled again and the free fallback row is expired. Unknown statuses change nothing.
  - Late and out-of-order events never undo newer state (the provider does not deliver events
    in order, and a failed event is redelivered later — possibly after the subscription
    ended). A `cancelled` row is final: the provider never reactivates a canceled subscription,
    so nothing that arrives after `customer.subscription.deleted` (a stale `active`, a late
    `past_due`, an invoice, the checkout) changes it. Each row keeps
    `last_provider_event_at`, the provider time (Stripe `Event.Created`) of the newest
    `customer.subscription.*` event applied to it; an older subscription event is ignored
    (e.g. `past_due` delivered after `unpaid` never gives the plan back), and so are older
    `invoice.*` events, which are compared with it but never advance it (a renewal's
    `customer.subscription.updated` is older than its `invoice.paid` and must still apply); a
    late `invoice.payment_failed` is still announced while the row is `past_due`. Events of the
    same second (the provider's resolution) apply in arrival order. A
    `checkout.session.completed` changes nothing on a row that lapsed since or holds newer
    state, and on a row its subscription events bound first it only fills in the plan (while
    the row still has `free`) and the customer id. A subscription the provider reported deleted
    (a stored `subscription_deleted` event, whatever its processing outcome) is never bound to
    the user, even when the deletion arrived before anything bound it.
- Billing notifications (always in-app; e-mailed when `emailOnBilling` is on and the address is
  confirmed): plan activated
  or changed, payment failed (`invoice.payment_failed`), cancellation scheduled
  (`cancel_at_period_end` set at the provider, e.g. in the portal), subscription ended or
  suspended (`unpaid`).
- Upgrade/downgrade with an active paid subscription: `POST /api/billing/checkout` switches
  the existing provider subscription to the new price in place
  (`IPaymentProvider.ChangePlanAsync`, prorated by the provider) and answers
  `planChanged: true` — a customer never ends up with two subscriptions. The plan changes
  when `customer.subscription.updated` arrives with the new price.
- Webhook matching: an event that names a provider subscription id is applied only to the
  row bound to that id; an unknown id is ignored (never matched by customer or user id), and
  `customer.subscription.created` binds the user's row only while it is not tied to another
  provider subscription. Should a `checkout.session.completed` still arrive for a user whose
  row is bound to a different subscription (a second checkout), the new subscription wins,
  the previous one is retired at the provider (`cancel_at_period_end`) and its id is kept in
  the `subscription_events` data for support.
- Webhooks: signature verified with the provider SDK (`400 invalid_signature`; `409
  payments_not_configured` without a webhook secret); event stored in `payment_events` with a
  unique `(provider, provider_event_id)` → a duplicate of a `processed`/`ignored` event is
  acknowledged (`200`) without being applied again; processing is transactional and records a
  `subscription_events` row; concurrent deliveries of one event serialise on its row, and
  events about the same user are applied one at a time (the per-user advisory lock of the quota
  checks), so the ordering rules above never decide on a stale read. A
  processing failure rolls back, is stored with `status=failed` and answered `500` (problem
  `code: internal_error`) so the provider redelivers; the redelivery processes the event again.
  Late events that change nothing are answered `200` with `status: ignored`.
- Downgrade/cancel: `cancel_at_period_end=true`, the plan stays until `current_period_end`,
  then the user returns to `free`. Data beyond the new limits is kept but new work is
  blocked until usage fits.

## Configuring Stripe (test mode)

1. Create products/prices in the Stripe dashboard (recurring monthly). Copy the price ids
   into `Plans__creator__ProviderPriceId` / `Plans__pro__ProviderPriceId`.
2. Set `Payments__Provider=Stripe`, `Payments__Stripe__SecretKey=sk_test_…`.
3. Forward webhooks locally: `stripe listen --forward-to localhost:5080/api/webhooks/stripe`
   and set `Payments__Stripe__WebhookSecret=whsec_…`.
4. Use Stripe test cards. No real charges happen in test mode.

Without credentials the structural flow is present and covered by tests using a fake
provider; no payment is ever simulated as real.
