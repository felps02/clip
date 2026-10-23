using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ClipForge.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class NotificationPreferencesContract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Data only. Documents written before the preference contract (keys emailOnJobCompleted, emailOnJobFailed,
            // emailBilling, inApp) never held a user's choice: the old settings page sent other keys, which the API
            // dropped, so every row carries the old class defaults. They become the contract defaults
            // (docs/API.md → Account); documents that already have the new keys are left alone.
            migrationBuilder.Sql("""
                UPDATE user_profiles
                SET notification_preferences = '{"emailOnJobCompleted": false, "emailOnJobFailed": true, "emailOnQuotaWarning": true, "emailOnBilling": true}'::jsonb
                WHERE jsonb_typeof(notification_preferences) IS DISTINCT FROM 'object'
                   OR NOT (notification_preferences ? 'emailOnQuotaWarning');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to restore: the replaced documents only held defaults, and the new shape reads fine with older code
            // (unknown keys are ignored).
        }
    }
}
