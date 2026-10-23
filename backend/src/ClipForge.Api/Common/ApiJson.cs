using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClipForge.Api.Common;

/// <summary>JSON settings of the HTTP API: camelCase, snake_case enum strings, ISO-8601 dates.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    public static void Apply(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        target.DictionaryKeyPolicy = null;
        target.PropertyNameCaseInsensitive = true;
        target.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        target.NumberHandling = JsonNumberHandling.AllowReadingFromString;
        target.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
    }
}
