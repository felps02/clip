using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClipForge.Api.Tests.Infrastructure;
using ClipForge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace ClipForge.Api.Tests.Integration;

/// <summary>Profile preference contract shared with the web (docs/API.md → Account).</summary>
public class PreferencesTests : TestBase
{
    public PreferencesTests(ApiFactory factory) : base(factory)
    {
    }

    private static void ShouldHaveEditing(JsonElement editing, string aspectRatio, bool subtitlesEnabled, string style, string position, int min, int max, int target, string language)
    {
        editing.GetProperty("aspectRatio").GetString().ShouldBe(aspectRatio);
        editing.GetProperty("subtitlesEnabled").GetBoolean().ShouldBe(subtitlesEnabled);
        editing.GetProperty("subtitleStyle").GetString().ShouldBe(style);
        editing.GetProperty("subtitlePosition").GetString().ShouldBe(position);
        editing.GetProperty("minClipSeconds").GetInt32().ShouldBe(min);
        editing.GetProperty("maxClipSeconds").GetInt32().ShouldBe(max);
        editing.GetProperty("targetClipSeconds").GetInt32().ShouldBe(target);
        editing.GetProperty("language").GetString().ShouldBe(language);
    }

    private static void ShouldHaveNotifications(JsonElement notifications, bool completed, bool failed, bool quota, bool billing)
    {
        notifications.GetProperty("emailOnJobCompleted").GetBoolean().ShouldBe(completed);
        notifications.GetProperty("emailOnJobFailed").GetBoolean().ShouldBe(failed);
        notifications.GetProperty("emailOnQuotaWarning").GetBoolean().ShouldBe(quota);
        notifications.GetProperty("emailOnBilling").GetBoolean().ShouldBe(billing);
    }

    [Fact]
    public async Task New_profiles_expose_the_contract_defaults()
    {
        var (client, _, _) = await RegisterAsync();
        var profile = await ReadJsonAsync(await client.GetAsync("/api/account/profile"));
        ShouldHaveEditing(profile.GetProperty("editingPreferences"), "9:16", true, "bold", "bottom", 15, 60, 30, "auto");
        ShouldHaveNotifications(profile.GetProperty("notificationPreferences"), completed: false, failed: true, quota: true, billing: true);
        profile.GetProperty("notificationPreferences").EnumerateObject().Select(p => p.Name).ShouldBe(["emailOnJobCompleted", "emailOnJobFailed", "emailOnQuotaWarning", "emailOnBilling"], ignoreOrder: true);

        var me = await ReadJsonAsync(await client.GetAsync("/api/auth/me"));
        ShouldHaveEditing(me.GetProperty("profile").GetProperty("editingPreferences"), "9:16", true, "bold", "bottom", 15, 60, 30, "auto");
    }

    [Fact]
    public async Task Put_profile_merges_nested_preferences_field_by_field()
    {
        var (client, _, _) = await RegisterAsync();
        var full = await UpdateProfileAsync(client, new
        {
            editingPreferences = new { aspectRatio = "16:9", subtitlesEnabled = false, subtitleStyle = "clean", subtitlePosition = "top", minClipSeconds = 20, maxClipSeconds = 90, targetClipSeconds = 45, language = "pt" },
            notificationPreferences = new { emailOnJobCompleted = true },
        });
        ShouldHaveEditing(full.GetProperty("editingPreferences"), "16:9", false, "clean", "top", 20, 90, 45, "pt");
        ShouldHaveNotifications(full.GetProperty("notificationPreferences"), completed: true, failed: true, quota: true, billing: true);

        // omitted fields keep the stored values
        var partial = await UpdateProfileAsync(client, new
        {
            editingPreferences = new { aspectRatio = "1:1", maxClipSeconds = 300 },
            notificationPreferences = new { emailOnBilling = false },
        });
        ShouldHaveEditing(partial.GetProperty("editingPreferences"), "1:1", false, "clean", "top", 20, 300, 45, "pt");
        ShouldHaveNotifications(partial.GetProperty("notificationPreferences"), completed: true, failed: true, quota: true, billing: false);

        // a request without the nested objects leaves them alone; the language is stored canonical (lower case)
        await UpdateProfileAsync(client, new { displayName = "Outro Nome" });
        await UpdateProfileAsync(client, new { editingPreferences = new { language = "ES" } });
        var stored = await ReadJsonAsync(await client.GetAsync("/api/account/profile"));
        stored.GetProperty("displayName").GetString().ShouldBe("Outro Nome");
        ShouldHaveEditing(stored.GetProperty("editingPreferences"), "1:1", false, "clean", "top", 20, 300, 45, "es");
        ShouldHaveNotifications(stored.GetProperty("notificationPreferences"), completed: true, failed: true, quota: true, billing: false);
    }

