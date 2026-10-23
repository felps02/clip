using System.Text;
using ClipForge.Api.Common;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Infrastructure.Payments;
using ClipForge.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Webhooks;

public static class WebhooksEndpoints
{
    public static IEndpointRouteBuilder MapWebhooksEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/webhooks/stripe", async (HttpContext http, [FromHeader(Name = "Stripe-Signature")] string? signature, IPaymentProvider payments, WebhookService service, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) throw AppException.Validation("Corpo vazio.");

            // 400 invalid_signature (bad Stripe-Signature) / 409 payments_not_configured (no webhook secret) come from the provider.
            var evt = payments.ParseWebhook(body, signature);
            var outcome = await service.HandleAsync(evt, ct);
            // A processing failure answers 5xx so the provider redelivers; the event stays stored with status failed and
            // the redelivery processes it again.
            return outcome.Status == PaymentEventStatus.Failed
                ? Problems.Result(500, ErrorCodes.InternalError, "Falha ao processar o evento de pagamento; ele será processado novamente na próxima entrega.")
                : Results.Ok(new { received = true, status = EnumNames.ToSnake(outcome.Status) });
        }).WithTags("Webhooks").AllowAnonymous().DisableAntiforgery();

        return app;
    }
}

/// <summary>
/// Idempotent webhook processing: payment_events unique (provider, provider_event_id); subscription changes are
/// applied in a transaction with a subscription_events history row. Entitlements only change here, and only for a
/// confirmed payment: a completed checkout must be paid (or need no payment), and a provider subscription is bound to
/// the user and grants its plan only while active/trialing — never while "incomplete" (first invoice unpaid).
/// The provider does not deliver events in order and redelivers failed ones later, so a late event never undoes newer
/// state: a cancelled provider subscription is final, an older subscription snapshot never overwrites a newer one
/// (<see cref="Subscription.LastProviderEventAt"/>), a checkout of a subscription that lapsed since grants nothing, and
/// a subscription the provider already reported deleted is never bound. Events about one user are applied one at a time.
/// </summary>
public sealed class WebhookService
{
    private static readonly string DeletedKind = EnumNames.ToSnake(WebhookEventKind.SubscriptionDeleted);

