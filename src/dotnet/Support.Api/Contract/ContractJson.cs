using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Support.Api.Contract;

internal static class ContractJson
{
    /// <summary>Wire format: snake_case, UTC timestamps, unknown request fields rejected.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new UtcTimestampConverter() },
    };

    public static string TraceId() =>
        (Activity.Current?.TraceId ?? ActivityTraceId.CreateRandom()).ToHexString();

    public static IResult Problem(int status, string code, string title, string? detail, IReadOnlyList<ProblemField>? errors = null) =>
        Results.Json(
            new ContractProblem(ContractVersion.Current, $"https://contracts.maf-support-lab.example/v1/errors/{code}", title, status, code, detail, TraceId(), errors),
            Options,
            contentType: "application/problem+json",
            statusCode: status);

    public static IResult NotFound() =>
        Problem(StatusCodes.Status404NotFound, "not_found", "Not found", "The resource does not exist or is not available to you.");

    /// <summary>Contract timestamps are RFC 3339 UTC with a Z suffix and whole seconds.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }
}
