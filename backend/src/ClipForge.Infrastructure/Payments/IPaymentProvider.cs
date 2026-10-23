using ClipForge.Domain.Common;

namespace ClipForge.Infrastructure.Payments;

public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>None | Stripe</summary>
    public string Provider { get; set; } = "None";
    public StripeOptions Stripe { get; set; } = new();

    public bool IsStripe => string.Equals(Provider, "Stripe", StringComparison.OrdinalIgnoreCase);
}

public sealed class StripeOptions
{
    public string? SecretKey { get; set; }
    public string? WebhookSecret { get; set; }
}

public sealed record CheckoutSessionRequest(
    Guid UserId,
    string Email,
    string? ExistingCustomerId,
    string PlanCode,
    string PriceId,
    string SuccessUrl,
    string CancelUrl);

public sealed record CheckoutSessionResult(string Url, string? SessionId);

public sealed record PortalSessionResult(string Url);

/// <summary>Provider-neutral webhook event kinds the billing service understands.</summary>
public enum WebhookEventKind
{
    Unknown,
    CheckoutCompleted,
    SubscriptionCreated,
    SubscriptionUpdated,
    SubscriptionDeleted,
    InvoicePaid,
    InvoicePaymentFailed,
}

/// <summary>Normalised view of a provider webhook event.</summary>
public sealed class PaymentWebhookEvent
{
    public required string Provider { get; init; }
    public required string ProviderEventId { get; init; }
    public required string Type { get; init; }
    public required string RawPayload { get; init; }
    public WebhookEventKind Kind { get; init; } = WebhookEventKind.Unknown;
    /// <summary>
    /// When the provider created the event (Stripe <c>Event.Created</c>, whole seconds). Providers do not deliver events
    /// in order and redeliver failed ones later: this is what tells an old subscription snapshot from a newer one.
    /// </summary>
    public DateTimeOffset? OccurredAt { get; init; }

    public string? CustomerId { get; init; }
    public string? ProviderSubscriptionId { get; init; }
    public string? PriceId { get; init; }
    /// <summary>Plan code passed through metadata when the checkout was created.</summary>
    public string? PlanCode { get; init; }
    /// <summary>User id passed as client reference / metadata when the checkout was created.</summary>
    public Guid? UserId { get; init; }
    /// <summary>Provider subscription status (e.g. active, trialing, past_due, canceled, unpaid, incomplete).</summary>
    public string? SubscriptionStatus { get; init; }
    /// <summary>
    /// Payment state of a completed checkout (Stripe <c>payment_status</c>: paid, unpaid, no_payment_required). Only
    /// paid / no_payment_required grant the plan; unpaid (asynchronous methods such as boleto/PIX) waits for the
    /// subscription to become active.
    /// </summary>
    public string? PaymentStatus { get; init; }
    public DateTimeOffset? PeriodStart { get; init; }
    public DateTimeOffset? PeriodEnd { get; init; }
    public bool? CancelAtPeriodEnd { get; init; }
    public long? AmountCents { get; init; }
    public string? Currency { get; init; }
}

public interface IPaymentProvider
{
    /// <summary>none | stripe</summary>
    string Name { get; }

    /// <summary>False when credentials are missing: billing endpoints answer 409 payments_not_configured.</summary>
    bool IsConfigured { get; }

    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default);

    Task<PortalSessionResult> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default);

    Task CancelAtPeriodEndAsync(string providerSubscriptionId, CancellationToken ct = default);

    /// <summary>
    /// Switches an existing provider subscription to another price in place (prorated by the provider) instead of
    /// creating a second subscription for the same customer. The entitlement only changes when the provider's
    /// subscription-updated webhook arrives. Throws <see cref="AppException"/> (502 payment_provider_error) when refused.
    /// </summary>
    Task ChangePlanAsync(string providerSubscriptionId, string priceId, string planCode, CancellationToken ct = default);

    /// <summary>Verifies the signature and parses the event. Throws <see cref="AppException"/> (400) when invalid.</summary>
    PaymentWebhookEvent ParseWebhook(string rawBody, string? signatureHeader);
}

/// <summary>No provider configured: everything answers payments_not_configured.</summary>
public sealed class NonePaymentProvider : IPaymentProvider
{
    public string Name => "none";
    public bool IsConfigured => false;

    public Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default)
        => throw NotConfigured();

    public Task<PortalSessionResult> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default)
        => throw NotConfigured();

    public Task CancelAtPeriodEndAsync(string providerSubscriptionId, CancellationToken ct = default)
        => throw NotConfigured();

    public Task ChangePlanAsync(string providerSubscriptionId, string priceId, string planCode, CancellationToken ct = default)
        => throw NotConfigured();

    public PaymentWebhookEvent ParseWebhook(string rawBody, string? signatureHeader)
        => throw NotConfigured();

    public static AppException NotConfigured()
        => new(ErrorCodes.PaymentsNotConfigured, 409, "Pagamentos ainda não configurados.");
}
