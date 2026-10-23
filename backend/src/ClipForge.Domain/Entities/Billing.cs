using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Domain.Entities;

public class Plan
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public int? PriceMonthlyCents { get; set; }
    public string Currency { get; set; } = "BRL";
    public string? ProviderPriceId { get; set; }
    public PlanLimits Limits { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>A plan priced at 0 is free: shown as "Grátis", never sold and never sent to the payment provider.</summary>
    public bool IsFree => PriceMonthlyCents == 0;

    /// <summary>Can be bought through checkout: a positive price and a provider price id.</summary>
    public bool IsPurchasable => PriceMonthlyCents is > 0 && !string.IsNullOrWhiteSpace(ProviderPriceId);
}

public class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid UserId { get; set; }
    public Guid PlanId { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public SubscriptionProvider Provider { get; set; } = SubscriptionProvider.None;
    public string? ProviderCustomerId { get; set; }
    public string? ProviderSubscriptionId { get; set; }
    public DateTimeOffset CurrentPeriodStart { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    /// <summary>
    /// Provider time (Stripe <c>Event.Created</c>) of the newest subscription snapshot event
    /// (customer.subscription.created/updated/deleted) applied to the row. The provider does not deliver events in order
    /// and redelivers failed ones later, so an older event never overwrites the state of a newer one. Null until a dated
    /// snapshot is applied; reset when the row is bound to another provider subscription.
    /// </summary>
    public DateTimeOffset? LastProviderEventAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Plan Plan { get; set; } = null!;
    public List<SubscriptionEvent> Events { get; set; } = new();

    public bool IsEntitled => Status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue;
}

public class SubscriptionEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid SubscriptionId { get; set; }
    public Guid UserId { get; set; }
    public SubscriptionEventType Type { get; set; }
    public string? FromPlanCode { get; set; }
    public string? ToPlanCode { get; set; }
    public EventSource Source { get; set; } = EventSource.System;
    /// <summary>Raw JSON document.</summary>
    public string? Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class UsageRecord
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>Nullable so records can be anonymised when the account is purged.</summary>
    public Guid? UserId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public UsageKind Kind { get; set; }
    public decimal Quantity { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public Guid? JobId { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? ClipId { get; set; }
    /// <summary>Raw JSON document.</summary>
    public string? Metadata { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class PaymentEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Provider { get; set; } = string.Empty;
    public string ProviderEventId { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    /// <summary>
    /// Provider-neutral kind of the event (snake_case of the webhook kind: checkout_completed, subscription_deleted, …,
    /// unknown). Null for events stored before it was recorded.
    /// </summary>
    public string? Kind { get; set; }
    /// <summary>Provider subscription the event is about (null when it names none).</summary>
    public string? ProviderSubscriptionId { get; set; }
    /// <summary>When the provider created the event (Stripe <c>Event.Created</c>); null when unknown.</summary>
    public DateTimeOffset? OccurredAt { get; set; }
    /// <summary>Raw JSON payload as received.</summary>
    public string Payload { get; set; } = "{}";
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public PaymentEventStatus Status { get; set; } = PaymentEventStatus.Received;
    public string? Error { get; set; }
    public Guid? UserId { get; set; }
    public Guid? SubscriptionId { get; set; }
}
