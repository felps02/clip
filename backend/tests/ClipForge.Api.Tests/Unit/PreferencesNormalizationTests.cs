using ClipForge.Api.Features.Account;
using ClipForge.Domain.Common;
using ClipForge.Domain.ValueObjects;

namespace ClipForge.Api.Tests.Unit;

public class PreferencesNormalizationTests
{
    [Fact]
    public void Defaults_match_the_shared_contract()
    {
        var editing = new EditingPreferences();
        (editing.AspectRatio, editing.SubtitlesEnabled, editing.SubtitleStyle, editing.SubtitlePosition).ShouldBe(("9:16", true, "bold", "bottom"));
        (editing.MinClipSeconds, editing.MaxClipSeconds, editing.TargetClipSeconds, editing.Language).ShouldBe((15, 60, 30, "auto"));
        var notifications = new NotificationPreferences();
        (notifications.EmailOnJobCompleted, notifications.EmailOnJobFailed, notifications.EmailOnQuotaWarning, notifications.EmailOnBilling).ShouldBe((false, true, true, true));
    }

    [Fact]
    public void Older_documents_deserialize_with_defaults_for_missing_fields()
    {
        var editing = JsonDefaults.Deserialize<EditingPreferences>("""{"aspectRatio":"16:9","subtitleStyle":"clean","subtitlesEnabled":false,"targetClipSeconds":45,"language":"pt"}""")!;
        editing.SubtitlePosition.ShouldBe("bottom");
        editing.MinClipSeconds.ShouldBe(15);
        editing.MaxClipSeconds.ShouldBe(60);
        editing.TargetClipSeconds.ShouldBe(45);

        var notifications = JsonDefaults.Deserialize<NotificationPreferences>("""{"emailOnJobCompleted":true,"emailOnJobFailed":true,"emailBilling":false,"inApp":true}""")!;
        notifications.EmailOnQuotaWarning.ShouldBeTrue();
        notifications.EmailOnBilling.ShouldBeTrue();
    }

    [Fact]
    public void Normalized_repairs_out_of_contract_values()
    {
        var repaired = new EditingPreferences
        {
            AspectRatio = "4:3", SubtitleStyle = "neon", SubtitlePosition = "left", Language = "PT ",
            MinClipSeconds = 1, MaxClipSeconds = 999, TargetClipSeconds = 500,
        }.Normalized();
        (repaired.AspectRatio, repaired.SubtitleStyle, repaired.SubtitlePosition, repaired.Language).ShouldBe(("9:16", "bold", "bottom", "pt"));
        (repaired.MinClipSeconds, repaired.MaxClipSeconds, repaired.TargetClipSeconds).ShouldBe((5, 300, 300));

        // a target stored without min/max (older documents) widens the range instead of being lost
        new EditingPreferences { TargetClipSeconds = 120 }.Normalized().MaxClipSeconds.ShouldBe(120);
        new EditingPreferences { TargetClipSeconds = 10 }.Normalized().MinClipSeconds.ShouldBe(10);
        var inverted = new EditingPreferences { MinClipSeconds = 100, MaxClipSeconds = 50, TargetClipSeconds = 70 }.Normalized();
        (inverted.MinClipSeconds <= inverted.TargetClipSeconds && inverted.TargetClipSeconds <= inverted.MaxClipSeconds).ShouldBeTrue();
        new EditingPreferences { Language = new string('x', 600) }.Normalized().Language.ShouldBe("auto");

        // valid documents come back unchanged
        var valid = new EditingPreferences { AspectRatio = "1:1", SubtitleStyle = "boxed", SubtitlePosition = "center", MinClipSeconds = 20, MaxClipSeconds = 90, TargetClipSeconds = 45, Language = "es" };
        JsonDefaults.Serialize(valid.Normalized()).ShouldBe(JsonDefaults.Serialize(valid));
    }

    [Fact]
    public void Merge_keeps_omitted_fields_and_validate_checks_the_relations()
    {
        var stored = new EditingPreferences { MinClipSeconds = 20, MaxClipSeconds = 90, TargetClipSeconds = 45, SubtitlePosition = "top" };
        var merged = ProfilePreferences.Merge(stored, new EditingPreferencesPatch("16:9", null, null, null, null, null, null, "EN"));
        (merged.AspectRatio, merged.SubtitlePosition, merged.MinClipSeconds, merged.MaxClipSeconds, merged.TargetClipSeconds, merged.Language).ShouldBe(("16:9", "top", 20, 90, 45, "en"));
        ProfilePreferences.Validate(merged).ShouldBeEmpty();
        stored.AspectRatio.ShouldBe("9:16"); // the stored document is not mutated

        var invalid = ProfilePreferences.Merge(stored, new EditingPreferencesPatch(null, null, null, null, null, 30, null, null));
        ProfilePreferences.Validate(invalid).Keys.ShouldBe(["editingPreferences.targetClipSeconds"]);
        var inverted = ProfilePreferences.Merge(stored, new EditingPreferencesPatch(null, null, null, null, 100, 50, 70, null));
        ProfilePreferences.Validate(inverted).Keys.ShouldContain("editingPreferences.maxClipSeconds");

        var notifications = ProfilePreferences.Merge(new NotificationPreferences { EmailOnJobCompleted = true }, new NotificationPreferencesPatch(null, false, null, null));
        (notifications.EmailOnJobCompleted, notifications.EmailOnJobFailed, notifications.EmailOnQuotaWarning, notifications.EmailOnBilling).ShouldBe((true, false, true, true));
    }
}
