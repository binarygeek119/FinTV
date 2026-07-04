using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.FinTV.Domain;

/// <summary>
/// Shared System.Text.Json settings for FinTV (case-insensitive property names).
/// </summary>
public static class FinTvJson
{
    /// <summary>
    /// Default serializer options for FinTV JSON read/write.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);

    public static T? Deserialize<T>(ReadOnlySpan<char> json)
        => JsonSerializer.Deserialize<T>(json, Options);

    public static object? Deserialize(string json, Type returnType)
        => JsonSerializer.Deserialize(json, returnType, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }
}
