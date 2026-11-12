using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Support.Evaluations;

/// <summary>
/// The evaluation schemas (evaluations/schemas): datasets in, run results out. Both are versioned; anything that
/// doesn't match is rejected before a case runs or a result is written.
/// </summary>
internal static class EvaluationSchemas
{
    public const string Dataset = "evaluation-dataset";
    public const string Run = "evaluation-run";
    public const string Quality = "evaluation-quality";
    public const string LoadRun = "evaluation-load";

    private static readonly Lazy<Dictionary<string, JsonSchema>> Loaded = new(Load);

    /// <summary>The schema violations in <paramref name="document"/> (empty when valid).</summary>
    public static IReadOnlyList<string> Errors(string schema, JsonElement document)
    {
        var result = Loaded.Value[schema].Evaluate(document, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid)
        {
            return [];
        }

        return [.. (result.Details ?? [])
            .Where(detail => detail.Errors is { Count: > 0 })
            .SelectMany(detail => detail.Errors!.Select(error => $"{detail.InstanceLocation}: {error.Value}"))
            .Take(20)];
    }

    public static IReadOnlyList<string> Errors(string schema, JsonNode? document) =>
        Errors(schema, JsonSerializer.SerializeToElement(document));

    private static Dictionary<string, JsonSchema> Load()
    {
        var directory = Path.Combine(RepositoryRoot.Find(), "evaluations", "schemas");
        return new Dictionary<string, JsonSchema>(StringComparer.Ordinal)
        {
            [Dataset] = JsonSchema.FromFile(Path.Combine(directory, $"{Dataset}.schema.json")),
            [Run] = JsonSchema.FromFile(Path.Combine(directory, $"{Run}.schema.json")),
            [Quality] = JsonSchema.FromFile(Path.Combine(directory, $"{Quality}.schema.json")),
            [LoadRun] = JsonSchema.FromFile(Path.Combine(directory, $"{LoadRun}.schema.json")),
        };
    }
}

/// <summary>Finds the repository root (the folder holding maf-support-lab.slnx), from the working directory or the binary.</summary>
internal static class RepositoryRoot
{
    public static string Find()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "maf-support-lab.slnx")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new InvalidOperationException("Run from inside the repository: maf-support-lab.slnx was not found above the working directory.");
    }
}
