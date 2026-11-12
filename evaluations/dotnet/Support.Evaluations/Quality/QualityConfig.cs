using System.Text.Json;

namespace Support.Evaluations.Quality;

/// <summary>
/// The quality lane's configuration (evaluations/config/quality.json, L32): the judge, the backends' live model, the
/// metrics, which dataset cases are sampled, the call budget, and the calibration examples. Everything that affects a
/// score is pinned here and copied into the result, so two runs can be compared.
/// </summary>
internal sealed record QualityConfig(
    string SchemaVersion,
    JudgeSettings Judge,
    BackendSettings Backends,
    IReadOnlyList<string> Metrics,
    IReadOnlyList<QualityCaseRef> Cases,
    BudgetSettings Budget,
    IReadOnlyList<CalibrationExample> Calibration)
{
    public static readonly string[] KnownMetrics = ["relevance", "groundedness"];

    public static string DefaultPath() => Path.Combine(RepositoryRoot.Find(), "evaluations", "config", "quality.json");

    /// <summary>Loads and checks the configuration. Throws <see cref="InvalidDataException"/> listing every problem.</summary>
    public static QualityConfig Load(string path)
    {
        var config = JsonSerializer.Deserialize<QualityConfig>(File.ReadAllText(path), EvaluationJson.Options)
            ?? throw new InvalidDataException($"{Path.GetFileName(path)} is empty.");
        var problems = config.Problems();
        return problems.Count == 0
            ? config
            : throw new InvalidDataException($"{Path.GetFileName(path)} is not a valid quality configuration:\n  {string.Join("\n  ", problems)}");
    }

    public IReadOnlyList<string> Problems()
    {
        List<string> problems = [];
        void Require(bool condition, string problem)
        {
            if (!condition)
            {
                problems.Add(problem);
            }
        }

        Require(SchemaVersion == "1.0", "schema_version must be 1.0");
        Require(Judge is { Provider: "anthropic" } && !string.IsNullOrWhiteSpace(Judge.Model), "judge needs provider anthropic and a model");
        Require(Judge is null || Judge.MaxOutputTokens is >= 256 and <= 4096, "judge.max_output_tokens must be 256-4096");
        Require(Backends is { Names.Count: > 0 } && Backends.Names.All(name => name is "dotnet" or "python") && Backends.Names.Distinct().Count() == Backends.Names.Count, "backends.names must be dotnet and/or python, once each");
        Require(Backends is null || !string.IsNullOrWhiteSpace(Backends.Model), "backends.model is required");
        Require(Backends is null || Backends.MaxModelCallsPerTurn is >= 1 and <= 10, "backends.max_model_calls_per_turn must be 1-10");
        Require(Metrics is { Count: > 0 } && Metrics.All(KnownMetrics.Contains) && Metrics.Distinct().Count() == Metrics.Count, $"metrics must be a non-empty subset of {string.Join(", ", KnownMetrics)}");
        Require(Cases is { Count: > 0 } && Cases.Select(c => (c.Dataset, c.CaseId)).Distinct().Count() == Cases.Count, "cases must be non-empty and unique");
        Require(Budget is { MaxModelCalls: >= 1 and <= 500, Repetitions: >= 1 and <= 5, Concurrency: >= 1 and <= 8 }, "budget needs max_model_calls 1-500, repetitions 1-5, concurrency 1-8");
        foreach (var example in Calibration ?? [])
        {
            Require(example.Expect is "high" or "low", $"calibration {example.Id}: expect must be high or low");
            Require(example.Metrics.Count > 0 && example.Metrics.All(m => Metrics?.Contains(m) == true), $"calibration {example.Id}: metrics must be configured metrics");
            Require(!string.IsNullOrWhiteSpace(example.Question) && !string.IsNullOrWhiteSpace(example.Answer), $"calibration {example.Id}: question and answer are required");
        }

        Require(Calibration is null || Calibration.Select(e => e.Id).Distinct().Count() == Calibration.Count, "calibration ids must be unique");
        return problems;
    }

    /// <summary>
    /// The most model calls the configured lane can make: every backend turn at its cap, and one judge call per metric
    /// per sample. The command refuses a configuration whose worst case is over the budget; the lane also admits
    /// samples against the budget one by one, so the cap holds even if this estimate were wrong.
    /// </summary>
    public int WorstCaseCalls() =>
        (Backends.Names.Count * Cases.Count * Budget.Repetitions * (Backends.MaxModelCallsPerTurn + Metrics.Count))
        + Calibration.Sum(example => example.Metrics.Count);
}

internal sealed record JudgeSettings(string Provider, string Model, int MaxOutputTokens);

internal sealed record BackendSettings(IReadOnlyList<string> Names, string Model, int MaxModelCallsPerTurn);

internal sealed record QualityCaseRef(string Dataset, string CaseId);

internal sealed record BudgetSettings(int MaxModelCalls, int Repetitions, int Concurrency);

/// <summary>A fixed answer judged against fixed context, to check the judge separates good from bad (and the stretch).</summary>
internal sealed record CalibrationExample(
    string Id,
    string Purpose,
    string Question,
    IReadOnlyList<string> Context,
    string Answer,
    string Expect,
    IReadOnlyList<string> Metrics);
