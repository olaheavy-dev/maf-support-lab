using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Support.Evaluations;

/// <summary>Who ran what, against which backend and configuration: everything needed to reproduce a result.</summary>
internal sealed record RunIdentity(string SourceCommit, string ModelMode, string? ModelId);

/// <summary>Runs one dataset against one backend and builds the normalized run document (evaluation-run schema).</summary>
internal static class EvaluationRun
{
    public const string RunnerName = "support-evaluations";
    public const string RunnerVersion = "0.2.0";
    public const string ScorerName = "contract-facts";
    public const string ScorerVersion = "1.0.0";

    public static async Task<JsonObject> RunAsync(Dataset dataset, CaseRunner runner, RunOptions options, RunIdentity identity, IReadOnlyDictionary<string, string> configuration, TimeProvider clock, CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        JsonArray results = [];
        foreach (var item in dataset.Cases)
        {
            results.Add(await runner.RunAsync(item, cancellationToken).ConfigureAwait(false));
        }

        int Count(string status) => results.Count(result => result!["status"]!.GetValue<string>() == status);
        var run = new JsonObject
        {
            ["schema_version"] = "1.1",
            ["run"] = new JsonObject
            {
                ["run_id"] = "run_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["started_at"] = Timestamp(started),
                ["finished_at"] = Timestamp(clock.GetUtcNow()),
                ["backend"] = options.Backend,
                ["source_commit"] = identity.SourceCommit,
                ["model"] = new JsonObject { ["mode"] = identity.ModelMode, ["model_id"] = identity.ModelId },
                ["dataset"] = new JsonObject
                {
                    ["dataset_id"] = dataset.DatasetId,
                    ["dataset_version"] = dataset.DatasetVersion,
                    ["splits"] = new JsonArray([.. options.Splits.Order(StringComparer.Ordinal).Select(split => (JsonNode?)split)]),
                },
                ["scorer"] = new JsonObject { ["name"] = ScorerName, ["version"] = ScorerVersion },
                ["runner"] = new JsonObject { ["name"] = RunnerName, ["version"] = RunnerVersion },
                ["configuration"] = Configuration(configuration, dataset),
            },
            ["results"] = results,
            ["summary"] = new JsonObject
            {
                ["total"] = results.Count,
                ["passed"] = Count("passed"),
                ["failed"] = Count("failed"),
                ["error"] = Count("error"),
                ["skipped"] = Count("skipped"),
            },
        };

        // The runner checks its own output: a result that doesn't match the schema is a runner bug, not a finding.
        var problems = EvaluationSchemas.Errors(EvaluationSchemas.Run, run);
        return problems.Count == 0 ? run : throw new InvalidOperationException($"The run document breaks its schema:\n  {string.Join("\n  ", problems)}");
    }

    private static JsonObject Configuration(IReadOnlyDictionary<string, string> values, Dataset dataset)
    {
        var json = new JsonObject { ["fixture_version"] = dataset.FixtureVersion };
        foreach (var (key, value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            json[key] = value;
        }

        return json;
    }

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
