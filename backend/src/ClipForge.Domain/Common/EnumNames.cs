using System.Collections.Concurrent;
using System.Text.Json;

namespace ClipForge.Domain.Common;

/// <summary>
/// Converts enum members to their snake_case lower representation (PreparingMedia → preparing_media)
/// and back. Used for database storage and API serialization so both agree.
/// </summary>
public static class EnumNames
{
    private static readonly ConcurrentDictionary<Type, Dictionary<string, object>> ParseCache = new();

    public static string ToSnake<TEnum>(TEnum value) where TEnum : struct, Enum
        => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static string ToSnake(Enum value)
        => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    public static TEnum Parse<TEnum>(string text) where TEnum : struct, Enum
    {
        if (TryParse<TEnum>(text, out var value)) return value;
        throw new ArgumentException($"'{text}' is not a valid {typeof(TEnum).Name} value.");
    }

    public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var map = ParseCache.GetOrAdd(typeof(TEnum), t =>
        {
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in Enum.GetValues(t))
            {
                var e = (Enum)v;
                dict[ToSnake(e)] = v;
                dict[e.ToString()] = v;
            }
            return dict;
        });
        if (map.TryGetValue(text.Trim(), out var boxed))
        {
            value = (TEnum)boxed;
            return true;
        }
        return false;
    }
}
