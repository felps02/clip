using ClipForge.Api.Common;
using ClipForge.Api.Features.Auth;
using ClipForge.Api.Options;
using ClipForge.Api.Services;
using ClipForge.Domain.Common;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using ClipForge.Domain.ValueObjects;
using ClipForge.Infrastructure.Payments;
using ClipForge.Infrastructure.Persistence;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClipForge.Api.Features.Billing;

public sealed record CheckoutRequest(string PlanCode, string? SuccessUrl, string? CancelUrl);

/// <summary>
/// Where to send the user next. <c>PlanChanged</c> is true when an active paid subscription was switched to the new
/// plan in place (no new checkout): <c>CheckoutUrl</c> is then the success page and the entitlement changes when the
/// provider's subscription-updated webhook arrives.
/// </summary>
public sealed record CheckoutResponse(string CheckoutUrl, bool PlanChanged = false);

public sealed record PortalResponse(string Url);

public sealed record BillingPlanDto(string Code, string Name, int? PriceMonthlyCents, string Currency, PlanLimits Limits, IReadOnlyList<string> Features);

public sealed record BillingSubscriptionDto(
    Guid? Id,
    string PlanCode,
    SubscriptionStatus Status,
    SubscriptionProvider Provider,
    DateTimeOffset CurrentPeriodStart,
    DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? CancelledAt);

public sealed record BillingSubscriptionResponse(BillingSubscriptionDto Subscription, BillingPlanDto Plan, bool PaymentsConfigured, bool PortalAvailable);

public sealed record PaymentEventDto(Guid Id, string Type, long? AmountCents, string? Currency, PaymentEventStatus Status, DateTimeOffset CreatedAt);

public sealed class CheckoutRequestValidator : AbstractValidator<CheckoutRequest>
{
    public CheckoutRequestValidator()
    {
        RuleFor(x => x.PlanCode).NotEmpty().WithMessage("Informe o plano.").MaximumLength(40);
        RuleFor(x => x.SuccessUrl).Must(BeHttpUrl).WithMessage("URL inválida.").When(x => x.SuccessUrl is not null);
        RuleFor(x => x.CancelUrl).Must(BeHttpUrl).WithMessage("URL inválida.").When(x => x.CancelUrl is not null);
    }

    private static bool BeHttpUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
}

public static class BillingEndpoints
{
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/billing").WithTags("Billing").WithValidation().RequireAuthorization();

        group.MapGet("/subscription", async (HttpContext http, SubscriptionService subscriptions, IPaymentProvider payments, CancellationToken ct) =>
        {
            var entitlement = await subscriptions.GetEntitlementAsync(http.User.GetUserId(), ct);
            var sub = entitlement.Subscription;
            var plan = entitlement.Plan;
            var response = new BillingSubscriptionResponse(
                new BillingSubscriptionDto(sub?.Id, plan.Code, sub?.Status ?? SubscriptionStatus.Active, sub?.Provider ?? SubscriptionProvider.None,
                    entitlement.PeriodStart, entitlement.PeriodEnd, sub?.CancelAtPeriodEnd ?? false, sub?.CancelledAt),
                new BillingPlanDto(plan.Code, plan.Name, plan.PriceMonthlyCents, plan.Currency, plan.Limits, plan.Features),
                payments.IsConfigured,
                payments.IsConfigured && !string.IsNullOrWhiteSpace(sub?.ProviderCustomerId));
            return Results.Ok(response);
        }).Produces<BillingSubscriptionResponse>();

        group.MapPost("/checkout", async (CheckoutRequest request, HttpContext http, AppDbContext db, UserManager<AppUser> users, SubscriptionService subscriptions, IPaymentProvider payments, IOptions<FrontendOptions> frontend, CancellationToken ct) =>
        {
            if (!payments.IsConfigured) throw NonePaymentProvider.NotConfigured();
            var userId = http.User.GetUserId();
            var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized();
            var plan = await db.Plans.FirstOrDefaultAsync(p => p.Code == request.PlanCode && p.IsActive, ct) ?? throw AppException.NotFound("Plano não encontrado.");
            if (plan.IsFree)
            {
                // Free plans never go to the payment provider: going back to free is a cancellation (POST /cancel).
                throw AppException.Conflict("Este plano é gratuito e não precisa de pagamento.");
            }
            if (!plan.IsPurchasable)
            {
                throw AppException.Conflict("Este plano não está disponível para compra.");
            }
            var entitlement = await subscriptions.GetEntitlementAsync(userId, ct);
            var current = entitlement.Subscription;
            if (entitlement.Plan.Code == plan.Code && current?.Provider != SubscriptionProvider.None)
            {
                throw AppException.Conflict("Você já assina este plano.");
            }
            var baseUrl = frontend.Value.BaseUrl.TrimEnd('/');
            var successUrl = request.SuccessUrl ?? $"{baseUrl}/billing?checkout=success";

            if (current is { Provider: not SubscriptionProvider.None, ProviderSubscriptionId.Length: > 0 })
            {
                // Upgrade/downgrade of an active paid subscription: switched in place at the provider (prorated)
                // instead of buying a second subscription for the same customer. Entitlements still only change
                // through the provider's customer.subscription.updated webhook.
                await payments.ChangePlanAsync(current.ProviderSubscriptionId, plan.ProviderPriceId!, plan.Code, ct);
                return Results.Ok(new CheckoutResponse(successUrl, PlanChanged: true));
            }

            var session = await payments.CreateCheckoutSessionAsync(new CheckoutSessionRequest(
                userId, user.Email!, current?.ProviderCustomerId, plan.Code, plan.ProviderPriceId!,
                successUrl,
                request.CancelUrl ?? $"{baseUrl}/billing?checkout=cancelled"), ct);
            return Results.Ok(new CheckoutResponse(session.Url));
        }).Produces<CheckoutResponse>();

