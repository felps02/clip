namespace ClipForge.Domain.ValueObjects;

public static class AspectRatios
{
    public const string Vertical = "9:16";
    public const string Horizontal = "16:9";
    public const string Square = "1:1";
    public static readonly string[] All = [Vertical, Horizontal, Square];
    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}

public static class SubtitleStyles
{
    public const string Bold = "bold";
    public const string Clean = "clean";
    public const string Boxed = "boxed";
    public static readonly string[] All = [Bold, Clean, Boxed];

    public const string Bottom = "bottom";
    public const string Center = "center";
    public const string Top = "top";
    public static readonly string[] Positions = [Bottom, Center, Top];

    public static bool IsStyle(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
    public static bool IsPosition(string? value) => value is not null && Array.IndexOf(Positions, value) >= 0;
}

/// <summary>Categories of detected moments; <see cref="AnalysisSettings.FocusCategories"/> is a subset of them.</summary>
public static class MomentCategories
{
    public const string Highlight = "highlight";
    public const string Reaction = "reaction";
    public const string Funny = "funny";
    public const string Hype = "hype";
    public const string Tension = "tension";
    public const string Gameplay = "gameplay";
    public const string Quote = "quote";
    public static readonly string[] All = [Highlight, Reaction, Funny, Hype, Tension, Gameplay, Quote];
    public static bool IsValid(string? value) => value is not null && Array.IndexOf(All, value) >= 0;
}

public static class FramingModes
{
    public const string Auto = "auto";
    public const string Center = "center";
    public const string WebcamSplit = "webcam-split";
    public static readonly string[] All = [Auto, Center, WebcamSplit];
}

/// <summary>Subtitle rendering options (stored as jsonb on clips and inside analysis settings).</summary>
public class SubtitleSettings
{
    public bool Enabled { get; set; } = true;
    public string Style { get; set; } = SubtitleStyles.Bold;
    public string Position { get; set; } = "bottom";

    public SubtitleSettings Clone() => new() { Enabled = Enabled, Style = Style, Position = Position };

    public bool SameAs(SubtitleSettings? other)
        => other is not null && Enabled == other.Enabled
           && string.Equals(Style, other.Style, StringComparison.Ordinal)
           && string.Equals(Position, other.Position, StringComparison.Ordinal);
}

/// <summary>Normalised rectangle (0..1) where the streamer webcam was detected.</summary>
public class WebcamRegion
{
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }
    public double? Confidence { get; set; }

    public bool IsValid() => W > 0 && H > 0 && X >= 0 && Y >= 0 && X + W <= 1.0001 && Y + H <= 1.0001;

    public WebcamRegion Clone() => new() { X = X, Y = Y, W = W, H = H, Confidence = Confidence };
}

/// <summary>Framing options (stored as jsonb on clips and inside analysis settings).</summary>
public class FramingSettings
{
    public string Mode { get; set; } = FramingModes.Auto;
    public double? FocusX { get; set; }
    public double? FocusY { get; set; }
    public WebcamRegion? WebcamRegion { get; set; }

    public FramingSettings Clone() => new()
    {
        Mode = Mode,
        FocusX = FocusX,
        FocusY = FocusY,
        WebcamRegion = WebcamRegion?.Clone(),
    };

    public bool SameAs(FramingSettings? other)
    {
        if (other is null) return false;
        if (!string.Equals(Mode, other.Mode, StringComparison.Ordinal)) return false;
        if (FocusX != other.FocusX || FocusY != other.FocusY) return false;
        if (WebcamRegion is null != other.WebcamRegion is null) return false;
        if (WebcamRegion is not null && other.WebcamRegion is not null)
        {
            return WebcamRegion.X == other.WebcamRegion.X && WebcamRegion.Y == other.WebcamRegion.Y
                && WebcamRegion.W == other.WebcamRegion.W && WebcamRegion.H == other.WebcamRegion.H;
        }
        return true;
    }
}

