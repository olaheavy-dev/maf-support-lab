using System.Text.Json;
using System.Text.Json.Serialization;

namespace Support.Evaluations;

/// <summary>A versioned evaluation dataset (evaluations/datasets/*.json), validated against its schema when loaded.</summary>
internal sealed record Dataset(
    string SchemaVersion,
    string DatasetId,
    string DatasetVersion,
    string Category,
    string Description,
    string FixtureVersion,
    LiveRun LiveRun,
    IReadOnlyList<EvaluationCase> Cases);

internal sealed record LiveRun(bool Eligible, int MaxCases, int MaxModelCallsPerCase, int Repetitions);

internal sealed record EvaluationCase(
    string CaseId,
    string Split,
    string Principal,
    string Purpose,
    string Source,
    bool Writes,
    string Reset,
    IReadOnlyList<string>? Context,
    IReadOnlyList<CaseStep> Steps,
    StateChange ExpectedStateChange);

internal sealed record CaseStep(
    string Action,
    string? Message,
    string? Decision,
    JsonElement? RawBody,
    string? AsPrincipal,
    Expectation Expect);

internal sealed record Expectation(
    int? HttpStatus,
    string? ProblemCode,
    string? Outcome,
    string? ErrorCode,
    string? RouteStatus,
    string? RouteTarget,
    string? UnroutedReason,
    string? ReplyKind,
    string? OperationKind,
    string? OperationStatus,
    IReadOnlyList<string>? Tools,
    IReadOnlyList<string>? ToolsAbsent,
    IReadOnlyList<string>? Mentions,
    IReadOnlyList<string>? MustNotMention,
    bool? SameTurnAsPrevious,
    bool? SameOperationAsPrevious);

internal sealed record StateChange(string Type, string? OrderId);

internal static class EvaluationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };
}

internal static class DatasetLoader
{
    /// <summary>Loads and validates one dataset. Throws <see cref="InvalidDataException"/> listing every problem.</summary>
    public static Dataset Load(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var errors = Problems(document.RootElement);
        if (errors.Count > 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not a valid evaluation dataset:\n  {string.Join("\n  ", errors)}");
        }

        return document.RootElement.Deserialize<Dataset>(EvaluationJson.Options)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
    }

    /// <summary>Schema violations plus the rules a schema can't express (case ids unique within the dataset).</summary>
    public static IReadOnlyList<string> Problems(JsonElement dataset)
    {
        List<string> problems = [.. EvaluationSchemas.Errors(EvaluationSchemas.Dataset, dataset)];
        if (problems.Count == 0)
        {
            var duplicates = dataset.GetProperty("cases").EnumerateArray()
                .GroupBy(item => item.GetProperty("case_id").GetString())
                .Where(group => group.Count() > 1)
                .Select(group => $"case_id '{group.Key}' is used more than once");
            problems.AddRange(duplicates);
        }

        return problems;
    }

    public static IReadOnlyList<string> AllPaths() =>
        [.. Directory.GetFiles(Path.Combine(RepositoryRoot.Find(), "evaluations", "datasets"), "*.json").Order(StringComparer.Ordinal)];
}
