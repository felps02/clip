namespace ClipForge.Domain.ValueObjects;

/// <summary>
/// user_profiles.editing_preferences: the defaults of new projects (docs/API.md → Account, shared contract with the
/// web). Documents stored before a field existed are read with that field at its default; <see cref="Normalized"/>
/// restores the invariants (allowed values, 5 ≤ min ≤ target ≤ max ≤ 300) for such documents.
/// </summary>
public class EditingPreferences
{
    public const int ClipSecondsMin = 5;
    public const int ClipSecondsMax = 300;

    /// <summary>Languages spoken in the recordings ("auto" = detected per recording). Not the interface language.</summary>
    public static readonly string[] Languages = ["auto", "pt", "en", "es"];

    public string AspectRatio { get; set; } = AspectRatios.Vertical;
    public bool SubtitlesEnabled { get; set; } = true;
    public string SubtitleStyle { get; set; } = SubtitleStyles.Bold;
    public string SubtitlePosition { get; set; } = SubtitleStyles.Bottom;
    public int MinClipSeconds { get; set; } = 15;
    public int MaxClipSeconds { get; set; } = 60;
    public int TargetClipSeconds { get; set; } = 30;
    public string Language { get; set; } = "auto";

    public static bool IsLanguage(string? value) => value is not null && Array.IndexOf(Languages, value) >= 0;

    public EditingPreferences Clone() => (EditingPreferences)MemberwiseClone();

    /// <summary>
    /// Copy with every field inside the contract. Values written through the API are already valid and come back
    /// unchanged; older documents (e.g. a target length stored without min/max) are repaired: unknown enum values fall
    /// back to the defaults and min/max widen to keep a stored target.
    /// </summary>
    public EditingPreferences Normalized()
    {
        var copy = Clone();
        if (!AspectRatios.IsValid(copy.AspectRatio)) copy.AspectRatio = AspectRatios.Vertical;
        if (!SubtitleStyles.IsStyle(copy.SubtitleStyle)) copy.SubtitleStyle = SubtitleStyles.Bold;
        if (!SubtitleStyles.IsPosition(copy.SubtitlePosition)) copy.SubtitlePosition = SubtitleStyles.Bottom;
        var language = copy.Language?.Trim().ToLowerInvariant();
        copy.Language = IsLanguage(language) ? language! : "auto";

        copy.MinClipSeconds = Math.Clamp(copy.MinClipSeconds, ClipSecondsMin, ClipSecondsMax);
        copy.MaxClipSeconds = Math.Clamp(copy.MaxClipSeconds, ClipSecondsMin, ClipSecondsMax);
        copy.TargetClipSeconds = Math.Clamp(copy.TargetClipSeconds, ClipSecondsMin, ClipSecondsMax);
        if (copy.TargetClipSeconds < copy.MinClipSeconds) copy.MinClipSeconds = copy.TargetClipSeconds;
        if (copy.TargetClipSeconds > copy.MaxClipSeconds) copy.MaxClipSeconds = copy.TargetClipSeconds;
        if (copy.MaxClipSeconds < copy.MinClipSeconds) copy.MaxClipSeconds = copy.MinClipSeconds;
        return copy;
    }
}

/// <summary>
/// user_profiles.notification_preferences: e-mail delivery per topic. In-app notifications are always created and
/// cannot be turned off. Documents written before this contract (keys emailBilling / inApp) are rewritten to these
/// defaults by the 20261023120000_NotificationPreferencesContract migration.
/// </summary>
public class NotificationPreferences
{
    public bool EmailOnJobCompleted { get; set; }
    public bool EmailOnJobFailed { get; set; } = true;
    public bool EmailOnQuotaWarning { get; set; } = true;
    public bool EmailOnBilling { get; set; } = true;

    public NotificationPreferences Clone() => (NotificationPreferences)MemberwiseClone();
}
