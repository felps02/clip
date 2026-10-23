using ClipForge.Api.Common;
using ClipForge.Api.Services;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Features.Plans;

public sealed record PlanDto(string Code, string Name, string? Description, int? PriceMonthlyCents, string Currency, PlanLimits Limits, IReadOnlyList<string> Features, bool? IsCurrent, bool Purchasable);

public sealed record PlansResponse(IReadOnlyList<PlanDto> Items);

public static class PlansEndpoints
{
    public static IEndpointRouteBuilder MapPlansEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/plans", async (HttpContext http, AppDbContext db, SubscriptionService subscriptions, CancellationToken ct) =>
        {
            var plans = await db.Plans.Where(p => p.IsActive).OrderBy(p => p.SortOrder).ThenBy(p => p.Code).ToListAsync(ct);
            string? currentCode = null;
            if (http.User.Identity?.IsAuthenticated == true && http.User.TryGetUserId() is { } userId)
            {
                currentCode = (await subscriptions.GetEntitlementAsync(userId, ct)).Plan.Code;
            }
            var items = plans.Select(p => new PlanDto(p.Code, p.Name, p.Description, p.PriceMonthlyCents, p.Currency, p.Limits, p.Features,
                currentCode is null ? null : p.Code == currentCode,
                // A free plan (price 0) is never purchasable, even with a provider price id configured.
                p.IsPurchasable)).ToList();
            return Results.Ok(new PlansResponse(items));
        }).WithTags("Plans").AllowAnonymous().Produces<PlansResponse>();

        return app;
    }
}
