using ClipForge.Domain.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;

using Stripe.Checkout;

namespace ClipForge.Infrastructure.Payments;

/// <summary>
/// Stripe implementation: Checkout Sessions in subscription mode, Billing Portal, cancel at period end and
/// webhook verification with <see cref="EventUtility.ConstructEvent(string, string, string, long, bool)"/>.
/// Entitlements are only changed by webhooks (docs/PLANS_AND_BILLING.md).
/// </summary>
public sealed class StripePaymentProvider : IPaymentProvider
{
    private readonly StripeOptions _options;
    private readonly StripeClient? _client;
    private readonly ILogger<StripePaymentProvider> _logger;

    public StripePaymentProvider(IOptions<PaymentsOptions> options, ILogger<StripePaymentProvider> logger)
    {
        _options = options.Value.Stripe;
        _logger = logger;
        if (!string.IsNullOrWhiteSpace(_options.SecretKey))
        {
            _client = new StripeClient(_options.SecretKey);
        }
    }

    public string Name => "stripe";

    public bool IsConfigured => _client is not null;

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default)
    {
        var client = RequireClient();
        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            SuccessUrl = request.SuccessUrl,
            CancelUrl = request.CancelUrl,
            ClientReferenceId = request.UserId.ToString("D"),
            LineItems = [new Stripe.Checkout.SessionLineItemOptions { Price = request.PriceId, Quantity = 1 }],
            Metadata = new Dictionary<string, string>
            {
                ["userId"] = request.UserId.ToString("D"),
                ["planCode"] = request.PlanCode,
            },
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
            {
                Metadata = new Dictionary<string, string>
                {
                    ["userId"] = request.UserId.ToString("D"),
                    ["planCode"] = request.PlanCode,
                },
            },
            AllowPromotionCodes = true,
        };
        if (!string.IsNullOrWhiteSpace(request.ExistingCustomerId)) options.Customer = request.ExistingCustomerId;
        else options.CustomerEmail = request.Email;

        var session = await new Stripe.Checkout.SessionService(client).CreateAsync(options, cancellationToken: ct);
        return new CheckoutSessionResult(session.Url, session.Id);
    }

    public async Task<PortalSessionResult> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default)
    {
        var client = RequireClient();
        var session = await new Stripe.BillingPortal.SessionService(client).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl,
        }, cancellationToken: ct);
        return new PortalSessionResult(session.Url);
    }

    public async Task CancelAtPeriodEndAsync(string providerSubscriptionId, CancellationToken ct = default)
    {
        var client = RequireClient();
        await new SubscriptionService(client).UpdateAsync(providerSubscriptionId, new SubscriptionUpdateOptions
        {
            CancelAtPeriodEnd = true,
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Upgrade/downgrade in place: the single subscription item is moved to the new price with prorations, the
    /// pending cancellation (if any) is lifted and the plan code metadata is refreshed so webhooks resolve the plan.
    /// Stripe then emits customer.subscription.updated, which is where the entitlement changes.
    /// </summary>
    public async Task ChangePlanAsync(string providerSubscriptionId, string priceId, string planCode, CancellationToken ct = default)
    {
        var client = RequireClient();
        var service = new SubscriptionService(client);
        try
        {
            var subscription = await service.GetAsync(providerSubscriptionId, cancellationToken: ct);
            var item = subscription.Items?.Data?.FirstOrDefault()
                ?? throw new AppException(ErrorCodes.PaymentProviderError, 502, "A assinatura no provedor não possui itens.");
            await service.UpdateAsync(providerSubscriptionId, new SubscriptionUpdateOptions
            {
                Items = [new SubscriptionItemOptions { Id = item.Id, Price = priceId }],
                ProrationBehavior = "create_prorations",
                CancelAtPeriodEnd = false,
                Metadata = new Dictionary<string, string> { ["planCode"] = planCode },
            }, cancellationToken: ct);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning("Stripe refused the plan change of {SubscriptionId}: {Message}", providerSubscriptionId, ex.Message);
            throw new AppException(ErrorCodes.PaymentProviderError, 502, "O provedor de pagamentos recusou a alteração do plano. Tente novamente mais tarde.");
        }
    }

    public PaymentWebhookEvent ParseWebhook(string rawBody, string? signatureHeader)
    {
        if (string.IsNullOrWhiteSpace(_options.WebhookSecret))
        {
            throw new AppException(ErrorCodes.PaymentsNotConfigured, 409, "Stripe webhook secret não configurado.");
        }
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(rawBody, signatureHeader ?? string.Empty, _options.WebhookSecret, throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            _logger.LogWarning("Stripe webhook signature verification failed: {Message}", ex.Message);
            throw new AppException("invalid_signature", 400, "Assinatura do webhook inválida.");
        }

        return Map(stripeEvent, rawBody);
    }

    private static PaymentWebhookEvent Map(Event e, string rawBody)
    {
        switch (e.Type)
        {
            case "checkout.session.completed" when e.Data.Object is Stripe.Checkout.Session session:
                return new PaymentWebhookEvent
                {
                    Provider = "stripe",
                    ProviderEventId = e.Id,
                    Type = e.Type,
                    RawPayload = rawBody,
                    OccurredAt = EventTime(e.Created),
                    Kind = WebhookEventKind.CheckoutCompleted,
                    CustomerId = session.CustomerId,
                    ProviderSubscriptionId = session.SubscriptionId,
                    PlanCode = session.Metadata is not null && session.Metadata.TryGetValue("planCode", out var pc) ? pc : null,
                    UserId = ParseUserId(session.ClientReferenceId) ?? ParseUserId(session.Metadata is not null && session.Metadata.TryGetValue("userId", out var uid) ? uid : null),
                    AmountCents = session.AmountTotal,
                    Currency = session.Currency,
                    PaymentStatus = session.PaymentStatus,
                };

            case "customer.subscription.created" or "customer.subscription.updated" or "customer.subscription.deleted" when e.Data.Object is Subscription sub:
            {
                var item = sub.Items?.Data?.FirstOrDefault();
                return new PaymentWebhookEvent
                {
                    Provider = "stripe",
                    ProviderEventId = e.Id,
                    Type = e.Type,
                    RawPayload = rawBody,
                    OccurredAt = EventTime(e.Created),
                    Kind = e.Type switch
                    {
                        "customer.subscription.created" => WebhookEventKind.SubscriptionCreated,
                        "customer.subscription.updated" => WebhookEventKind.SubscriptionUpdated,
                        _ => WebhookEventKind.SubscriptionDeleted,
                    },
                    CustomerId = sub.CustomerId,
                    ProviderSubscriptionId = sub.Id,
                    PriceId = item?.Price?.Id,
                    PlanCode = sub.Metadata is not null && sub.Metadata.TryGetValue("planCode", out var pc2) ? pc2 : null,
                    UserId = ParseUserId(sub.Metadata is not null && sub.Metadata.TryGetValue("userId", out var uid2) ? uid2 : null),
                    SubscriptionStatus = sub.Status,
                    PeriodStart = ToOffset(item?.CurrentPeriodStart),
                    PeriodEnd = ToOffset(item?.CurrentPeriodEnd),
                    CancelAtPeriodEnd = sub.CancelAtPeriodEnd,
                };
            }

            case "invoice.paid" or "invoice.payment_failed" when e.Data.Object is Invoice invoice:
                return new PaymentWebhookEvent
                {
                    Provider = "stripe",
                    ProviderEventId = e.Id,
                    Type = e.Type,
                    RawPayload = rawBody,
                    OccurredAt = EventTime(e.Created),
                    Kind = e.Type == "invoice.paid" ? WebhookEventKind.InvoicePaid : WebhookEventKind.InvoicePaymentFailed,
                    CustomerId = invoice.CustomerId,
                    ProviderSubscriptionId = invoice.Parent?.SubscriptionDetails?.SubscriptionId,
                    AmountCents = invoice.AmountPaid,
                    Currency = invoice.Currency,
                };

            default:
                return new PaymentWebhookEvent
                {
                    Provider = "stripe",
                    ProviderEventId = e.Id,
                    Type = e.Type,
                    RawPayload = rawBody,
                    OccurredAt = EventTime(e.Created),
                    Kind = WebhookEventKind.Unknown,
                };
        }
    }

    private static Guid? ParseUserId(string? value) => Guid.TryParse(value, out var id) ? id : null;

    private static DateTimeOffset? ToOffset(DateTime? value)
        => value is { } v ? new DateTimeOffset(DateTime.SpecifyKind(v, DateTimeKind.Utc)) : null;

    /// <summary>Stripe <c>Event.Created</c> (UTC, whole seconds); null when the event carries none.</summary>
    private static DateTimeOffset? EventTime(DateTime? created)
        => created is { } c && c != default ? ToOffset(c) : null;

    private StripeClient RequireClient() => _client ?? throw NonePaymentProvider.NotConfigured();
}
