using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;

namespace Support.Evaluations.Reporting;

/// <summary>
/// <c>report --results DIR [--store DIR] [--execution NAME]</c> (L33): reads the result documents a run left in DIR
/// (evaluation-run and evaluation-quality), checks each against its schema, and writes them to a disk result store
/// under one execution name. The <c>aieval</c> tool then renders HTML or JSON from that store. Reading files only:
/// no backend, agent, or judge is started, and no credentials are read.
/// </summary>
internal static class ReportCommand
{
    public static async Task<int> MainAsync(string[] args)
    {
        string? results = null, store = null, execution = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--results": results = Value(); break;
                case "--store": store = Value(); break;
                case "--execution": execution = Value(); break;
                default: throw new ArgumentException($"Unknown option {args[i]}.");
            }
        }

        results ??= Path.Combine(RepositoryRoot.Find(), "evaluations", "results");
        store ??= Path.Combine(results, "report", "store");
        execution ??= $"local-{DateTime.UtcNow:yyyyMMddTHHmmss}";
        if (execution.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException("--execution may use letters, digits, '-', '_' and '.' only (it names a folder).");
        }

        var (scenarios, read) = await LoadAsync(results, execution, CancellationToken.None).ConfigureAwait(false);
        Directory.CreateDirectory(store);
        await new DiskBasedResultStore(store).WriteResultsAsync(scenarios, CancellationToken.None).ConfigureAwait(false);
        Console.WriteLine($"report: {scenarios.Count} scenario results from {read.Count} documents -> {store} (execution {execution})");
        foreach (var line in read)
        {
            Console.WriteLine($"  {line}");
        }

        return 0;
    }

    /// <summary>
    /// The scenario results for every result document in <paramref name="directory"/>. A document that claims a known
    /// shape but breaks its schema stops the report (exit 2): a report must not present broken evidence as results.
    /// </summary>
    public static async Task<(List<ScenarioRunResult> Scenarios, List<string> Read)> LoadAsync(string directory, string execution, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"No results folder at {directory}: run the evaluations first.");
        }

        var datasets = DatasetLoader.AllPaths().Select(DatasetLoader.Load).ToList();
        Dataset? Find(string id, string version) => datasets.FirstOrDefault(d => d.DatasetId == id && d.DatasetVersion == version);
        List<ScenarioRunResult> scenarios = [];
        List<string> read = [];
        foreach (var path in Directory.GetFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            JsonObject document;
            try
            {
                document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)) as JsonObject
                    ?? throw new InvalidDataException($"{Path.GetFileName(path)} is not a JSON object.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} is not valid JSON.");
            }

            var (schema, map) = document switch
            {
                _ when document.ContainsKey("samples") => (EvaluationSchemas.Quality, (Func<IReadOnlyList<ScenarioRunResult>>)(() => ReportAdapter.FromQuality(document, execution))),
                _ when document.ContainsKey("results") && document["run"]?["backend"] is not null => (EvaluationSchemas.Run, () => ReportAdapter.FromRun(document, execution, Find)),
                _ => (null, null),
            };
            if (schema is null || map is null)
            {
                read.Add($"{Path.GetFileName(path)}: not a result document, left out");
                continue;
            }

            var problems = EvaluationSchemas.Errors(schema, document);
            if (problems.Count > 0)
            {
                throw new InvalidDataException($"{Path.GetFileName(path)} breaks the {schema} schema:\n  {string.Join("\n  ", problems)}");
            }

            var mapped = map();
            scenarios.AddRange(mapped);
            read.Add($"{Path.GetFileName(path)}: {mapped.Count} scenarios ({schema})");
        }

        // The store keys results by scenario and iteration: a duplicate would silently replace another result.
        var duplicate = scenarios.GroupBy(s => (s.ScenarioName, s.IterationName)).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null
            ? (scenarios, read)
            : throw new InvalidDataException($"Two results map to scenario '{duplicate.Key.ScenarioName}' iteration {duplicate.Key.IterationName}: report one run per backend, mode and dataset at a time.");
    }
}