/// <summary>Per-project analysis settings (projects.settings jsonb and analyze_source job input).</summary>
public class AnalysisSettings
{
    /// <summary>Bounds of min/max/target clip length (seconds), the same as the profile's editing preferences.</summary>
    public const int ClipSecondsMin = EditingPreferences.ClipSecondsMin;
    public const int ClipSecondsMax = EditingPreferences.ClipSecondsMax;
    public const int MaxCustomKeywords = 20;
    public const int CustomKeywordMaxLength = 40;
    public const int InstructionsMaxLength = 500;

    public int MinClipSeconds { get; set; } = 15;
    public int MaxClipSeconds { get; set; } = 60;
    public int TargetClipSeconds { get; set; } = 30;
    public int Sensitivity { get; set; } = 50;
    public int MaxCandidates { get; set; } = 20;
    /// <summary>
    /// How many of the best candidates the analysis renders. Null (not sent) = every candidate found
    /// (<see cref="MaxCandidates"/>); <see cref="Plans.PlanLimitHelpers.CapSettings"/> resolves it and caps it by the
    /// plan's maxAutoRenderPerJob, so stored settings and job payloads always carry a number.
    /// </summary>
    public int? AutoRenderTop { get; set; }
    public string AspectRatio { get; set; } = AspectRatios.Vertical;
    public SubtitleSettings Subtitles { get; set; } = new();
    public string Language { get; set; } = "auto";
    public FramingSettings Framing { get; set; } = new();
    public string? Title { get; set; }
    /// <summary>Categories the analysis favours (subset of <see cref="MomentCategories.All"/>); empty = no focus.</summary>
    public List<string> FocusCategories { get; set; } = [];
    /// <summary>Words matched in the transcript like the built-in lexicon (at most 20, 1-40 characters each).</summary>
    public List<string> CustomKeywords { get; set; } = [];
    /// <summary>What the user asked in their own words (at most 500 characters); only a worker with an LLM uses it.</summary>
    public string? Instructions { get; set; }

    public AnalysisSettings Clone() => new()
    {
        MinClipSeconds = MinClipSeconds,
        MaxClipSeconds = MaxClipSeconds,
        TargetClipSeconds = TargetClipSeconds,
        Sensitivity = Sensitivity,
        MaxCandidates = MaxCandidates,
        AutoRenderTop = AutoRenderTop,
        AspectRatio = AspectRatio,
        Subtitles = Subtitles?.Clone() ?? new SubtitleSettings(),
        Language = Language,
        Framing = Framing?.Clone() ?? new FramingSettings(),
        Title = Title,
        FocusCategories = FocusCategories is null ? [] : [.. FocusCategories],
        CustomKeywords = CustomKeywords is null ? [] : [.. CustomKeywords],
        Instructions = Instructions,
    };

    /// <summary>Known categories only (trimmed, lower-case), without duplicates, in the order given.</summary>
    public static List<string> NormalizeFocusCategories(IEnumerable<string?>? values)
    {
        var result = new List<string>();
        if (values is null) return result;
        foreach (var value in values)
        {
            var category = value?.Trim().ToLowerInvariant();
            if (MomentCategories.IsValid(category) && !result.Contains(category!)) result.Add(category!);
        }
        return result;
    }

    /// <summary>A keyword as stored: trimmed, inner whitespace collapsed to single spaces.</summary>
    public static string CleanKeyword(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>
    /// Cleaned keywords (<see cref="CleanKeyword"/>) without empty or over-long entries, deduplicated case-insensitively
    /// (the first spelling wins), at most <see cref="MaxCustomKeywords"/>.
    /// </summary>
    public static List<string> NormalizeCustomKeywords(IEnumerable<string?>? values)
    {
        var result = new List<string>();
        if (values is null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var keyword = CleanKeyword(value);
            if (keyword.Length is 0 or > CustomKeywordMaxLength || !seen.Add(keyword)) continue;
            result.Add(keyword);
            if (result.Count == MaxCustomKeywords) break;
        }
        return result;
    }

    /// <summary>Trimmed instructions (null when empty), cut to <see cref="InstructionsMaxLength"/> characters.</summary>
    public static string? NormalizeInstructions(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length > InstructionsMaxLength ? text[..InstructionsMaxLength].TrimEnd() : text;
    }
}
