using System.Net;
using System.Net.Http.Json;
using System.Text;
using ClipForge.Api.Services;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Domain.Entities;
using ClipForge.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>
/// Payment webhooks: entitlement only for a confirmed payment, failures answered 500 internal_error and processed again
/// on redelivery, duplicates acknowledged once, late or out-of-order events never undoing newer state (a cancelled
/// subscription stays cancelled, older snapshots are ignored), billing e-mails following emailOnBilling (sent to a
/// confirmed address).
/// </summary>
public class WebhookEntitlementTests : TestBase
{
    public WebhookEntitlementTests(ApiFactory factory) : base(factory)
    {
    }

    private async Task<HttpResponseMessage> PostWebhookAsync(object body)
    {
        var client = CreateClient();
        using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/stripe") { Content = content };
        request.Headers.Add("Stripe-Signature", FakePaymentProvider.Secret);
        return await client.SendAsync(request);
    }

    private async Task<string> PostOkAsync(object body)
    {
        var response = await PostWebhookAsync(body);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await ReadJsonAsync(response)).GetProperty("status").GetString()!;
    }

    private async Task<(string PlanCode, string Status, int Minutes)> EntitlementAsync(HttpClient client)
    {
        var me = await ReadJsonAsync(await client.GetAsync("/api/auth/me"));
        return (me.GetProperty("subscription").GetProperty("planCode").GetString()!, me.GetProperty("subscription").GetProperty("status").GetString()!,
            me.GetProperty("limits").GetProperty("monthlyProcessingMinutes").GetInt32());
    }

    /// <summary>Provider event times (Stripe Event.Created has one-second resolution).</summary>
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static object Snapshot(string id, string subscriptionId, string status, int second, Guid? userId = null, string? planCode = null)
        => new
        {
            id,
            type = "customer.subscription.updated",
            kind = "subscription_updated",
            customerId = "cus_" + subscriptionId,
            subscriptionId,
            status,
            planCode,
            userId,
            occurredAt = T0.AddSeconds(second),
        };

    private static object Deleted(string id, string subscriptionId, int second, Guid? userId = null)
        => new { id, type = "customer.subscription.deleted", kind = "subscription_deleted", customerId = "cus_" + subscriptionId, subscriptionId, status = "canceled", userId, occurredAt = T0.AddSeconds(second) };

    private static object Invoice(string id, string subscriptionId, bool paid, int second)
        => new
        {
            id,
            type = paid ? "invoice.paid" : "invoice.payment_failed",
            kind = paid ? "invoice_paid" : "invoice_payment_failed",
            customerId = "cus_" + subscriptionId,
            subscriptionId,
            occurredAt = T0.AddSeconds(second),
        };

    private static object PaidCheckout(string id, string subscriptionId, Guid userId, int second, string planCode = "pro")
        => new { id, type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_" + subscriptionId, subscriptionId, planCode, userId, occurredAt = T0.AddSeconds(second) };

    private Task<int> BillingNotificationsAsync(Guid userId, string title)
        => WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.Billing && n.Title == title));

    [Fact]
    public async Task Deleted_subscription_followed_by_a_stale_active_snapshot_leaves_the_user_on_free()
    {
        var (client, userId, _) = await RegisterAsync();
        (await PostOkAsync(Snapshot("evt_d_active", "sub_d", "active", 10, userId, "pro"))).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));
        (await PostOkAsync(Deleted("evt_d_deleted", "sub_d", 30, userId))).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));

        // an "active" snapshot from before the deletion delivered late (e.g. it failed and the provider retried it)
        (await PostOkAsync(Snapshot("evt_d_stale", "sub_d", "active", 20, userId, "pro"))).ShouldBe("ignored");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));

        // cancellation is final whatever the event claims or its date: undated, newer, past_due, invoices, the checkout
        var undated = new { id = "evt_d_undated", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_sub_d", subscriptionId = "sub_d", status = "active", planCode = "pro", userId };
        (await PostOkAsync(undated)).ShouldBe("ignored");
        (await PostOkAsync(Snapshot("evt_d_newer", "sub_d", "active", 40, userId, "pro"))).ShouldBe("ignored");
        (await PostOkAsync(Snapshot("evt_d_past_due", "sub_d", "past_due", 25))).ShouldBe("ignored");
        (await PostOkAsync(Invoice("evt_d_paid", "sub_d", paid: true, 26))).ShouldBe("ignored");
        (await PostOkAsync(Invoice("evt_d_failed", "sub_d", paid: false, 27))).ShouldBe("ignored");
        // a late checkout of the cancelled subscription is acknowledged (no failure loop) and grants nothing
        (await PostOkAsync(PaidCheckout("evt_d_checkout", "sub_d", userId, 11))).ShouldBe("ignored");

        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        var rows = await WithDbAsync(db => db.Subscriptions.Where(s => s.UserId == userId).ToListAsync());
        rows.Single(s => s.ProviderSubscriptionId == "sub_d").Status.ShouldBe(SubscriptionStatus.Cancelled);
        rows.Where(s => s.IsEntitled).ShouldHaveSingleItem().Provider.ShouldBe(SubscriptionProvider.None);
        (await BillingNotificationsAsync(userId, "Assinatura ativada")).ShouldBe(1);
        (await BillingNotificationsAsync(userId, "Assinatura encerrada")).ShouldBe(1);
        (await BillingNotificationsAsync(userId, "Falha no pagamento")).ShouldBe(0);
    }

    [Fact]
    public async Task Failed_activations_redelivered_after_the_deletion_grant_nothing()
    {
        // Real redeliveries: the first delivery of each activation fails (the user in the metadata does not exist yet,
        // standing in for any transient error) and is stored as failed; the provider retries after the deletions.
        var userId = Guid.NewGuid();
        var boundActivation = Snapshot("evt_f1_active", "sub_f1", "active", 10, userId, "pro");
        var unboundActivation = Snapshot("evt_f2_active", "sub_f2", "active", 10, userId, "pro");
        var unboundCheckout = PaidCheckout("evt_f2_checkout", "sub_f2", userId, 11);
        foreach (var body in new[] { boundActivation, unboundActivation, unboundCheckout })
        {
            await ShouldBeProblemAsync(await PostWebhookAsync(body), HttpStatusCode.InternalServerError, "internal_error");
        }

        var email = NextEmail("redelivery");
        await using (var scope = Factory.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.CreateAsync(new AppUser { Id = userId, UserName = email, Email = email, DisplayName = "Late User" }, "Password123")).Succeeded.ShouldBeTrue();
        }
        // sub_f1 is bound by its checkout, then deleted; sub_f2 is deleted before anything bound it
        (await PostOkAsync(PaidCheckout("evt_f1_checkout", "sub_f1", userId, 11))).ShouldBe("processed");
        (await PostOkAsync(Deleted("evt_f1_deleted", "sub_f1", 30, userId))).ShouldBe("processed");
        (await PostOkAsync(Deleted("evt_f2_deleted", "sub_f2", 30, userId))).ShouldBe("ignored");

        // the failed events come back: none of them reactivates or binds a deleted subscription
        (await PostOkAsync(boundActivation)).ShouldBe("ignored");
        (await PostOkAsync(unboundActivation)).ShouldBe("ignored");
        (await PostOkAsync(unboundCheckout)).ShouldBe("ignored");

        var rows = await WithDbAsync(db => db.Subscriptions.Include(s => s.Plan).Where(s => s.UserId == userId).ToListAsync());
        rows.Single(s => s.ProviderSubscriptionId == "sub_f1").Status.ShouldBe(SubscriptionStatus.Cancelled);
        rows.ShouldNotContain(s => s.ProviderSubscriptionId == "sub_f2");
        rows.Where(s => s.IsEntitled).ShouldHaveSingleItem().Plan.Code.ShouldBe("free");
        await using (var scope = Factory.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<SubscriptionService>().GetEntitlementAsync(userId)).Plan.Code.ShouldBe("free");
        }

        // the stored events say what they were about and when the provider created them, and they are now handled
        var stored = await WithDbAsync(db => db.PaymentEvents.Where(e => e.ProviderSubscriptionId == "sub_f2").OrderBy(e => e.OccurredAt).ToListAsync());
        stored.Select(e => (e.ProviderEventId, e.Kind, e.Status)).ShouldBe([
            ("evt_f2_active", "subscription_updated", PaymentEventStatus.Ignored),
            ("evt_f2_checkout", "checkout_completed", PaymentEventStatus.Ignored),
            ("evt_f2_deleted", "subscription_deleted", PaymentEventStatus.Ignored),
        ]);
        stored[2].OccurredAt.ShouldBe(T0.AddSeconds(30));
    }

    [Fact]
    public async Task Older_events_never_overwrite_newer_subscription_state()
    {
        var (client, userId, _) = await RegisterAsync();
        (await PostOkAsync(Snapshot("evt_o_creator", "sub_o", "active", 10, userId, "creator"))).ShouldBe("processed");
        (await PostOkAsync(Snapshot("evt_o_pro", "sub_o", "active", 30, userId, "pro"))).ShouldBe("processed"); // upgraded in place
        // a snapshot from before the upgrade, delivered late, does not take the upgrade back
        (await PostOkAsync(Snapshot("evt_o_old_plan", "sub_o", "active", 20, userId, "creator"))).ShouldBe("ignored");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));

        // the renewal is retried (past_due keeps the plan), then the retries are exhausted (unpaid suspends it)
        (await PostOkAsync(Snapshot("evt_o_past_due", "sub_o", "past_due", 40))).ShouldBe("processed");
        (await PostOkAsync(Snapshot("evt_o_unpaid", "sub_o", "unpaid", 50))).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        // the past_due snapshot (entitled) redelivered after "unpaid", an older invoice.paid or the original checkout
        // arriving late do not give the plan back
        (await PostOkAsync(Snapshot("evt_o_late_past_due", "sub_o", "past_due", 45))).ShouldBe("ignored");
        (await PostOkAsync(Invoice("evt_o_late_paid", "sub_o", paid: true, 35))).ShouldBe("ignored");
        (await PostOkAsync(PaidCheckout("evt_o_late_checkout", "sub_o", userId, 9))).ShouldBe("ignored");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        (await WithDbAsync(db => db.Subscriptions.CountAsync(s => s.ProviderSubscriptionId == "sub_o"))).ShouldBe(1);

        // the open invoice is paid: the newer "active" snapshot gives the plan back
        (await PostOkAsync(Snapshot("evt_o_active", "sub_o", "active", 60))).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));
        // a payment failure from before that recovery is neither applied nor announced
        (await PostOkAsync(Invoice("evt_o_late_failed", "sub_o", paid: false, 42))).ShouldBe("ignored");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));
        (await BillingNotificationsAsync(userId, "Falha no pagamento")).ShouldBe(0);

        // events of the same second are not ordered (the provider's resolution): they apply in arrival order
        (await PostOkAsync(Snapshot("evt_o_same_second", "sub_o", "past_due", 60))).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "past_due", 2400));
        // the failure behind that past_due, created a second earlier but delivered after it, is still announced
        (await PostOkAsync(Invoice("evt_o_failed", "sub_o", paid: false, 59))).ShouldBe("processed");
        (await BillingNotificationsAsync(userId, "Falha no pagamento")).ShouldBe(1);
        (await EntitlementAsync(client)).ShouldBe(("pro", "past_due", 2400));
        (await WithDbAsync(db => db.Subscriptions.Where(s => s.ProviderSubscriptionId == "sub_o").Select(s => s.LastProviderEventAt).SingleAsync())).ShouldBe(T0.AddSeconds(60));
    }

    [Fact]
    public async Task Checkout_without_a_confirmed_payment_and_incomplete_subscriptions_grant_nothing()
    {
        var (client, userId, _) = await RegisterAsync();

        // Checkout in subscription mode: the subscription is created "incomplete" before the first invoice is paid
        (await PostOkAsync(new { id = "evt_created_incomplete", type = "customer.subscription.created", kind = "subscription_created", customerId = "cus_i", subscriptionId = "sub_i", status = "incomplete", planCode = "pro", userId })).ShouldBe("ignored");
        // an abandoned/declined (or still pending boleto/PIX) checkout completes as unpaid
        (await PostOkAsync(new { id = "evt_checkout_unpaid", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "unpaid", customerId = "cus_i", subscriptionId = "sub_i", planCode = "pro", userId })).ShouldBe("ignored");
        // a checkout event without a payment status is not a confirmed payment either
        (await PostOkAsync(new { id = "evt_checkout_unknown", type = "checkout.session.completed", kind = "checkout_completed", customerId = "cus_i", subscriptionId = "sub_i", planCode = "pro", userId })).ShouldBe("ignored");

        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        // an ignored event redelivered is acknowledged again without being processed
        (await PostOkAsync(new { id = "evt_checkout_unpaid", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_i", subscriptionId = "sub_i", planCode = "pro", userId })).ShouldBe("ignored");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        (await WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.ProviderEventId == "evt_checkout_unpaid"))).ShouldBe(1);
        var row = await WithDbAsync(db => db.Subscriptions.SingleAsync(s => s.UserId == userId));
        row.ProviderSubscriptionId.ShouldBeNull(); // not even bound
        row.Provider.ShouldBe(SubscriptionProvider.None);

        // the payment clears: the subscription becomes active → bound and granted
        (await PostOkAsync(new { id = "evt_updated_active", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_i", subscriptionId = "sub_i", status = "active", planCode = "pro", userId })).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));

        // late deliveries never take it back: an out-of-order "incomplete" keeps the row active, the paid checkout is idempotent
        (await PostOkAsync(new { id = "evt_created_late", type = "customer.subscription.created", kind = "subscription_created", customerId = "cus_i", subscriptionId = "sub_i", status = "incomplete", planCode = "pro", userId })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_checkout_paid", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_i", subscriptionId = "sub_i", planCode = "pro", userId })).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.Billing && n.Title == "Assinatura ativada"))).ShouldBe(1);

        // no_payment_required (100 % coupon, trial) confirms the checkout too
        var (other, otherId, _) = await RegisterAsync();
        (await PostOkAsync(new { id = "evt_checkout_free", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "no_payment_required", customerId = "cus_f", subscriptionId = "sub_f", planCode = "creator", userId = otherId })).ShouldBe("processed");
        (await EntitlementAsync(other)).ShouldBe(("creator", "active", 600));
    }

    [Fact]
    public async Task Unpaid_subscription_loses_the_plan_and_gets_it_back_when_paid()
    {
        var (client, userId, _) = await RegisterAsync();
        (await PostOkAsync(new { id = "evt_c", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_u", subscriptionId = "sub_u", planCode = "pro", userId })).ShouldBe("processed");
        (await EntitlementAsync(client)).PlanCode.ShouldBe("pro");

        // a renewal being retried keeps the plan (grace period) ...
        (await PostOkAsync(new { id = "evt_past_due", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_u", subscriptionId = "sub_u", status = "past_due" })).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "past_due", 2400));
        // ... "unpaid" (retries exhausted) does not
        (await PostOkAsync(new { id = "evt_unpaid", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_u", subscriptionId = "sub_u", status = "unpaid" })).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("free", "active", 60));
        (await WithDbAsync(db => db.Subscriptions.Where(s => s.ProviderSubscriptionId == "sub_u").Select(s => s.Status).SingleAsync())).ShouldBe(SubscriptionStatus.Expired);

        // the open invoice is paid: the subscription is active again and the free fallback stops being current
        (await PostOkAsync(new { id = "evt_active_again", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_u", subscriptionId = "sub_u", status = "active" })).ShouldBe("processed");
        (await EntitlementAsync(client)).ShouldBe(("pro", "active", 2400));
        var entitled = await WithDbAsync(db => db.Subscriptions.Where(s => s.UserId == userId && (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing || s.Status == SubscriptionStatus.PastDue)).ToListAsync());
        entitled.ShouldHaveSingleItem().ProviderSubscriptionId.ShouldBe("sub_u");
    }

    [Fact]
    public async Task Processing_failure_answers_500_internal_error_and_the_redelivery_processes_the_event()
    {
        // the event names a user that does not exist yet: applying it fails (foreign key) and nothing is half-written
        var userId = Guid.NewGuid();
        var body = new { id = "evt_retry", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_r", subscriptionId = "sub_r", planCode = "creator", userId };
        var failed = await PostWebhookAsync(body);
        await ShouldBeProblemAsync(failed, HttpStatusCode.InternalServerError, "internal_error");
        var stored = await WithDbAsync(db => db.PaymentEvents.SingleAsync(e => e.ProviderEventId == "evt_retry"));
        stored.Status.ShouldBe(PaymentEventStatus.Failed);
        stored.Error.ShouldNotBeNullOrEmpty();
        (await WithDbAsync(db => db.Subscriptions.AnyAsync(s => s.UserId == userId))).ShouldBeFalse();

        // the condition is fixed (here: the user now exists) and the provider redelivers the same event
        var email = NextEmail("late");
        await using (var scope = Factory.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            (await users.CreateAsync(new AppUser { Id = userId, UserName = email, Email = email, DisplayName = "Late User" }, "Password123")).Succeeded.ShouldBeTrue();
        }
        (await PostOkAsync(body)).ShouldBe("processed");
        var subscription = await WithDbAsync(db => db.Subscriptions.Include(s => s.Plan).SingleAsync(s => s.UserId == userId));
        subscription.Plan.Code.ShouldBe("creator");
        subscription.ProviderSubscriptionId.ShouldBe("sub_r");
        var history = await WithDbAsync(db => db.SubscriptionEvents.CountAsync(e => e.UserId == userId));

        // a processed duplicate is acknowledged without being applied again
        (await PostOkAsync(body)).ShouldBe("processed");
        (await WithDbAsync(db => db.SubscriptionEvents.CountAsync(e => e.UserId == userId))).ShouldBe(history);
        (await WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.ProviderEventId == "evt_retry"))).ShouldBe(1);
        (await WithDbAsync(db => db.PaymentEvents.Where(e => e.ProviderEventId == "evt_retry").Select(e => e.Status).SingleAsync())).ShouldBe(PaymentEventStatus.Processed);
    }

    [Fact]
    public async Task Concurrent_deliveries_of_one_event_apply_it_once()
    {
        var (client, userId, _) = await RegisterAsync();
        var body = new { id = "evt_parallel", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_p", subscriptionId = "sub_p", planCode = "pro", userId };
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostWebhookAsync(body)));
        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        }
        (await WithDbAsync(db => db.PaymentEvents.CountAsync(e => e.ProviderEventId == "evt_parallel"))).ShouldBe(1);
        // created (registration) + plan_changed, once
        (await WithDbAsync(db => db.SubscriptionEvents.CountAsync(e => e.UserId == userId))).ShouldBe(2);
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == userId && n.Type == NotificationType.Billing))).ShouldBe(1);
        (await EntitlementAsync(client)).PlanCode.ShouldBe("pro");
    }

    [Fact]
    public async Task Billing_emails_follow_the_billing_preference()
    {
        var (_, userId, email) = await RegisterAsync(confirmEmail: true);
        (await PostOkAsync(new { id = "evt_b1", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_b", subscriptionId = "sub_b", planCode = "pro", userId })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_b2", type = "invoice.payment_failed", kind = "invoice_payment_failed", customerId = "cus_b", subscriptionId = "sub_b" })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_b3", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_b", subscriptionId = "sub_b", status = "active", cancelAtPeriodEnd = true, periodEnd = DateTimeOffset.UtcNow.AddDays(10) })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_b4", type = "customer.subscription.updated", kind = "subscription_updated", customerId = "cus_b", subscriptionId = "sub_b", status = "active", planCode = "creator", cancelAtPeriodEnd = false })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_b5", type = "customer.subscription.deleted", kind = "subscription_deleted", customerId = "cus_b", subscriptionId = "sub_b", status = "canceled" })).ShouldBe("processed");

        var subjects = (await SentEmailsAsync(email)).Select(m => m.Subject).Where(s => !s.Contains("confirme seu e-mail")).ToList();
        subjects.ShouldBe([
            "ClipForge: assinatura ativada",
            "ClipForge: falha no pagamento",
            "ClipForge: cancelamento agendado",
            "ClipForge: plano alterado",
            "ClipForge: assinatura encerrada",
        ]);
        var cancellation = Factory.Emails.Messages.Single(m => m.Subject == "ClipForge: cancelamento agendado");
        cancellation.TextBody.ShouldContain("http://localhost:3000/billing");

        // emailOnBilling = false: the in-app notifications are still created, no e-mail
        var (quietClient, quietId, quietEmail) = await RegisterAsync(confirmEmail: true);
        await UpdateProfileAsync(quietClient, new { notificationPreferences = new { emailOnBilling = false } });
        (await PostOkAsync(new { id = "evt_q1", type = "checkout.session.completed", kind = "checkout_completed", paymentStatus = "paid", customerId = "cus_q", subscriptionId = "sub_q", planCode = "pro", userId = quietId })).ShouldBe("processed");
        (await PostOkAsync(new { id = "evt_q2", type = "invoice.payment_failed", kind = "invoice_payment_failed", customerId = "cus_q", subscriptionId = "sub_q" })).ShouldBe("processed");
        (await SentEmailsAsync(quietEmail)).ShouldNotContain(m => !m.Subject.Contains("confirme seu e-mail"));
        (await WithDbAsync(db => db.Notifications.CountAsync(n => n.UserId == quietId && n.Type == NotificationType.Billing))).ShouldBe(2);
    }
}
