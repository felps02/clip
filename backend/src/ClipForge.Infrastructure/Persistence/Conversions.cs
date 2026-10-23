using System.Text.Json;
using ClipForge.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ClipForge.Infrastructure.Persistence;

/// <summary>Stores enums as snake_case lower text (e.g. preparing_media).</summary>
public sealed class SnakeEnumConverter<TEnum> : ValueConverter<TEnum, string> where TEnum : struct, Enum
{
    public SnakeEnumConverter()
        : base(e => EnumNames.ToSnake(e), s => EnumNames.Parse<TEnum>(s))
    {
    }
}

public static class JsonColumn
{
    /// <summary>Maps a POCO property to a jsonb column using the shared System.Text.Json settings.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> builder)
    {
        var converter = new ValueConverter<T, string>(
            v => JsonSerializer.Serialize(v, JsonDefaults.Options),
            s => JsonSerializer.Deserialize<T>(s, JsonDefaults.Options)!);

        var comparer = new ValueComparer<T>(
            (a, b) => ReferenceEquals(a, b) || (a != null && b != null && JsonSerializer.Serialize(a, JsonDefaults.Options) == JsonSerializer.Serialize(b, JsonDefaults.Options)),
            v => v == null ? 0 : JsonSerializer.Serialize(v, JsonDefaults.Options).GetHashCode(),
            v => v == null ? v : JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, JsonDefaults.Options), JsonDefaults.Options)!);

        builder.HasConversion(converter, comparer);
        builder.HasColumnType("jsonb");
        return builder;
    }

    /// <summary>Maps a raw JSON string property to a jsonb column.</summary>
    public static PropertyBuilder<string?> HasRawJson(this PropertyBuilder<string?> builder)
    {
        builder.HasColumnType("jsonb");
        return builder;
    }

    public static PropertyBuilder<string> HasRawJsonRequired(this PropertyBuilder<string> builder)
    {
        builder.HasColumnType("jsonb");
        return builder;
    }
}
