using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Services;

/// <summary>The user's current entitlement: subscription + plan + limits + billing period.</summary>
public sealed record Entitlement(Subscription? Subscription, Plan Plan, PlanLimits Limits, DateTimeOffset PeriodStart, DateTimeOffset PeriodEnd)
{
    public Guid? SubscriptionId => Subscription?.Id;
}

public sealed class SubscriptionService
{
    public const string FreePlanCode = "free";

    private readonly AppDbContext _db;
    private readonly IClock _clock;

    public SubscriptionService(AppDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<Plan> GetFreePlanAsync(CancellationToken ct = default)
    {
        var plan = await _db.Plans.FirstOrDefaultAsync(p => p.Code == FreePlanCode, ct);
        return plan ?? throw new AppException(ErrorCodes.InternalError, 500, "Plano gratuito não configurado.");
    }

    public Task<Plan?> GetPlanByCodeAsync(string code, CancellationToken ct = default)
        => _db.Plans.FirstOrDefaultAsync(p => p.Code == code, ct);

    /// <summary>Creates the free subscription of a new user (rolling monthly period from creation).</summary>
    public async Task<Subscription> CreateFreeSubscriptionAsync(Guid userId, CancellationToken ct = default)
    {
        var plan = await GetFreePlanAsync(ct);
        var now = _clock.UtcNow;
        var subscription = new Subscription
        {
            UserId = userId,
            PlanId = plan.Id,
            Plan = plan,
            Status = SubscriptionStatus.Active,
            Provider = SubscriptionProvider.None,
            CurrentPeriodStart = now,
            CurrentPeriodEnd = now.AddMonths(1),
            CreatedAt = now,
            UpdatedAt = now,
        };
        subscription.Events.Add(new SubscriptionEvent
        {
            SubscriptionId = subscription.Id,
            UserId = userId,
            Type = SubscriptionEventType.Created,
            ToPlanCode = plan.Code,
            Source = EventSource.System,
            CreatedAt = now,
        });
        _db.Subscriptions.Add(subscription);
        return subscription;
    }

    /// <summary>Returns the entitled subscription (active|trialing|past_due) with its plan, rolling the free period when needed.</summary>
    public async Task<Entitlement> GetEntitlementAsync(Guid userId, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var subscription = await _db.Subscriptions
            .Include(s => s.Plan)
            .Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue))
            .OrderByDescending(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (subscription is null)
        {
            var free = await GetFreePlanAsync(ct);
            return new Entitlement(null, free, free.Limits, now, now.AddMonths(1));
        }

        if (subscription.Provider == SubscriptionProvider.None && subscription.CurrentPeriodEnd <= now)
        {
            // Free plan: the period rolls monthly from the subscription creation date.
            var start = subscription.CurrentPeriodStart;
            var end = subscription.CurrentPeriodEnd;
            while (end <= now)
            {
                start = end;
                end = end.AddMonths(1);
            }
            subscription.CurrentPeriodStart = start;
            subscription.CurrentPeriodEnd = end;
            subscription.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);
        }

        return new Entitlement(subscription, subscription.Plan, subscription.Plan.Limits, subscription.CurrentPeriodStart, subscription.CurrentPeriodEnd);
    }
}