    private readonly AppDbContext _db;
    private readonly SubscriptionService _subscriptions;
    private readonly NotificationService _notifications;
    private readonly IPaymentProvider _payments;
    private readonly IClock _clock;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(AppDbContext db, SubscriptionService subscriptions, NotificationService notifications, IPaymentProvider payments, IClock clock, ILogger<WebhookService> logger)
    {
        _db = db;
        _subscriptions = subscriptions;
        _notifications = notifications;
        _payments = payments;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Stores the event (once per provider event id) and processes it unless it already reached a final state.
    /// processed / ignored duplicates are acknowledged without reprocessing; received (a crash before processing) and
    /// failed events are processed again. Concurrent deliveries of one event serialise on the payment_events row lock,
    /// so an event is never applied twice.
    /// </summary>
    public async Task<PaymentEvent> HandleAsync(PaymentWebhookEvent evt, CancellationToken ct)
    {
        var existing = await _db.PaymentEvents.AsNoTracking().FirstOrDefaultAsync(e => e.Provider == evt.Provider && e.ProviderEventId == evt.ProviderEventId, ct);
        if (existing is not null && IsFinal(existing.Status))
        {
            _logger.LogInformation("Duplicate webhook event {Provider}/{EventId} acknowledged ({Status})", evt.Provider, evt.ProviderEventId, existing.Status);
            return existing;
        }

        var recordId = existing?.Id;
        if (recordId is null)
        {
            var record = new PaymentEvent
            {
                Provider = evt.Provider,
                ProviderEventId = evt.ProviderEventId,
                Type = evt.Type,
                Kind = EnumNames.ToSnake(evt.Kind),
                ProviderSubscriptionId = evt.ProviderSubscriptionId,
                OccurredAt = evt.OccurredAt,
                Payload = evt.RawPayload,
                ReceivedAt = _clock.UtcNow,
                Status = PaymentEventStatus.Received,
            };
            _db.PaymentEvents.Add(record);
            try
            {
                await _db.SaveChangesAsync(ct);
                recordId = record.Id;
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
            {
                // Concurrent delivery of the same event: it is processed (or acknowledged) below, under the row lock.
                _db.ChangeTracker.Clear();
                recordId = await _db.PaymentEvents.Where(e => e.Provider == evt.Provider && e.ProviderEventId == evt.ProviderEventId).Select(e => e.Id).FirstAsync(ct);
            }
        }
        _db.ChangeTracker.Clear();

        PaymentEvent locked;
        await using (var transaction = await _db.Database.BeginTransactionAsync(ct))
        {
            try
            {
                locked = (await _db.PaymentEvents.FromSql($"SELECT * FROM payment_events WHERE id = {recordId.Value} FOR UPDATE").ToListAsync(ct)).Single();
                if (IsFinal(locked.Status))
                {
                    await transaction.CommitAsync(ct);
                    return locked;
                }
                // A failed event stored before these columns existed is redelivered: record them now.
                locked.Kind ??= EnumNames.ToSnake(evt.Kind);
                locked.ProviderSubscriptionId ??= evt.ProviderSubscriptionId;
                locked.OccurredAt ??= evt.OccurredAt;

                var (status, userId, subscriptionId) = await ApplyAsync(evt, ct);
                locked.Status = status;
                locked.UserId = userId;
                locked.SubscriptionId = subscriptionId;
                locked.ProcessedAt = _clock.UtcNow;
                locked.Error = null;
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _logger.LogError(ex, "Webhook {Provider}/{EventId} failed", evt.Provider, evt.ProviderEventId);
                _db.ChangeTracker.Clear();
                _notifications.DiscardPending();
                var failed = await _db.PaymentEvents.FirstAsync(e => e.Id == recordId.Value, CancellationToken.None);
                failed.Status = PaymentEventStatus.Failed;
                failed.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
                failed.ProcessedAt = _clock.UtcNow;
                await _db.SaveChangesAsync(CancellationToken.None);
                return failed;
            }
        }

        await _notifications.FlushAsync();
        return locked;
    }

    private static bool IsFinal(PaymentEventStatus status) => status is PaymentEventStatus.Processed or PaymentEventStatus.Ignored;

    /// <summary>Stripe checkout payment_status values that confirm the payment (no_payment_required: 100 % coupon, free trial).</summary>
    public static bool IsPaymentConfirmed(string? paymentStatus)
        => paymentStatus?.Trim().ToLowerInvariant() is "paid" or "no_payment_required";

    private async Task<(PaymentEventStatus Status, Guid? UserId, Guid? SubscriptionId)> ApplyAsync(PaymentWebhookEvent evt, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        if (evt.Kind != WebhookEventKind.Unknown) await LockEventUsersAsync(evt, ct);
        switch (evt.Kind)
        {
            case WebhookEventKind.CheckoutCompleted:
            {
                if (evt.UserId is not { } userId) return (PaymentEventStatus.Ignored, null, null);
                if (!IsPaymentConfirmed(evt.PaymentStatus))
                {
                    // e.g. boleto/PIX still pending, or a declined payment: nothing is granted (nor bound) here; the plan
                    // arrives with customer.subscription.updated once the subscription is active.
                    _logger.LogInformation("Checkout {EventId} of user {UserId} completed without a confirmed payment ({PaymentStatus}); no entitlement",
                        evt.ProviderEventId, userId, evt.PaymentStatus ?? "unknown");
                    return (PaymentEventStatus.Ignored, userId, null);
                }
                if (!string.IsNullOrWhiteSpace(evt.ProviderSubscriptionId))
                {
                    var bound = await _db.Subscriptions.Include(s => s.Plan).FirstOrDefaultAsync(s => s.ProviderSubscriptionId == evt.ProviderSubscriptionId, ct);
                    if (bound is not null) return await ConfirmBoundCheckoutAsync(bound, userId, evt, ct);
                    if (await ProviderReportedDeletionAsync(evt, ct))
                    {
                        // A late delivery (e.g. a failed event redelivered) of the checkout of a subscription the provider
                        // has deleted since, before anything bound it: a deleted subscription never becomes active again.
                        _logger.LogInformation("Ignoring late {Type} {EventId} of user {UserId}: provider subscription {SubscriptionId} was already deleted",
                            evt.Type, evt.ProviderEventId, userId, evt.ProviderSubscriptionId);
                        return (PaymentEventStatus.Ignored, userId, null);
                    }
                }
                var plan = await ResolvePlanAsync(evt, ct);
                var current = await CurrentSubscriptionAsync(userId, ct);
                if (current is null)
                {
                    current = await _subscriptions.CreateFreeSubscriptionAsync(userId, ct);
                    await _db.SaveChangesAsync(ct);
                }
                var fromCode = current.Plan.Code;
                var previousProviderSubscriptionId = current.ProviderSubscriptionId;
                // A second checkout while a paid subscription was active (should not happen through the API, which
                // switches plans in place): the new subscription is what the user just paid for, so the previous one
                // is retired at the provider (no renewal) and its later webhooks are ignored (unknown id).
                var replaced = current.Provider != SubscriptionProvider.None
                    && !string.IsNullOrWhiteSpace(current.ProviderSubscriptionId)
                    && !string.IsNullOrWhiteSpace(evt.ProviderSubscriptionId)
                    && !string.Equals(current.ProviderSubscriptionId, evt.ProviderSubscriptionId, StringComparison.Ordinal)
                    ? current.ProviderSubscriptionId
                    : null;
                current.Provider = ParseProvider(evt.Provider);
                current.ProviderCustomerId = evt.CustomerId ?? current.ProviderCustomerId;
                current.ProviderSubscriptionId = evt.ProviderSubscriptionId ?? current.ProviderSubscriptionId;
                if (!string.Equals(previousProviderSubscriptionId, current.ProviderSubscriptionId, StringComparison.Ordinal))
                {
                    // Another provider subscription: the ordering clock restarts with its own events.
                    current.LastProviderEventAt = null;
                }
                current.Status = SubscriptionStatus.Active;
                current.CancelAtPeriodEnd = false;
                current.CancelledAt = null;
                if (plan is not null)
                {
                    current.PlanId = plan.Id;
                    current.Plan = plan;
                }
                current.CurrentPeriodStart = evt.PeriodStart ?? now;
                current.CurrentPeriodEnd = evt.PeriodEnd ?? now.AddMonths(1);
                current.UpdatedAt = now;
                AddEvent(current, plan is not null && plan.Code != fromCode ? SubscriptionEventType.PlanChanged : SubscriptionEventType.Renewed, fromCode, current.Plan.Code, evt, replaced);
                // Once per activation: subscription.created/updated may have bound and switched the row already.
                if (current.Plan.Code != fromCode || !string.Equals(previousProviderSubscriptionId, current.ProviderSubscriptionId, StringComparison.Ordinal))
                {
                    NotifyBilling(userId, "Assinatura ativada", $"Seu plano {current.Plan.Name} está ativo. Obrigado!");
                }
                await _db.SaveChangesAsync(ct);
                if (replaced is not null)
                {
                    await RetireReplacedSubscriptionAsync(replaced, evt.ProviderSubscriptionId!, ct);
                }
                return (PaymentEventStatus.Processed, userId, current.Id);
            }

            case WebhookEventKind.SubscriptionCreated:
            case WebhookEventKind.SubscriptionUpdated:
            {
                var subscription = await FindByProviderAsync(evt, ct);
                var providerStatus = evt.SubscriptionStatus?.Trim().ToLowerInvariant();
                if (subscription is null)
                {
                    if (evt.UserId is not { } uid || string.IsNullOrWhiteSpace(evt.ProviderSubscriptionId))
                    {
                        return (PaymentEventStatus.Ignored, null, null);
                    }
                    if (providerStatus is not ("active" or "trialing"))
                    {
                        // Only a subscription whose payment is confirmed is bound to the user: "incomplete" (Checkout
                        // creates it before the first invoice is paid), "incomplete_expired", "unpaid"... grant nothing.
                        _logger.LogInformation("Ignoring {Type} for subscription {SubscriptionId} of user {UserId}: status {Status} grants no plan",
                            evt.Type, evt.ProviderSubscriptionId, uid, providerStatus ?? "unknown");
                        return (PaymentEventStatus.Ignored, uid, null);
                    }
                    if (await ProviderReportedDeletionAsync(evt, ct))
                    {
                        // A late snapshot (e.g. a failed event redelivered) of a subscription the provider has deleted
                        // since, before anything bound it: a deleted subscription never becomes active again.
                        _logger.LogInformation("Ignoring late {Type} {EventId} for subscription {SubscriptionId} of user {UserId}: the provider already deleted it",
                            evt.Type, evt.ProviderEventId, evt.ProviderSubscriptionId, uid);
                        return (PaymentEventStatus.Ignored, uid, null);
                    }
                    // customer.subscription.created may arrive before checkout.session.completed: bind the user's
                    // row only while it is not yet tied to a provider subscription. A row already bound to another
                    // id means this event belongs to a replaced/orphaned subscription and must not clobber it.
                    var currentOfUser = await CurrentSubscriptionAsync(uid, ct);
                    if (currentOfUser is not null && !string.IsNullOrWhiteSpace(currentOfUser.ProviderSubscriptionId))
                    {
                        _logger.LogWarning("Ignoring {Type} for unknown provider subscription {SubscriptionId}: user {UserId} is bound to {CurrentId}",
                            evt.Type, evt.ProviderSubscriptionId, uid, currentOfUser.ProviderSubscriptionId);
                        return (PaymentEventStatus.Ignored, uid, currentOfUser.Id);
                    }
                    subscription = currentOfUser ?? await _subscriptions.CreateFreeSubscriptionAsync(uid, ct);
                    await _db.SaveChangesAsync(ct);
                    subscription.Provider = ParseProvider(evt.Provider);
                    subscription.ProviderSubscriptionId = evt.ProviderSubscriptionId;
                    subscription.ProviderCustomerId ??= evt.CustomerId;
                    subscription.LastProviderEventAt = null;
                }
                else if (subscription.Status == SubscriptionStatus.Cancelled)
                {
                    // The provider never reactivates a canceled subscription (buying again creates a new one), so whatever
                    // arrives after the cancellation — a stale "active", a late "past_due" — changes nothing.
                    _logger.LogInformation("Ignoring {Type} {EventId} ({Status}) for subscription {SubscriptionId}: it was cancelled",
                        evt.Type, evt.ProviderEventId, providerStatus ?? "unknown", evt.ProviderSubscriptionId);
                    return (PaymentEventStatus.Ignored, subscription.UserId, subscription.Id);
                }
                else if (IsStale(subscription, evt))
                {
                    LogLate(evt, subscription);
                    return (PaymentEventStatus.Ignored, subscription.UserId, subscription.Id);
                }
                var fromCode = subscription.Plan.Code;
                var fromStatus = subscription.Status;
                var wasEntitled = subscription.IsEntitled;
                var wasCancelling = subscription.CancelAtPeriodEnd;
                var previousEnd = subscription.CurrentPeriodEnd;

                var plan = await ResolvePlanAsync(evt, ct);
                if (plan is not null && plan.Id != subscription.PlanId)
                {
                    subscription.PlanId = plan.Id;
                    subscription.Plan = plan;
                }
                // "incomplete" (and unknown statuses) never change a bound row: the row was bound by a confirmed
                // payment, so such an event is a late/out-of-order delivery of the subscription's first state.
                if (providerStatus is not null && TryMapStatus(providerStatus, out var mapped)) subscription.Status = mapped;
                if (evt.PeriodStart is { } ps) subscription.CurrentPeriodStart = ps;
                if (evt.PeriodEnd is { } pe) subscription.CurrentPeriodEnd = pe;
                if (evt.CancelAtPeriodEnd is { } cape)
                {
                    subscription.CancelAtPeriodEnd = cape;
                    if (cape) subscription.CancelledAt ??= now;
                    else subscription.CancelledAt = null;
                }
                subscription.ProviderCustomerId = evt.CustomerId ?? subscription.ProviderCustomerId;
                subscription.UpdatedAt = now;
                RecordSnapshotTime(subscription, evt);

                var type = evt.Kind == WebhookEventKind.SubscriptionCreated ? SubscriptionEventType.Created
                    : subscription.Plan.Code != fromCode ? SubscriptionEventType.PlanChanged
                    : subscription.CancelAtPeriodEnd && !wasCancelling ? SubscriptionEventType.Cancelled
                    : !subscription.CancelAtPeriodEnd && wasCancelling ? SubscriptionEventType.Reactivated
                    : subscription.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired && fromStatus != subscription.Status ? SubscriptionEventType.Expired
                    : subscription.Status == SubscriptionStatus.PastDue && fromStatus != SubscriptionStatus.PastDue ? SubscriptionEventType.PaymentFailed
                    : subscription.CurrentPeriodEnd > previousEnd ? SubscriptionEventType.Renewed
                    : SubscriptionEventType.Renewed;
                AddEvent(subscription, type, fromCode, subscription.Plan.Code, evt);

                if (subscription.Status is SubscriptionStatus.Cancelled or SubscriptionStatus.Expired)
                {
                    await EnsureFreeFallbackAsync(subscription, evt, ct);
                    if (wasEntitled && providerStatus == "unpaid")
                    {
                        NotifyBilling(subscription.UserId, "Assinatura suspensa",
                            $"Não recebemos o pagamento da sua assinatura do plano {subscription.Plan.Name}. Sua conta voltou ao plano gratuito até o pagamento ser regularizado.");
                    }
                    else if (wasEntitled)
                    {
                        NotifyBilling(subscription.UserId, "Assinatura encerrada", "Sua assinatura foi encerrada e sua conta voltou ao plano gratuito.");
                    }
                }
                else if (!wasEntitled && subscription.IsEntitled)
                {
                    // Paid again (e.g. an unpaid subscription whose open invoice was settled): the free row created when
                    // it lapsed must stop being the user's current entitlement.
                    await RetireFreeFallbackAsync(subscription, ct);
                }

                if (subscription.IsEntitled && subscription.Plan.Code != fromCode)
                {
                    NotifyBilling(subscription.UserId, fromCode == SubscriptionService.FreePlanCode ? "Assinatura ativada" : "Plano alterado",
                        fromCode == SubscriptionService.FreePlanCode ? $"Seu plano {subscription.Plan.Name} está ativo. Obrigado!" : $"Seu plano agora é {subscription.Plan.Name}.");
                }
                else if (subscription.IsEntitled && subscription.CancelAtPeriodEnd && !wasCancelling)
                {
                    var until = await _notifications.FormatUserDateAsync(subscription.UserId, subscription.CurrentPeriodEnd, ct);
                    NotifyBilling(subscription.UserId, "Cancelamento agendado",
                        $"Sua assinatura do plano {subscription.Plan.Name} será encerrada em {until}. Até lá, tudo continua funcionando; depois sua conta volta ao plano gratuito.");
                }
                await _db.SaveChangesAsync(ct);
                return (PaymentEventStatus.Processed, subscription.UserId, subscription.Id);
            }

            case WebhookEventKind.SubscriptionDeleted:
            {
                // Final whatever its age: nothing comes after a deletion. An unknown subscription is ignored here, but
                // the stored event keeps a late activation of it from being bound (ProviderReportedDeletionAsync).
                var subscription = await FindByProviderAsync(evt, ct);
                if (subscription is null) return (PaymentEventStatus.Ignored, null, null);
                var fromCode = subscription.Plan.Code;
                var wasEntitledBeforeDelete = subscription.IsEntitled;
                subscription.Status = SubscriptionStatus.Cancelled;
                subscription.CancelledAt ??= now;
                subscription.CancelAtPeriodEnd = false;
                subscription.UpdatedAt = now;
                RecordSnapshotTime(subscription, evt);
                AddEvent(subscription, SubscriptionEventType.Expired, fromCode, fromCode, evt);
                await EnsureFreeFallbackAsync(subscription, evt, ct);
                if (wasEntitledBeforeDelete)
                {
                    NotifyBilling(subscription.UserId, "Assinatura encerrada", "Sua assinatura foi encerrada e sua conta voltou ao plano gratuito.");
                }
                await _db.SaveChangesAsync(ct);
                return (PaymentEventStatus.Processed, subscription.UserId, subscription.Id);
            }

            case WebhookEventKind.InvoicePaid:
            {
                // Invoice events carry no subscription snapshot: they are checked against the clock but never advance
                // it (a renewal's customer.subscription.updated is older than its invoice.paid and must still apply).
                var subscription = await FindByProviderAsync(evt, ct);
                if (subscription is null) return (PaymentEventStatus.Ignored, null, null);
                if (subscription.Status == SubscriptionStatus.Cancelled || IsStale(subscription, evt))
                {
                    LogLate(evt, subscription);
                    return (PaymentEventStatus.Ignored, subscription.UserId, subscription.Id);
                }
                var wasPastDue = subscription.Status == SubscriptionStatus.PastDue;
                if (subscription.Status is SubscriptionStatus.PastDue or SubscriptionStatus.Trialing) subscription.Status = SubscriptionStatus.Active;
                if (evt.PeriodStart is { } ps) subscription.CurrentPeriodStart = ps;
                if (evt.PeriodEnd is { } pe) subscription.CurrentPeriodEnd = pe;
                subscription.UpdatedAt = now;
                AddEvent(subscription, wasPastDue ? SubscriptionEventType.Reactivated : SubscriptionEventType.Renewed, subscription.Plan.Code, subscription.Plan.Code, evt);
                await _db.SaveChangesAsync(ct);
                return (PaymentEventStatus.Processed, subscription.UserId, subscription.Id);
            }

            case WebhookEventKind.InvoicePaymentFailed:
            {
                var subscription = await FindByProviderAsync(evt, ct);
                if (subscription is null) return (PaymentEventStatus.Ignored, null, null);
                // An older failure than the newest snapshot only still matters while the subscription is past_due (the
                // snapshot reporting it may simply have arrived first); once it is active again, or already suspended
                // or cancelled, the late failure changes nothing and is not announced.
                var stale = IsStale(subscription, evt);
                if (subscription.Status == SubscriptionStatus.Cancelled || (stale && subscription.Status != SubscriptionStatus.PastDue))
                {
                    LogLate(evt, subscription);
                    return (PaymentEventStatus.Ignored, subscription.UserId, subscription.Id);
                }
                if (!stale && subscription.Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing) subscription.Status = SubscriptionStatus.PastDue;
                subscription.UpdatedAt = now;
                AddEvent(subscription, SubscriptionEventType.PaymentFailed, subscription.Plan.Code, subscription.Plan.Code, evt);
                NotifyBilling(subscription.UserId, "Falha no pagamento", "Não conseguimos processar o pagamento da sua assinatura. Atualize sua forma de pagamento para não perder o plano.");
                await _db.SaveChangesAsync(ct);
                return (PaymentEventStatus.Processed, subscription.UserId, subscription.Id);
            }

            default:
                return (PaymentEventStatus.Ignored, evt.UserId, null);
        }
    }

    /// <summary>In-app billing notification plus its e-mail copy (sent after the commit when emailOnBilling is on).</summary>
    private void NotifyBilling(Guid userId, string title, string body)
    {
        _notifications.Add(userId, NotificationType.Billing, title, body);
        _notifications.QueueEmail(userId, EmailTopic.Billing, $"{_notifications.BrandName}: {title.ToLowerInvariant()}",
            $"{body}\n\nVeja sua assinatura e pagamentos: {_notifications.AppUrl("/billing")}");
    }

    /// <summary>
    /// checkout.session.completed of a provider subscription that already has its row (its subscription events arrived
    /// first, or this is a late delivery). A checkout completes once, when the subscription starts: when the row lapsed
    /// since (expired/cancelled) or already holds a newer provider state, the checkout changes nothing. Otherwise it only
    /// fills in what the subscription events could not tell (the plan from the checkout metadata while the row still
    /// has the free plan, and the customer id); status, cancellation and period stay what the subscription events set.
    /// </summary>
    private async Task<(PaymentEventStatus Status, Guid? UserId, Guid? SubscriptionId)> ConfirmBoundCheckoutAsync(Subscription bound, Guid userId, PaymentWebhookEvent evt, CancellationToken ct)
    {
        if (bound.UserId != userId)
        {
            _logger.LogWarning("Ignoring {Type} {EventId} of user {UserId}: provider subscription {SubscriptionId} is bound to another user",
                evt.Type, evt.ProviderEventId, userId, evt.ProviderSubscriptionId);
            return (PaymentEventStatus.Ignored, userId, null);
        }
        if (!bound.IsEntitled || IsStale(bound, evt))
        {
            LogLate(evt, bound);
            return (PaymentEventStatus.Ignored, userId, bound.Id);
        }
        var fromCode = bound.Plan.Code;
        var plan = await ResolvePlanAsync(evt, ct);
        if (plan is not null && fromCode == SubscriptionService.FreePlanCode && plan.Code != fromCode)
        {
            bound.PlanId = plan.Id;
            bound.Plan = plan;
        }
        bound.ProviderCustomerId ??= evt.CustomerId;
        if (bound.Plan.Code != fromCode)
        {
            bound.UpdatedAt = _clock.UtcNow;
            AddEvent(bound, SubscriptionEventType.PlanChanged, fromCode, bound.Plan.Code, evt);
            NotifyBilling(userId, "Assinatura ativada", $"Seu plano {bound.Plan.Name} está ativo. Obrigado!");
        }
        await _db.SaveChangesAsync(ct);
        return (PaymentEventStatus.Processed, userId, bound.Id);
    }

    /// <summary>
    /// True when a deletion of the event's provider subscription was received — whatever the outcome of processing it:
    /// the event is signed by the provider. Stripe never reactivates a canceled subscription, so a late activation of one
    /// (checkout or active snapshot) must not bind it, even when it was deleted before anything bound it to the user.
    /// </summary>
    private Task<bool> ProviderReportedDeletionAsync(PaymentWebhookEvent evt, CancellationToken ct)
        => _db.PaymentEvents.AnyAsync(e => e.Provider == evt.Provider && e.ProviderSubscriptionId == evt.ProviderSubscriptionId && e.Kind == DeletedKind, ct);

    /// <summary>
    /// True when the row already holds the state of a newer subscription snapshot than <paramref name="evt"/>. Events of
    /// the same second (the provider's resolution) are not ordered: they apply in arrival order.
    /// </summary>
    private static bool IsStale(Subscription subscription, PaymentWebhookEvent evt)
        => evt.OccurredAt is { } occurred && subscription.LastProviderEventAt is { } last && occurred < last;

    /// <summary>Advances the row's ordering clock to the subscription snapshot just applied (never backwards).</summary>
    private static void RecordSnapshotTime(Subscription subscription, PaymentWebhookEvent evt)
    {
        if (evt.OccurredAt is { } occurred && (subscription.LastProviderEventAt is not { } last || occurred > last))
        {
            subscription.LastProviderEventAt = occurred;
        }
    }

    private void LogLate(PaymentWebhookEvent evt, Subscription subscription)
        => _logger.LogInformation("Ignoring late {Type} {EventId} for subscription {SubscriptionId}: the row is {Status} with the provider state of {LastProviderEventAt} (event created {OccurredAt})",
            evt.Type, evt.ProviderEventId, subscription.ProviderSubscriptionId, subscription.Status, subscription.LastProviderEventAt, evt.OccurredAt);

    /// <summary>
    /// Serialises the processing of events about the same user (transaction-scoped advisory lock, same key as the
    /// quota checks): a deletion and a late activation of one subscription arriving together are applied one after the
    /// other, each seeing what the previous one committed, so the ordering rules never decide on a stale read. Locks are
    /// taken in a fixed order, so two webhook transactions cannot deadlock on them.
    /// </summary>
    private async Task LockEventUsersAsync(PaymentWebhookEvent evt, CancellationToken ct)
    {
        var users = new HashSet<Guid>();
        if (evt.UserId is { } named) users.Add(named);
        if (!string.IsNullOrWhiteSpace(evt.ProviderSubscriptionId))
        {
            users.UnionWith(await _db.Subscriptions.AsNoTracking().Where(s => s.ProviderSubscriptionId == evt.ProviderSubscriptionId).Select(s => s.UserId).ToListAsync(ct));
        }
        else if (!string.IsNullOrWhiteSpace(evt.CustomerId))
        {
            users.UnionWith(await _db.Subscriptions.AsNoTracking().Where(s => s.ProviderCustomerId == evt.CustomerId).Select(s => s.UserId).ToListAsync(ct));
        }
        foreach (var userId in users.Order())
        {
            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({QuotaService.AdvisoryLockKey(userId)})", ct);
        }
    }

    /// <summary>Ends the free rows that became the user's entitlement while <paramref name="paid"/> was lapsed.</summary>
    private async Task RetireFreeFallbackAsync(Subscription paid, CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var fallbacks = await _db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.UserId == paid.UserId && s.Id != paid.Id && s.Provider == SubscriptionProvider.None
                && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue))
            .ToListAsync(ct);
        foreach (var free in fallbacks)
        {
            free.Status = SubscriptionStatus.Expired;
            free.UpdatedAt = now;
            _db.SubscriptionEvents.Add(new SubscriptionEvent
            {
                SubscriptionId = free.Id,
                UserId = free.UserId,
                Type = SubscriptionEventType.Expired,
                FromPlanCode = free.Plan.Code,
                ToPlanCode = paid.Plan.Code,
                Source = EventSource.Webhook,
                Data = JsonDefaults.Serialize(new { reason = "paid_subscription_reactivated", subscriptionId = paid.Id }),
                CreatedAt = now,
            });
        }
    }

