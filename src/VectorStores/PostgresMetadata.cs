using System.Text.Json;
using System.Text.Json.Serialization;
using Fynydd.Umbraco.Search.Postgres.Extensions;

namespace Fynydd.Umbraco.Search.Postgres.VectorStores;

internal static class PostgresMetadata
{
    private static readonly JsonSerializerOptions ReadOptions = new() { Converters = { new ObjectConverter() } };

    public static string Create(string documentId, string? variationKey, int chunkIndex, IDictionary<string, object>? metadata)
    {
        var values = metadata is null ? new Dictionary<string, object>() : new Dictionary<string, object>(metadata);
        var (culture, segment) = AiVariationKey.Parse(variationKey);
        values["documentId"] = documentId;
        values["chunkIndex"] = chunkIndex;
        values.Remove("culture");
        values.Remove("segment");

        if (culture is not null)
            values["culture"] = culture;

        if (segment is not null)
            values["segment"] = segment;

        return JsonSerializer.Serialize(values);
    }

    public static Dictionary<string, object> Read(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, object>>(json, ReadOptions) ?? [];

    private sealed class ObjectConverter : JsonConverter<object>
    {
        public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => reader.TryGetInt64(out var integer) ? (object)integer : reader.GetDouble(),
            JsonTokenType.StartObject => JsonSerializer.Deserialize<Dictionary<string, object>>(ref reader, options),
            JsonTokenType.StartArray => JsonSerializer.Deserialize<List<object>>(ref reader, options),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Unsupported metadata token.")
        };

        public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options) =>
            JsonSerializer.Serialize(writer, value, value.GetType());
    }
}
