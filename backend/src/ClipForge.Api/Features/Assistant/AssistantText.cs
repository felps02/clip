using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ClipForge.Api.Features.Assistant;

/// <summary>
/// A user message prepared for matching: <see cref="Original"/> and <see cref="Folded"/> (lower-case, accents removed)
/// have the same length, so a match in the folded text maps back to the user's own spelling.
/// </summary>
public sealed class FoldedText
{
    // The API runs with invariant globalization (string.Normalize is a no-op there), so accents are folded with a table.
    private const string Accented = "àáâãäåāçćčèéêëēěìíîïīñńòóôõöøōùúûüūýÿÀÁÂÃÄÅĀÇĆČÈÉÊËĒĚÌÍÎÏĪÑŃÒÓÔÕÖØŌÙÚÛÜŪÝŸ";
    private const string Plain = "aaaaaaaccceeeeeeiiiiinnooooooouuuuuyyaaaaaaaccceeeeeeiiiiinnooooooouuuuuyy";

    public FoldedText(string? text)
    {
        Original = text ?? string.Empty;
        Folded = Fold(Original);
    }

    public string Original { get; }

    public string Folded { get; }

    public bool Has(Regex pattern) => pattern.IsMatch(Folded);

    /// <summary>The original spelling of a span of the folded text.</summary>
    public string OriginalSpan(int index, int length) => Original.Substring(index, length);

    /// <summary>Lower-case, without accents: one character in, one character out.</summary>
    public static char FoldChar(char ch)
    {
        var lower = char.ToLowerInvariant(ch);
        if (lower < 128) return lower;
        var index = Accented.IndexOf(lower);
        return index >= 0 ? Plain[index] : lower;
    }

    public static string Fold(string? text)
    {
        var source = text ?? string.Empty;
        var folded = new StringBuilder(source.Length);
        foreach (var ch in source) folded.Append(FoldChar(ch));
        return folded.ToString();
    }
}

/// <summary>pt-BR text helpers of the composed replies.</summary>
public static class PtText
{
    /// <summary>"a", "a e b", "a, b e c".</summary>
    public static string JoinList(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " e " + items[^1],
    };

    public static string Quote(string text) => "“" + text + "”";

    public static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>Seconds as pt-BR text: 12.7 → "12,7" (invariant globalization: the comma is put in by hand).</summary>
    public static string Seconds(decimal seconds)
        => Math.Round(seconds, 1).ToString("0.#", CultureInfo.InvariantCulture).Replace('.', ',');
}

/// <summary>Regex factory for the rule layer (folded text, invariant culture, bounded matching time).</summary>
internal static class Rx
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);

    public static Regex Create(string pattern) => new(pattern, RegexOptions.CultureInvariant, Timeout);
}
