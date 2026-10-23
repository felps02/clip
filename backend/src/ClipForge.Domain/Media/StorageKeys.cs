using System.Text;
using System.Text.RegularExpressions;

namespace ClipForge.Domain.Media;

/// <summary>Builds and validates object storage keys. Keys live under users/{userId}/… and never contain "..".</summary>
public static partial class StorageKeys
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._\-/]*$")]
    private static partial Regex ValidKeyRegex();

    [GeneratedRegex(@"[^A-Za-z0-9._\-]+")]
    private static partial Regex UnsafeFileNameChars();

    public static string UserPrefix(Guid userId) => $"users/{userId:D}/";

    public static string SourceKey(Guid userId, Guid assetId, string extension)
        => $"users/{userId:D}/sources/{assetId:D}/original{extension}";

    public static string JobArtifactPrefix(Guid userId, Guid jobId) => $"users/{userId:D}/jobs/{jobId:D}/";

    public static string JobArtifactKey(Guid userId, Guid jobId, string kind, string fileName)
        => $"{JobArtifactPrefix(userId, jobId)}{kind}/{SanitizeFileName(fileName)}";

    /// <summary>True when the key is well formed: no empty segments, no "..", no leading slash, safe characters only.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 512) return false;
        if (!ValidKeyRegex().IsMatch(key)) return false;
        if (key.Contains("..", StringComparison.Ordinal)) return false;
        if (key.Contains("//", StringComparison.Ordinal)) return false;
        if (key.EndsWith('/')) return false;
        return true;
    }

    public static bool BelongsToUser(string? key, Guid userId)
        => IsValid(key) && key!.StartsWith(UserPrefix(userId), StringComparison.Ordinal);

    public static void EnsureValid(string? key)
    {
        if (!IsValid(key)) throw new Common.AppException(Common.ErrorCodes.ValidationFailed, 400, "Chave de armazenamento inválida.");
    }

    public static void EnsureOwned(string? key, Guid userId)
    {
        if (!BelongsToUser(key, userId)) throw new Common.AppException(Common.ErrorCodes.Forbidden, 403, "Chave de armazenamento fora do espaço do usuário.");
    }

    /// <summary>Keeps only safe characters of a file name (no path separators).</summary>
    public static string SanitizeFileName(string? fileName, string fallback = "file")
    {
        if (string.IsNullOrWhiteSpace(fileName)) return fallback;
        var name = fileName.Replace('\\', '/');
        var slash = name.LastIndexOf('/');
        if (slash >= 0) name = name[(slash + 1)..];
        name = UnsafeFileNameChars().Replace(name, "_").Trim('.', '_');
        if (name.Length == 0) return fallback;
        if (name.Length > 120) name = name[^120..];
        return name;
    }

    private static readonly Dictionary<char, string> Transliterations = new()
    {
        ['à'] = "a", ['á'] = "a", ['â'] = "a", ['ã'] = "a", ['ä'] = "a", ['å'] = "a", ['æ'] = "ae",
        ['ç'] = "c", ['è'] = "e", ['é'] = "e", ['ê'] = "e", ['ë'] = "e", ['ì'] = "i", ['í'] = "i", ['î'] = "i", ['ï'] = "i",
        ['ñ'] = "n", ['ò'] = "o", ['ó'] = "o", ['ô'] = "o", ['õ'] = "o", ['ö'] = "o", ['ø'] = "o", ['œ'] = "oe",
        ['ù'] = "u", ['ú'] = "u", ['û'] = "u", ['ü'] = "u", ['ý'] = "y", ['ÿ'] = "y", ['ß'] = "ss",
    };

    /// <summary>Builds a download file name from a clip title: ASCII letters/digits/dash, lower-case, ".mp4".</summary>
    public static string DownloadFileName(string? title, string extension = ".mp4")
    {
        var sb = new StringBuilder();
        foreach (var raw in title ?? string.Empty)
        {
            var ch = char.ToLowerInvariant(raw);
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (Transliterations.TryGetValue(ch, out var replacement)) sb.Append(replacement);
            else if (ch is ' ' or '-' or '_' or '.') sb.Append('-');
            else if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
        }
        var slug = Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
        if (slug.Length == 0) slug = "clip";
        if (slug.Length > 80) slug = slug[..80].TrimEnd('-');
        return slug + extension;
    }
}