    private void AddEvent(Subscription subscription, SubscriptionEventType type, string? fromCode, string? toCode, PaymentWebhookEvent evt, string? replacedProviderSubscriptionId = null)
    {
        _db.SubscriptionEvents.Add(new SubscriptionEvent
        {
            SubscriptionId = subscription.Id,
            UserId = subscription.UserId,
            Type = type,
            FromPlanCode = fromCode,
            ToPlanCode = toCode,
            Source = EventSource.Webhook,
            Data = JsonDefaults.Serialize(new
            {
                provider = evt.Provider,
                eventId = evt.ProviderEventId,
                eventType = evt.Type,
                status = evt.SubscriptionStatus,
                periodStart = evt.PeriodStart,
                periodEnd = evt.PeriodEnd,
                cancelAtPeriodEnd = evt.CancelAtPeriodEnd,
                amountCents = evt.AmountCents,
                currency = evt.Currency,
                replacedProviderSubscriptionId,
            }),
            CreatedAt = _clock.UtcNow,
        });
    }

    /// <summary>Best effort: stops the renewal of the provider subscription that a new checkout replaced.</summary>
    private async Task RetireReplacedSubscriptionAsync(string replacedId, string newId, CancellationToken ct)
    {
        _logger.LogWarning("Checkout created provider subscription {NewId} while {ReplacedId} was active; retiring the old one at period end", newId, replacedId);
        try
        {
            await _payments.CancelAtPeriodEndAsync(replacedId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not retire replaced provider subscription {ReplacedId}; cancel it manually in the provider dashboard", replacedId);
        }
    }

    private async Task EnsureFreeFallbackAsync(Subscription ended, PaymentWebhookEvent evt, CancellationToken ct)
    {
        var hasEntitled = await _db.Subscriptions.AnyAsync(s => s.UserId == ended.UserId && s.Id != ended.Id
            && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue), ct);
        if (hasEntitled) return;
        var free = await _subscriptions.CreateFreeSubscriptionAsync(ended.UserId, ct);
        free.ProviderCustomerId = ended.ProviderCustomerId;
    }

