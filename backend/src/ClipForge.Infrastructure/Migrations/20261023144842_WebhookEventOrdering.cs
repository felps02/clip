using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClipForge.Infrastructure.Migrations
{
    /// <summary>
    /// Payment webhooks arrive out of order and failed ones are redelivered later (docs/PLANS_AND_BILLING.md):
    /// subscriptions.last_provider_event_at keeps the provider time of the newest subscription snapshot applied, and
    /// payment_events records each event's normalised kind, provider subscription and provider time, so a late
    /// activation of a subscription the provider already deleted is recognised even if it was never bound.
    /// </summary>
    public partial class WebhookEventOrdering : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_provider_event_at",
                table: "subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "payment_events",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "occurred_at",
                table: "payment_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_subscription_id",
                table: "payment_events",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_payment_events_provider_provider_subscription_id",
                table: "payment_events",
                columns: new[] { "provider", "provider_subscription_id" });

            // Deletions received before this migration must also block a late activation of their subscription: fill
            // the new columns from the stored Stripe payload (data.object is the subscription; created is Unix seconds).
            migrationBuilder.Sql("""
                UPDATE payment_events
                SET kind = 'subscription_deleted',
                    provider_subscription_id = left(payload #>> '{data,object,id}', 200),
                    occurred_at = CASE WHEN jsonb_typeof(payload -> 'created') = 'number'
                                       THEN to_timestamp((payload ->> 'created')::double precision) END
                WHERE provider = 'stripe'
                  AND type = 'customer.subscription.deleted'
                  AND kind IS NULL
                  AND payload #>> '{data,object,object}' = 'subscription';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_payment_events_provider_provider_subscription_id",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "last_provider_event_at",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "occurred_at",
                table: "payment_events");

            migrationBuilder.DropColumn(
                name: "provider_subscription_id",
                table: "payment_events");
        }
    }
}