    [Fact]
    public async Task Invalid_preferences_are_rejected_with_field_errors_and_nothing_is_written()
    {
        var (client, _, _) = await RegisterAsync(displayName: "Nome Original");

        async Task ExpectInvalidAsync(object body, params string[] keys)
        {
            var response = await client.PutAsJsonAsync("/api/account/profile", body, Json);
            await ShouldBeProblemAsync(response, HttpStatusCode.BadRequest, "validation_failed");
            var errors = (await ReadJsonAsync(response)).GetProperty("errors");
            foreach (var key in keys)
            {
                errors.TryGetProperty(key, out _).ShouldBeTrue($"{key} missing in {errors}");
            }
        }

        await ExpectInvalidAsync(new { editingPreferences = new { aspectRatio = "4:3" } }, "editingPreferences.aspectRatio");
        await ExpectInvalidAsync(new { editingPreferences = new { subtitleStyle = "neon" } }, "editingPreferences.subtitleStyle");
        await ExpectInvalidAsync(new { editingPreferences = new { subtitlePosition = "left" } }, "editingPreferences.subtitlePosition");
        await ExpectInvalidAsync(new { editingPreferences = new { minClipSeconds = 4 } }, "editingPreferences.minClipSeconds");
        await ExpectInvalidAsync(new { editingPreferences = new { maxClipSeconds = 301 } }, "editingPreferences.maxClipSeconds");
        await ExpectInvalidAsync(new { editingPreferences = new { targetClipSeconds = 0 } }, "editingPreferences.targetClipSeconds");
        await ExpectInvalidAsync(new { editingPreferences = new { language = "fr" } }, "editingPreferences.language");
        await ExpectInvalidAsync(new { editingPreferences = new { language = new string('x', 600) + " ; drop" } }, "editingPreferences.language");
        // relations are checked against the stored values of the omitted fields (stored: 15 / 60 / 30)
        await ExpectInvalidAsync(new { editingPreferences = new { maxClipSeconds = 10 } }, "editingPreferences.maxClipSeconds", "editingPreferences.targetClipSeconds");
        await ExpectInvalidAsync(new { editingPreferences = new { targetClipSeconds = 61 } }, "editingPreferences.targetClipSeconds");
        await ExpectInvalidAsync(new { editingPreferences = new { minClipSeconds = 40 } }, "editingPreferences.targetClipSeconds");
        // wrong types never bind
        await ShouldBeProblemAsync(await client.PutAsJsonAsync("/api/account/profile", new { editingPreferences = new { minClipSeconds = 12.5 } }, Json), HttpStatusCode.BadRequest, "validation_failed");
        await ShouldBeProblemAsync(await client.PutAsJsonAsync("/api/account/profile", new { notificationPreferences = new { emailOnBilling = "yes" } }, Json), HttpStatusCode.BadRequest, "validation_failed");

        // an invalid preference fails the whole request: the valid display name in it is not written either
        await ExpectInvalidAsync(new { displayName = "Nome Novo", editingPreferences = new { targetClipSeconds = 999 } }, "editingPreferences.targetClipSeconds");
        var profile = await ReadJsonAsync(await client.GetAsync("/api/account/profile"));
        profile.GetProperty("displayName").GetString().ShouldBe("Nome Original");
        ShouldHaveEditing(profile.GetProperty("editingPreferences"), "9:16", true, "bold", "bottom", 15, 60, 30, "auto");
    }

    private static Task SetRawPreferencesAsync(AppDbContext db, Guid userId, string editingJson, string notificationJson)
        => db.Database.ExecuteSqlAsync($"UPDATE user_profiles SET editing_preferences = {editingJson}::jsonb, notification_preferences = {notificationJson}::jsonb WHERE user_id = {userId}");

    [Fact]
    public async Task Documents_stored_before_the_contract_are_read_with_defaults_and_merged_safely()
    {
        var (client, userId, _) = await RegisterAsync();
        await WithDbAsync(db => SetRawPreferencesAsync(db, userId,
            """{"aspectRatio":"16:9","subtitleStyle":"clean","subtitlesEnabled":false,"targetClipSeconds":120,"language":"pt"}""",
            """{"emailOnJobCompleted":true,"emailOnJobFailed":false,"emailBilling":false,"inApp":true}"""));

        var profile = await ReadJsonAsync(await client.GetAsync("/api/account/profile"));
        // missing fields take their defaults; the stored target widens the range instead of being lost
        ShouldHaveEditing(profile.GetProperty("editingPreferences"), "16:9", false, "clean", "bottom", 15, 120, 120, "pt");
        // unknown legacy keys are ignored, missing ones take their defaults
        ShouldHaveNotifications(profile.GetProperty("notificationPreferences"), completed: true, failed: false, quota: true, billing: true);

        var merged = await UpdateProfileAsync(client, new { editingPreferences = new { minClipSeconds = 30 } });
        ShouldHaveEditing(merged.GetProperty("editingPreferences"), "16:9", false, "clean", "bottom", 30, 120, 120, "pt");

        // junk stored by older versions (no language validation) never reaches a new project
        await WithDbAsync(db => db.Database.ExecuteSqlAsync($"UPDATE user_profiles SET editing_preferences = jsonb_set(editing_preferences, '{{language}}', to_jsonb({new string('x', 50) + " ; junk"}::text)) WHERE user_id = {userId}"));
        (await ReadJsonAsync(await client.GetAsync("/api/account/profile"))).GetProperty("editingPreferences").GetProperty("language").GetString().ShouldBe("auto");
        var (projectId, _) = await CreateProjectAsync(client, settings: null);
        var project = await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"));
        project.GetProperty("project").GetProperty("settings").GetProperty("language").GetString().ShouldBe("auto");
    }