        group.MapPost("/portal", async (HttpContext http, SubscriptionService subscriptions, IPaymentProvider payments, IOptions<FrontendOptions> frontend, CancellationToken ct) =>
        {
            if (!payments.IsConfigured) throw NonePaymentProvider.NotConfigured();
            var entitlement = await subscriptions.GetEntitlementAsync(http.User.GetUserId(), ct);
            var customerId = entitlement.Subscription?.ProviderCustomerId;
            if (string.IsNullOrWhiteSpace(customerId)) throw AppException.Conflict("Nenhuma assinatura paga encontrada.");
            var portal = await payments.CreatePortalSessionAsync(customerId, $"{frontend.Value.BaseUrl.TrimEnd('/')}/billing", ct);
            return Results.Ok(new PortalResponse(portal.Url));
        }).Produces<PortalResponse>();

        group.MapPost("/cancel", async (HttpContext http, AppDbContext db, SubscriptionService subscriptions, IPaymentProvider payments, IClock clock, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var entitlement = await subscriptions.GetEntitlementAsync(userId, ct);
            var sub = entitlement.Subscription;
            if (sub is null || sub.Provider == SubscriptionProvider.None || string.IsNullOrWhiteSpace(sub.ProviderSubscriptionId))
            {
                throw AppException.Conflict("Nenhuma assinatura paga para cancelar.");
            }
            if (!payments.IsConfigured) throw NonePaymentProvider.NotConfigured();
            await payments.CancelAtPeriodEndAsync(sub.ProviderSubscriptionId, ct);
            var now = clock.UtcNow;
            sub.CancelAtPeriodEnd = true;
            sub.CancelledAt ??= now;
            sub.UpdatedAt = now;
            db.SubscriptionEvents.Add(new SubscriptionEvent
            {
                SubscriptionId = sub.Id,
                UserId = userId,
                Type = SubscriptionEventType.Cancelled,
                FromPlanCode = entitlement.Plan.Code,
                ToPlanCode = entitlement.Plan.Code,
                Source = EventSource.User,
                Data = JsonDefaults.Serialize(new { cancelAtPeriodEnd = true, periodEnd = sub.CurrentPeriodEnd }),
                CreatedAt = now,
            });
            await db.SaveChangesAsync(ct);
            return Results.Accepted(value: new MessageResponse("Assinatura será cancelada ao fim do período atual."));
        });

        group.MapGet("/payments", async (HttpContext http, AppDbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var userId = http.User.GetUserId();
            var (p, ps) = Paging.Normalize(page, pageSize);
            var query = db.PaymentEvents.Where(e => e.UserId == userId);
            var total = await query.CountAsync(ct);
            var events = await query.OrderByDescending(e => e.ReceivedAt).Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            var items = events.Select(e =>
            {
                var (amount, currency) = ReadAmount(e.Payload);
                return new PaymentEventDto(e.Id, e.Type, amount, currency, e.Status, e.ReceivedAt);
            }).ToList();
            return Results.Ok(new PagedResult<PaymentEventDto>(items, p, ps, total));
        }).Produces<PagedResult<PaymentEventDto>>();

        return app;
    }

    private static (long? Amount, string? Currency) ReadAmount(string payload)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var obj))
            {
                long? amount = null;
                foreach (var name in new[] { "amount_paid", "amount_total", "amount_due", "amount" })
                {
                    if (obj.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        amount = value.GetInt64();
                        break;
                    }
                }
                string? currency = obj.TryGetProperty("currency", out var cur) && cur.ValueKind == System.Text.Json.JsonValueKind.String ? cur.GetString() : null;
                return (amount, currency);
            }
            // Normalised (fake provider) payloads
            long? a = root.TryGetProperty("amountCents", out var ac) && ac.ValueKind == System.Text.Json.JsonValueKind.Number ? ac.GetInt64() : null;
            string? c = root.TryGetProperty("currency", out var cc) && cc.ValueKind == System.Text.Json.JsonValueKind.String ? cc.GetString() : null;
            return (a, c);
        }
        catch (System.Text.Json.JsonException)
        {
            return (null, null);
        }
    }
}
