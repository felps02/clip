namespace ClipForge.Domain.Media;

/// <summary>Container detected from the first bytes of a file.</summary>
public sealed record DetectedContainer(string Container, string ContentType, bool IsVideo);

/// <summary>
/// Magic-byte validation of uploaded sources (docs/API.md "Accepted containers"). Only the first 64 bytes
/// are needed. The API never runs ffprobe (ADR-003); codec/duration checks happen when the worker probes.
/// </summary>
public static class MediaMagic
{
    public const int HeaderLength = 64;

    public static readonly string[] AllowedContentTypes =
    [
        "video/mp4", "video/quicktime", "video/x-matroska", "video/webm",
        "audio/mpeg", "audio/wav", "audio/x-wav", "audio/wave", "application/octet-stream"
    ];

    public static bool IsAllowedContentType(string? contentType)
        => contentType is not null && Array.IndexOf(AllowedContentTypes, contentType.Trim().ToLowerInvariant()) >= 0;

    /// <summary>Returns the detected container or null when the header is not a known media container.</summary>
    public static DetectedContainer? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 12 && header[4] == (byte)'f' && header[5] == (byte)'t' && header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            var brand = System.Text.Encoding.ASCII.GetString(header.Slice(8, 4)).ToLowerInvariant();
            return brand.StartsWith("qt") ? new DetectedContainer("mov", "video/quicktime", true)
                : new DetectedContainer("mp4", "video/mp4", true);
        }

        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            // EBML: look for the DocType string ("webm" or "matroska") inside the header bytes.
            var text = System.Text.Encoding.ASCII.GetString(header);
            if (text.Contains("webm", StringComparison.OrdinalIgnoreCase)) return new DetectedContainer("webm", "video/webm", true);
            return new DetectedContainer("mkv", "video/x-matroska", true);
        }

        if (header.Length >= 12 && header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F'
            && header[8] == (byte)'W' && header[9] == (byte)'A' && header[10] == (byte)'V' && header[11] == (byte)'E')
        {
            return new DetectedContainer("wav", "audio/wav", false);
        }

        if (header.Length >= 3 && header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3')
        {
            return new DetectedContainer("mp3", "audio/mpeg", false);
        }

        // MPEG audio frame sync: 11 set bits (0xFF 0xE0..0xFF), MPEG version != reserved, layer != reserved.
        if (header.Length >= 4 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0
            && (header[1] & 0x18) != 0x08 && (header[1] & 0x06) != 0x00 && (header[2] & 0xF0) != 0xF0)
        {
            return new DetectedContainer("mp3", "audio/mpeg", false);
        }

        return null;
    }

    private static readonly Dictionary<string, string> ExtensionContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".vtt"] = "text/vtt",
        [".srt"] = "application/x-subrip",
    };

    public static string ContentTypeForExtension(string? extension)
        => extension is not null && ExtensionContentTypes.TryGetValue(extension, out var ct) ? ct : "application/octet-stream";

    public static string ContentTypeForKey(string key) => ContentTypeForExtension(Path.GetExtension(key));

    /// <summary>Returns a safe lower-case extension (including the dot) for a source file name, or ".bin".</summary>
    public static string SafeExtension(string? fileName, string? contentType)
    {
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (ext.Length is > 1 and <= 8 && ext.Skip(1).All(c => char.IsAsciiLetterOrDigit(c)))
        {
            return ext;
        }
        return (contentType?.ToLowerInvariant()) switch
        {
            "video/mp4" => ".mp4",
            "video/quicktime" => ".mov",
            "video/x-matroska" => ".mkv",
            "video/webm" => ".webm",
            "audio/mpeg" => ".mp3",
            "audio/wav" or "audio/x-wav" or "audio/wave" => ".wav",
            _ => ".bin",
        };
    }
}