    [Fact]
    public async Task Migration_rewrites_legacy_notification_documents_to_the_contract_defaults()
    {
        var (_, legacyUser, _) = await RegisterAsync();
        var (modernClient, modernUser, _) = await RegisterAsync();
        await WithDbAsync(db => SetRawPreferencesAsync(db, legacyUser,
            """{"aspectRatio":"16:9"}""",
            """{"emailOnJobCompleted":true,"emailOnJobFailed":true,"emailBilling":true,"inApp":true}"""));
        await UpdateProfileAsync(modernClient, new { notificationPreferences = new { emailOnJobCompleted = true, emailOnBilling = false } });

        // run the data migration again over these rows (its Down is a no-op)
        await WithDbAsync(async db =>
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261023124808_ClipCurrentRenderForeignKey");
            await migrator.MigrateAsync();
        });

        var legacy = await WithDbAsync(db => db.UserProfiles.AsNoTracking().FirstAsync(p => p.UserId == legacyUser));
        legacy.NotificationPreferences.EmailOnJobCompleted.ShouldBeFalse(); // never a user choice: back to the default
        legacy.NotificationPreferences.EmailOnJobFailed.ShouldBeTrue();
        legacy.NotificationPreferences.EmailOnQuotaWarning.ShouldBeTrue();
        legacy.NotificationPreferences.EmailOnBilling.ShouldBeTrue();
        legacy.EditingPreferences.AspectRatio.ShouldBe("16:9"); // editing documents are untouched
        var modern = await WithDbAsync(db => db.UserProfiles.AsNoTracking().FirstAsync(p => p.UserId == modernUser));
        modern.NotificationPreferences.EmailOnJobCompleted.ShouldBeTrue();
        modern.NotificationPreferences.EmailOnBilling.ShouldBeFalse();
    }

    [Fact]
    public async Task New_projects_without_settings_start_from_the_editing_preferences_capped_by_the_plan()
    {
        var (client, userId, _) = await RegisterAsync();
        await UpdateProfileAsync(client, new
        {
            editingPreferences = new { aspectRatio = "16:9", subtitlesEnabled = false, subtitleStyle = "boxed", subtitlePosition = "top", minClipSeconds = 20, maxClipSeconds = 240, targetClipSeconds = 90, language = "es" },
        });

        var (projectId, jobId) = await CreateProjectAsync(client, settings: null);
        var settings = (await ReadJsonAsync(await client.GetAsync($"/api/projects/{projectId}"))).GetProperty("project").GetProperty("settings");
        settings.GetProperty("aspectRatio").GetString().ShouldBe("16:9");
        settings.GetProperty("subtitles").GetProperty("enabled").GetBoolean().ShouldBeFalse();
        settings.GetProperty("minClipSeconds").GetInt32().ShouldBe(20);
        settings.GetProperty("maxClipSeconds").GetInt32().ShouldBe(240);
        settings.GetProperty("targetClipSeconds").GetInt32().ShouldBe(90);
        settings.GetProperty("language").GetString().ShouldBe("es");
        // the free plan has no custom subtitle styles: the plan cap wins over the preference
        settings.GetProperty("subtitles").GetProperty("style").GetString().ShouldBe("bold");
        settings.GetProperty("subtitles").GetProperty("position").GetString().ShouldBe("bottom");
        await CompleteAnalyzeJobAsync(CreateInternalClient("worker-1"), jobId, 60);

        // on a plan with custom styles the preferred style and position are used
        await WithDbAsync(async db =>
        {
            var creator = await db.Plans.FirstAsync(p => p.Code == "creator");
            await db.Subscriptions.Where(s => s.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PlanId, creator.Id));
        });
        var (secondId, _) = await CreateProjectAsync(client, settings: null);
        var second = (await ReadJsonAsync(await client.GetAsync($"/api/projects/{secondId}"))).GetProperty("project").GetProperty("settings");
        second.GetProperty("subtitles").GetProperty("style").GetString().ShouldBe("boxed");
        second.GetProperty("subtitles").GetProperty("position").GetString().ShouldBe("top");

        // explicit settings still win over the preferences
        var assetId = await UploadAssetAsync(client);
        var explicitResponse = await client.PostAsJsonAsync("/api/projects", new
        {
            source = new { kind = "upload", mediaAssetId = assetId },
            settings = new { aspectRatio = "9:16", minClipSeconds = 15, maxClipSeconds = 60, targetClipSeconds = 30, language = "auto", subtitles = new { enabled = true, style = "bold", position = "bottom" }, framing = new { mode = "auto" } },
        }, Json);
        explicitResponse.StatusCode.ShouldBe(HttpStatusCode.Created, await explicitResponse.Content.ReadAsStringAsync());
        (await ReadJsonAsync(explicitResponse)).GetProperty("project").GetProperty("settings").GetProperty("aspectRatio").GetString().ShouldBe("9:16");
    }
}