    private Task<Subscription?> CurrentSubscriptionAsync(Guid userId, CancellationToken ct)
        => _db.Subscriptions.Include(s => s.Plan)
            .Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// Resolves the subscription row an event applies to. An event that names a provider subscription id is matched
    /// by that id only: a customer can have more than one subscription at the provider (a replaced one, a second
    /// checkout), and matching such events by customer or user id would apply the plan/period of the wrong
    /// subscription to the user's single row. The customer/user fallbacks are for events without a subscription id.
    /// </summary>
    private async Task<Subscription?> FindByProviderAsync(PaymentWebhookEvent evt, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(evt.ProviderSubscriptionId))
        {
            return await _db.Subscriptions.Include(s => s.Plan).FirstOrDefaultAsync(s => s.ProviderSubscriptionId == evt.ProviderSubscriptionId, ct);
        }
        Subscription? subscription = null;
        if (!string.IsNullOrWhiteSpace(evt.CustomerId))
        {
            subscription = await _db.Subscriptions.Include(s => s.Plan)
                .Where(s => s.ProviderCustomerId == evt.CustomerId)
                .OrderByDescending(s => s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue)
                .ThenByDescending(s => s.CreatedAt)
                .FirstOrDefaultAsync(ct);
        }
        if (subscription is null && evt.UserId is { } userId)
        {
            subscription = await CurrentSubscriptionAsync(userId, ct);
        }
        return subscription;
    }

    private async Task<Plan?> ResolvePlanAsync(PaymentWebhookEvent evt, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(evt.PriceId))
        {
            var byPrice = await _db.Plans.FirstOrDefaultAsync(p => p.ProviderPriceId == evt.PriceId, ct);
            if (byPrice is not null) return byPrice;
        }
        if (!string.IsNullOrWhiteSpace(evt.PlanCode))
        {
            return await _db.Plans.FirstOrDefaultAsync(p => p.Code == evt.PlanCode, ct);
        }
        return null;
    }

    private static SubscriptionProvider ParseProvider(string provider)
        => EnumNames.TryParse<SubscriptionProvider>(provider, out var parsed) ? parsed : SubscriptionProvider.Stripe;

    /// <summary>
    /// Provider status → entitlement status. Entitled: active, trialing and past_due (a renewal being retried — grace
    /// period). Not entitled: canceled, and unpaid / incomplete_expired / paused (no payment). "incomplete" (first
    /// invoice not paid yet) and unknown statuses map to nothing: they never grant a plan nor change a bound row.
    /// </summary>
    public static bool TryMapStatus(string providerStatus, out SubscriptionStatus status)
    {
        SubscriptionStatus? mapped = providerStatus.Trim().ToLowerInvariant() switch
        {
            "active" => SubscriptionStatus.Active,
            "trialing" => SubscriptionStatus.Trialing,
            "past_due" => SubscriptionStatus.PastDue,
            "canceled" or "cancelled" => SubscriptionStatus.Cancelled,
            "unpaid" or "incomplete_expired" or "expired" or "paused" => SubscriptionStatus.Expired,
            _ => null,
        };
        status = mapped ?? default;
        return mapped is not null;
    }
}
