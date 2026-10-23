using ClipForge.Api.Features.Webhooks;
using ClipForge.Domain.Enums;

namespace ClipForge.Api.Tests.Unit;

public class WebhookStatusTests
{
    [Theory]
    [InlineData("active", SubscriptionStatus.Active)]
    [InlineData("trialing", SubscriptionStatus.Trialing)]
    [InlineData("past_due", SubscriptionStatus.PastDue)]
    [InlineData("canceled", SubscriptionStatus.Cancelled)]
    [InlineData("unpaid", SubscriptionStatus.Expired)]
    [InlineData("incomplete_expired", SubscriptionStatus.Expired)]
    [InlineData("paused", SubscriptionStatus.Expired)]
    [InlineData(" ACTIVE ", SubscriptionStatus.Active)]
    public void Provider_statuses_map_to_entitlement_statuses(string providerStatus, SubscriptionStatus expected)
    {
        WebhookService.TryMapStatus(providerStatus, out var status).ShouldBeTrue();
        status.ShouldBe(expected);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("something_new")]
    public void Statuses_without_a_confirmed_payment_or_unknown_map_to_nothing(string providerStatus)
        => WebhookService.TryMapStatus(providerStatus, out _).ShouldBeFalse();

    [Theory]
    [InlineData("paid", true)]
    [InlineData("no_payment_required", true)]
    [InlineData("unpaid", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Only_paid_or_free_checkouts_confirm_the_payment(string? paymentStatus, bool confirmed)
        => WebhookService.IsPaymentConfirmed(paymentStatus).ShouldBe(confirmed);

    [Fact]
    public void Entitled_statuses_are_active_trialing_and_past_due()
    {
        foreach (var status in Enum.GetValues<SubscriptionStatus>())
        {
            new ClipForge.Domain.Entities.Subscription { Status = status }.IsEntitled
                .ShouldBe(status is SubscriptionStatus.Active or SubscriptionStatus.Trialing or SubscriptionStatus.PastDue);
        }
    }
}
