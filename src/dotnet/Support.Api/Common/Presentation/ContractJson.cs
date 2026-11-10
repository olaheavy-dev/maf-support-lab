using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Support.Api.Common.Presentation;

internal static class ContractJson
{
    /// <summary>Wire format: snake_case, UTC timestamps, unknown request fields rejected.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcTimestampConverter() },
    };

    /// <summary>Contract timestamps are RFC 3339 UTC with a Z suffix and whole seconds.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }
}
