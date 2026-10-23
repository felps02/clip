using System.Text.Json;
using ClipForge.Domain.Common;
using ClipForge.Infrastructure.Payments;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>
/// Test-only payment provider. Webhook bodies are JSON documents with the normalised event fields; the signature
/// header must equal <see cref="Secret"/>. Checkout/portal are only available when <see cref="Configured"/> is true.
/// </summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    public const string Secret = "fake-signature";

    public bool Configured { get; set; }
    public List<CheckoutSessionRequest> CheckoutRequests { get; } = new();
    public List<string> CancelledSubscriptions { get; } = new();
    public List<(string SubscriptionId, string PriceId, string PlanCode)> PlanChanges { get; } = new();

    public string Name => "stripe";

    public bool IsConfigured => Configured;

    public void Reset()
    {
        Configured = false;
        CheckoutRequests.Clear();
        CancelledSubscriptions.Clear();
        PlanChanges.Clear();
    }

    public Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CheckoutSessionRequest request, CancellationToken ct = default)
    {
        if (!Configured) throw NonePaymentProvider.NotConfigured();
        CheckoutRequests.Add(request);
        return Task.FromResult(new CheckoutSessionResult("https://checkout.example.test/session/" + Guid.NewGuid().ToString("N"), "cs_test"));
    }

    public Task<PortalSessionResult> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default)
    {
        if (!Configured) throw NonePaymentProvider.NotConfigured();
        return Task.FromResult(new PortalSessionResult("https://portal.example.test/" + customerId));
    }

    public Task CancelAtPeriodEndAsync(string providerSubscriptionId, CancellationToken ct = default)
    {
        if (!Configured) throw NonePaymentProvider.NotConfigured();
        CancelledSubscriptions.Add(providerSubscriptionId);
        return Task.CompletedTask;
    }

    public Task ChangePlanAsync(string providerSubscriptionId, string priceId, string planCode, CancellationToken ct = default)
    {
        if (!Configured) throw NonePaymentProvider.NotConfigured();
        PlanChanges.Add((providerSubscriptionId, priceId, planCode));
        return Task.CompletedTask;
    }

    public PaymentWebhookEvent ParseWebhook(string rawBody, string? signatureHeader)
    {
        if (signatureHeader != Secret) throw new AppException("invalid_signature", 400, "Assinatura do webhook inválida.");
        var doc = JsonSerializer.Deserialize<FakeWebhookBody>(rawBody, JsonDefaults.Options)
            ?? throw new AppException(ErrorCodes.ValidationFailed, 400, "Corpo inválido.");
        return new PaymentWebhookEvent
        {
            Provider = "stripe",
            ProviderEventId = doc.Id,
            Type = doc.Type,
            RawPayload = rawBody,
            Kind = doc.Kind,
            OccurredAt = doc.OccurredAt,
            CustomerId = doc.CustomerId,
            ProviderSubscriptionId = doc.SubscriptionId,
            PriceId = doc.PriceId,
            PlanCode = doc.PlanCode,
            UserId = doc.UserId,
            SubscriptionStatus = doc.Status,
            PeriodStart = doc.PeriodStart,
            PeriodEnd = doc.PeriodEnd,
            CancelAtPeriodEnd = doc.CancelAtPeriodEnd,
            AmountCents = doc.AmountCents,
            Currency = doc.Currency,
            PaymentStatus = doc.PaymentStatus,
        };
    }
}

public sealed class FakeWebhookBody
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public WebhookEventKind Kind { get; set; }
    /// <summary>Provider creation time of the event (Stripe Event.Created); omitted = unknown.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
    public string? CustomerId { get; set; }
    public string? SubscriptionId { get; set; }
    public string? PriceId { get; set; }
    public string? PlanCode { get; set; }
    public Guid? UserId { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? PeriodStart { get; set; }
    public DateTimeOffset? PeriodEnd { get; set; }
    public bool? CancelAtPeriodEnd { get; set; }
    public long? AmountCents { get; set; }
    public string? Currency { get; set; }
    /// <summary>Checkout payment_status (paid, unpaid, no_payment_required).</summary>
    public string? PaymentStatus { get; set; }
}
