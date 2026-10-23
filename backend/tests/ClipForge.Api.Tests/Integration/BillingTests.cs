using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace ClipForge.Api.Tests.Integration;

public class BillingTests : TestBase
{
    public BillingTests(ApiFactory factory) : base(factory)
    {
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(object body, string? signature = FakePaymentProvider.Secret)
    {
        var client = CreateClient();
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = content };
        if (signature is not null) request.Headers.Add("Stripe-Signature", signature);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Plans_are_seeded_from_configuration()
    {
        var plans = await WithDbAsync(db => db.Plans.OrderBy(p => p.SortOrder).ToListAsync());
        plans.Select(p => p.Code).ShouldBe(["free", "creator", "pro"]);
        var creator = plans.Single(p => p.Code == "creator");
        creator.Limits.MonthlyProcessingMinutes.ShouldBe(600);
        creator.Limits.MaxActiveProjects.ShouldBe(20);
        creator.Limits.AllowWebcamLayout.ShouldBeTrue();
        creator.Limits.TranscriptionModel.ShouldBe("small");
        creator.PriceMonthlyCents.ShouldBeNull();
        plans.Single(p => p.Code == "pro").Limits.MaxRendersPerMonth.ShouldBe(0);
        plans.Single(p => p.Code == "pro").Limits.TranscriptionModel.ShouldBe("small");
        var free = plans.Single(p => p.Code == "free");
        free.PriceMonthlyCents.ShouldBe(0);
        free.Limits.TranscriptionModel.ShouldBe("base");
        plans.Select(p => p.Limits.MaxAutoRenderPerJob).ShouldBe([15, 30, 50]);
        plans.Select(p => p.Limits.MaxCandidatesPerJob).ShouldBe([15, 30, 50]);
        plans.Select(p => p.Limits.AssistantMessagesPerMonth).ShouldBe([100, 1000, 5000]);

        var response = await ReadJsonAsync(await CreateClient().GetAsync("/api/plans"));
        var items = response.GetProperty("items").EnumerateArray().ToList();
        items.Count.ShouldBe(3);
        items[0].GetProperty("code").GetString().ShouldBe("free");
        items[0].GetProperty("limits").GetProperty("monthlyProcessingMinutes").GetInt32().ShouldBe(60);
        items[0].GetProperty("limits").GetProperty("assistantMessagesPerMonth").GetInt32().ShouldBe(100);
        items[0].GetProperty("priceMonthlyCents").GetInt32().ShouldBe(0);
        items[0].GetProperty("purchasable").GetBoolean().ShouldBeFalse();
        items[0].GetProperty("isCurrent").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);

        var (client, _, _) = await RegisterAsync();
        var mine = await ReadJsonAsync(await client.GetAsync("/api/plans"));
        mine.GetProperty("items")[0].GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Billing_endpoints_report_not_configured_without_a_provider()
    {
        var (client, _, _) = await RegisterAsync();
        var subscription = await ReadJsonAsync(await client.GetAsync("/api/billing/subscription"));
        subscription.GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("free");
        subscription.GetProperty("subscription").GetProperty("provider").GetString().ShouldBe("none");
        subscription.GetProperty("paymentsConfigured").GetBoolean().ShouldBeFalse();
        subscription.GetProperty("portalAvailable").GetBoolean().ShouldBeFalse();

        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "creator" }, Json), HttpStatusCode.Conflict, "payments_not_configured");
        await ShouldBeProblemAsync(await client.PostAsync("/api/billing/portal", null), HttpStatusCode.Conflict, "payments_not_configured");
        await ShouldBeProblemAsync(await client.PostAsync("/api/billing/cancel", null), HttpStatusCode.Conflict, "conflict");
        (await ReadJsonAsync(await client.GetAsync("/api/billing/payments"))).GetProperty("total").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Checkout_uses_the_plan_price_id_when_the_provider_is_configured()
    {
        var (client, userId, email) = await RegisterAsync();
        Factory.Payments.Configured = true;
        await WithDbAsync(db => db.Plans.Where(p => p.Code == "creator").ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceMonthlyCents, 2990).SetProperty(p => p.ProviderPriceId, "price_creator")));

        var response = await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "creator" }, Json);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await ReadJsonAsync(response)).GetProperty("checkoutUrl").GetString().ShouldStartWith("https://checkout.example.test/");
        var request = Factory.Payments.CheckoutRequests.ShouldHaveSingleItem();
        request.UserId.ShouldBe(userId);
        request.Email.ShouldBe(email);
        request.PriceId.ShouldBe("price_creator");
        request.PlanCode.ShouldBe("creator");
        request.SuccessUrl.ShouldStartWith("http://localhost:3000/billing");

        // the plan is NOT changed by the checkout call itself
        (await ReadJsonAsync(await client.GetAsync("/api/billing/subscription"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("free");
        await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "pro" }, Json), HttpStatusCode.Conflict, "conflict"); // no price
        await WithDbAsync(db => db.Plans.Where(p => p.Code == "creator").ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceMonthlyCents, (int?)null).SetProperty(p => p.ProviderPriceId, (string?)null)));
    }

    [Fact]
    public async Task Webhook_grants_entitlements_and_duplicates_are_ignored()
    {
        var (client, userId, _) = await RegisterAsync();
        var subscriptionId = await WithDbAsync(db => db.Subscriptions.Where(s => s.UserId == userId).Select(s => s.Id).FirstAsync());
        (await WithDbAsync(db => db.SubscriptionEvents.CountAsync(e => e.SubscriptionId == subscriptionId))).ShouldBe(1); // created

        var body = new
        {
            id = "evt_checkout_1",
            type = "checkout.session.completed",
            kind = "checkout_completed", paymentStatus = "paid",
            customerId = "cus_123",
            subscriptionId = "sub_123",
            planCode = "creator",
            userId,
            amountCents = 2990,
            currency = "brl",
        };

        var first = await PostWebhookAsync(body);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        (await ReadJsonAsync(first)).GetProperty("status").GetString().ShouldBe("processed");

        var second = await PostWebhookAsync(body);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ReadJsonAsync(second)).GetProperty("status").GetString().ShouldBe("processed"); // acknowledged, not reprocessed

        (await WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.ProviderEventId == "evt_checkout_1"))).ShouldBe(1);
        (await WithDbAsync(db => db.SubscriptionEvents.CountAsync(e => e.SubscriptionId == subscriptionId))).ShouldBe(2); // created + plan_changed, once
        var subscription = await WithDbAsync(db => db.Subscriptions.Include(s => s.Plan).FirstAsync(s => s.Id == subscriptionId));
        subscription.Plan.Code.ShouldBe("creator");
        subscription.Provider.ShouldBe(SubscriptionProvider.Stripe);
        subscription.ProviderCustomerId.ShouldBe("cus_123");
        subscription.ProviderSubscriptionId.ShouldBe("sub_123");
        subscription.Status.ShouldBe(SubscriptionStatus.Active);

        var me = await ReadJsonAsync(await client.GetAsync("/api/auth/me"));
        me.GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("creator");
        me.GetProperty("limits").GetProperty("monthlyProcessingMinutes").GetInt32().ShouldBe(600);
        var payments = await ReadJsonAsync(await client.GetAsync("/api/billing/payments"));
        payments.GetProperty("total").GetInt32().ShouldBe(1);
        payments.GetProperty("items")[0].GetProperty("amountCents").GetInt64().ShouldBe(2990);

        // subscription.updated with a period and cancel flag
        var periodEnd = DateTimeOffset.UtcNow.AddDays(30);
        var updated = await PostWebhookAsync(new
        {
            id = "evt_sub_updated_1", type = "customer.subscription.updated", kind = "subscription_updated",
            customerId = "cus_123", subscriptionId = "sub_123", status = "active", periodStart = DateTimeOffset.UtcNow, periodEnd, cancelAtPeriodEnd = true,
        });
        updated.StatusCode.ShouldBe(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());
        subscription = await WithDbAsync(db => db.Subscriptions.FirstAsync(s => s.Id == subscriptionId));
        subscription.CancelAtPeriodEnd.ShouldBeTrue();
        subscription.CurrentPeriodEnd.ShouldBe(periodEnd, TimeSpan.FromSeconds(1));
        (await WithDbAsync(db => db.SubscriptionEvents.Where(e => e.SubscriptionId == subscriptionId).OrderByDescending(e => e.CreatedAt).Select(e => e.Type).FirstAsync())).ShouldBe(SubscriptionEventType.Cancelled);

        // payment failed → past_due (still entitled); deleted → back to free
        (await PostWebhookAsync(new { id = "evt_inv_failed", type = "invoice.payment_failed", kind = "invoice_payment_failed", customerId = "cus_123", subscriptionId = "sub_123" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WithDbAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.Status).FirstAsync())).ShouldBe(SubscriptionStatus.PastDue);
        (await ReadJsonAsync(await client.GetAsync("/api/auth/me"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("creator");

        (await PostWebhookAsync(new { id = "evt_sub_deleted", type = "customer.subscription.deleted", kind = "subscription_deleted", customerId = "cus_123", subscriptionId = "sub_123", status = "canceled" })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await WithDbAsync(db => db.Subscriptions.Where(s => s.Id == subscriptionId).Select(s => s.Status).FirstAsync())).ShouldBe(SubscriptionStatus.Cancelled);
        me = await ReadJsonAsync(await client.GetAsync("/api/auth/me"));
        me.GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("free");
        (await WithDbAsync(db => db.Subscriptions.CountAsync(s => s.UserId == userId && s.Status == SubscriptionStatus.Active))).ShouldBe(1);

        // unknown events are stored and ignored; bad signatures are rejected
        var unknown = await PostWebhookAsync(new { id = "evt_unknown", type = "charge.refunded", kind = "unknown" });
        (await ReadJsonAsync(unknown)).GetProperty("status").GetString().ShouldBe("ignored");
        (await PostWebhookAsync(new { id = "evt_bad", type = "x", kind = "unknown" }, signature: "wrong")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await WithDbAsync(db => db.PaymentEvents.AnyAsync(e => e.ProviderEventId == "evt_bad"))).ShouldBeFalse();
    }

    private async Task ConfigurePaidPlansAsync()
    {
        Factory.Payments.Configured = true;
        await WithDbAsync(async db =>
        {
            await db.Plans.Where(p => p.Code == "creator").ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceMonthlyCents, 2990).SetProperty(p => p.ProviderPriceId, "price_creator"));
            await db.Plans.Where(p => p.Code == "pro").ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceMonthlyCents, 7990).SetProperty(p => p.ProviderPriceId, "price_pro"));
        });
    }

    /// <summary>Plans survive the database reset between tests, so the prices must be cleared again.</summary>
    private Task ResetPaidPlansAsync()
        => WithDbAsync(db => db.Plans.Where(p => p.Code == "creator" || p.Code == "pro")
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.PriceMonthlyCents, (int?)null).SetProperty(p => p.ProviderPriceId, (string?)null)));

    [Fact]
    public async Task Checkout_with_an_active_paid_subscription_switches_the_plan_in_place()
    {
        var (client, userId, _) = await RegisterAsync();
        await ConfigurePaidPlansAsync();
        try
        {
            (await PostWebhookAsync(new { id = "evt_c1", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_1", subscriptionId = "sub_1", planCode = "creator", userId })).StatusCode.ShouldBe(HttpStatusCode.OK);

            var response = await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "pro" }, Json);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var body = await ReadJsonAsync(response);
            body.GetProperty("planChanged").GetBoolean().ShouldBeTrue();
            body.GetProperty("checkoutUrl").GetString().ShouldStartWith("http://localhost:3000/billing");
            Factory.Payments.CheckoutRequests.ShouldBeEmpty(); // never a second subscription for the customer
            Factory.Payments.PlanChanges.ShouldBe([("sub_1", "price_pro", "pro")]);

            // the entitlement only changes through the provider's webhook
            (await ReadJsonAsync(await client.GetAsync("/api/billing/subscription"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("creator");
            (await PostWebhookAsync(new { id = "evt_u1", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_1", subscriptionId = "sub_1", status = "active", priceId = "price_pro" })).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await ReadJsonAsync(await client.GetAsync("/api/billing/subscription"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("pro");
            (await WithDbAsync(db => db.Subscriptions.CountAsync(s => s.UserId == userId))).ShouldBe(1);
            (await WithDbAsync(db => db.SubscriptionEvents.Where(e => e.UserId == userId).OrderByDescending(e => e.CreatedAt).Select(e => e.Type).FirstAsync())).ShouldBe(SubscriptionEventType.PlanChanged);

            // the plan already held cannot be bought again
            await ShouldBeProblemAsync(await client.PostAsJsonAsync("/api/billing/checkout", new { planCode = "pro" }, Json), HttpStatusCode.Conflict, "conflict");
            Factory.Payments.PlanChanges.Count.ShouldBe(1);
        }
        finally
        {
            await ResetPaidPlansAsync();
        }
    }

    [Fact]
    public async Task Webhooks_naming_an_unknown_subscription_never_touch_the_users_row()
    {
        var (client, userId, _) = await RegisterAsync();
        await ConfigurePaidPlansAsync();
        try
        {
            (await PostWebhookAsync(new { id = "evt_c2", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_2", subscriptionId = "sub_2", planCode = "pro", userId })).StatusCode.ShouldBe(HttpStatusCode.OK);
            var periodEnd = DateTimeOffset.UtcNow.AddDays(20);
            (await PostWebhookAsync(new { id = "evt_u2", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_2", subscriptionId = "sub_2", status = "active", priceId = "price_pro", periodEnd })).StatusCode.ShouldBe(HttpStatusCode.OK);

            // events of another (replaced/orphaned) subscription of the same customer and user are ignored, even with a price id and user metadata
            foreach (var (id, type, kind) in new[]
            {
                ("evt_o1", "customer.subscription.updated", "subscription_updated"),
                ("evt_o2", "customer.subscription.created", "subscription_created"),
                ("evt_o3", "invoice.payment_failed", "invoice_payment_failed"),
                ("evt_o4", "invoice.paid", "invoice_paid"),
                ("evt_o5", "customer.subscription.deleted", "subscription_deleted"),
            })
            {
                var response = await PostWebhookAsync(new { id, type, kind, customerId = "cus_2", subscriptionId = "sub_old", userId, status = "canceled", priceId = "price_creator", periodEnd = DateTimeOffset.UtcNow.AddDays(-1), cancelAtPeriodEnd = true });
                response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                (await ReadJsonAsync(response)).GetProperty("status").GetString().ShouldBe("ignored");
            }
            var subscription = await WithDbAsync(db => db.Subscriptions.Include(s => s.Plan).SingleAsync(s => s.UserId == userId));
            subscription.Plan.Code.ShouldBe("pro");
            subscription.Status.ShouldBe(SubscriptionStatus.Active);
            subscription.ProviderSubscriptionId.ShouldBe("sub_2");
            subscription.CancelAtPeriodEnd.ShouldBeFalse();
            subscription.CurrentPeriodEnd.ShouldBe(periodEnd, TimeSpan.FromSeconds(1));
            (await ReadJsonAsync(await client.GetAsync("/api/auth/me"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("pro");
        }
        finally
        {
            await ResetPaidPlansAsync();
        }
    }

    [Fact]
    public async Task Second_checkout_replaces_and_retires_the_previous_provider_subscription()
    {
        var (client, userId, _) = await RegisterAsync();
        await ConfigurePaidPlansAsync();
        try
        {
            (await PostWebhookAsync(new { id = "evt_c3", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_3", subscriptionId = "sub_3a", planCode = "creator", userId })).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await PostWebhookAsync(new { id = "evt_c4", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_3", subscriptionId = "sub_3b", planCode = "pro", userId })).StatusCode.ShouldBe(HttpStatusCode.OK);

            var subscription = await WithDbAsync(db => db.Subscriptions.Include(s => s.Plan).SingleAsync(s => s.UserId == userId));
            subscription.Plan.Code.ShouldBe("pro");
            subscription.ProviderSubscriptionId.ShouldBe("sub_3b");
            Factory.Payments.CancelledSubscriptions.ShouldBe(["sub_3a"]);
            var data = await WithDbAsync(db => db.SubscriptionEvents.Where(e => e.UserId == userId).OrderByDescending(e => e.CreatedAt).Select(e => e.Data).FirstAsync());
            using (var eventData = System.Text.Json.JsonDocument.Parse(data.ShouldNotBeNull()))
            {
                eventData.RootElement.GetProperty("replacedProviderSubscriptionId").GetString().ShouldBe("sub_3a");
            }

            // the retired subscription's later webhooks do not downgrade the user
            (await ReadJsonAsync(await PostWebhookAsync(new { id = "evt_d3", type = "customer.subscription.deleted", kind = "subscription_deleted", customerId = "cus_3", subscriptionId = "sub_3a", status = "canceled" }))).GetProperty("status").GetString().ShouldBe("ignored");
            (await ReadJsonAsync(await client.GetAsync("/api/auth/me"))).GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("pro");
        }
        finally
        {
            await ResetPaidPlansAsync();
        }
    }

    [Fact]
    public async Task Cancel_at_period_end_goes_through_the_provider()
    {
        var (client, userId, _) = await RegisterAsync();
        Factory.Payments.Configured = true;
        (await PostWebhookAsync(new { id = "evt_c", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_9", subscriptionId = "sub_9", planCode = "pro", userId })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var cancel = await client.PostAsync("/api/billing/cancel", null);
        cancel.StatusCode.ShouldBe(HttpStatusCode.Accepted, await cancel.Content.ReadAsStringAsync());
        Factory.Payments.CancelledSubscriptions.ShouldBe(["sub_9"]);
        var subscription = await ReadJsonAsync(await client.GetAsync("/api/billing/subscription"));
        subscription.GetProperty("subscription").GetProperty("cancelAtPeriodEnd").GetBoolean().ShouldBeTrue();
        subscription.GetProperty("subscription").GetProperty("planCode").GetString().ShouldBe("pro");
        subscription.GetProperty("portalAvailable").GetBoolean().ShouldBeTrue();
        (await ReadJsonAsync(await client.PostAsync("/api/billing/portal", null))).GetProperty("url").GetString().ShouldBe("https://portal.example.test/cus_9");
    }
}
