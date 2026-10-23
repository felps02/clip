using ClipForge.Domain.Enums;

namespace ClipForge.Domain.Media;

/// <summary>Identifies the streaming platform of a pasted link (ADR-008). Never scrapes anything.</summary>
public static class PlatformDetector
{
    public static DetectedPlatform Detect(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return DetectedPlatform.Unknown;
        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return DetectedPlatform.Unknown;
        if (uri.Scheme is not ("http" or "https")) return DetectedPlatform.Unknown;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];

        if (host == "twitch.tv" || host.EndsWith(".twitch.tv", StringComparison.Ordinal)) return DetectedPlatform.Twitch;
        if (host == "youtube.com" || host.EndsWith(".youtube.com", StringComparison.Ordinal) || host == "youtu.be"
            || host == "youtube-nocookie.com" || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal))
        {
            return DetectedPlatform.Youtube;
        }
        if (host == "kick.com" || host.EndsWith(".kick.com", StringComparison.Ordinal)) return DetectedPlatform.Kick;

        return DetectedPlatform.Unknown;
    }

    public static SourcePlatform ToSourcePlatform(DetectedPlatform platform) => platform switch
    {
        DetectedPlatform.Twitch => SourcePlatform.Twitch,
        DetectedPlatform.Youtube => SourcePlatform.Youtube,
        DetectedPlatform.Kick => SourcePlatform.Kick,
        _ => SourcePlatform.Other,
    };
}
