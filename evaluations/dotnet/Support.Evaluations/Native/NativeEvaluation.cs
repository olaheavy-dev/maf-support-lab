using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;

namespace Support.Evaluations.Native;

/// <summary>
/// Native .NET evaluation with Microsoft Agent Framework (L30). Two commands:
/// <list type="bullet">
/// <item><c>native record</c>: MAF runs the fake support agent on each case's query (<c>agent.EvaluateAsync(queries, …)</c>),
/// scores it, and saves each conversation as a capture in evaluations/recordings.</item>
/// <item><c>native evaluate</c>: scores the saved captures with MAF's recorded-response overload
/// (<c>agent.EvaluateAsync(responses, queries, …)</c>), which never re-runs the agent; the run fails if the model was called.</item>
/// </list>
/// Both use the same <see cref="SupportChecks"/> through MAF's <see cref="LocalEvaluator"/>, and write run documents
/// (evaluation-run schema) with the dataset and scorer versions and a link to each case's evidence.
/// </summary>
internal static class NativeEvaluation
{
    public static async Task<int> MainAsync(string[] args)
    {
        var command = args.FirstOrDefault();
        string? recordings = null, output = null;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--recordings" when i + 1 < args.Length: recordings = args[++i]; break;
                case "--out" when i + 1 < args.Length: output = args[++i]; break;
                default: throw new ArgumentException($"Unknown or incomplete option {args[i]}.");
            }
        }

        recordings ??= Captures.Directory;
        output ??= Path.Combine(RepositoryRoot.Find(), "evaluations", "results");
        return command switch
        {
            "record" => await RecordAsync(recordings, output, CancellationToken.None).ConfigureAwait(false),
            "evaluate" => await EvaluateRecordedAsync(recordings, output, CancellationToken.None).ConfigureAwait(false),
            _ => throw new ArgumentException("Usage: native record|evaluate [--recordings DIR] [--out DIR]"),
        };
    }

    /// <summary>Every case with expected tool calls: the native suite, with the dataset it belongs to.</summary>
    internal static IReadOnlyList<(Dataset Dataset, EvaluationCase Case)> Suite(IEnumerable<Dataset>? datasets = null) =>
        [.. (datasets ?? DatasetLoader.AllPaths().Select(DatasetLoader.Load))
            .SelectMany(d => d.Cases.Where(c => c.ExpectedToolCalls is not null).Select(c => (d, c)))];

    internal static async Task<int> RecordAsync(string recordings, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(recordings);
        Directory.CreateDirectory(output);
        foreach (var group in Suite().GroupBy(entry => entry.Case.Principal, StringComparer.Ordinal))
        {
            var fake = new FakeSupportAgent(group.Key);
            var entries = group.ToList();
            // MAF runs the agent on each query and scores the response (the fake-agent run).
            var results = await fake.Agent.EvaluateAsync(
                entries.Select(e => Query(e.Case)),
                SupportChecks.Evaluator(),
                evalName: "native-record",
                expectedToolCalls: entries.Select(e => Captures.ExpectedToolCalls(e.Case)),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            for (var i = 0; i < entries.Count; i++)
            {
                var item = results.InputItems![i];
                var (_, responseMessages) = item.Split();
                var capture = Captures.FromConversation(entries[i].Dataset, entries[i].Case, item.Query, responseMessages, fake.Tools);
                await File.WriteAllTextAsync(Path.Combine(recordings, $"{capture["capture_id"]}.json"), capture.ToJsonString(EvaluationJson.Options) + "\n", cancellationToken).ConfigureAwait(false);
            }

            await WriteRunsAsync(entries, results, "fake", output, cancellationToken).ConfigureAwait(false);
        }

        // Informational scores only: in a live run MAF returns an approval-required write as a ToolApprovalRequestContent,
        // which EvalChecks.ToolCallArgsMatch (MAF 1.28.0) doesn't count as a call. The saved captures keep it as an
        // approval_request, and `native evaluate` scores those: that is the gate.
        return 0;
    }

    internal static async Task<int> EvaluateRecordedAsync(string recordings, string output, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(output);
        var suite = Suite();
        var captures = Directory.GetFiles(recordings, "*.json").Order(StringComparer.Ordinal).Select(Captures.Load).ToList();
        List<((Dataset Dataset, EvaluationCase Case) Entry, JsonObject Capture)> paired = [];
        foreach (var capture in captures)
        {
            var entry = suite.FirstOrDefault(e => e.Dataset.DatasetId == capture["dataset_id"]!.GetValue<string>() && e.Case.CaseId == capture["case_id"]!.GetValue<string>());
            if (entry.Case is null)
            {
                throw new InvalidDataException($"{capture["capture_id"]} names a case that is not in the native suite.");
            }

            // A recording made against an older dataset version is stale: re-record rather than score it silently.
            if (entry.Dataset.DatasetVersion != capture["dataset_version"]!.GetValue<string>())
            {
                throw new InvalidDataException($"{capture["capture_id"]} was recorded for {entry.Dataset.DatasetId} {capture["dataset_version"]}, not {entry.Dataset.DatasetVersion}: run native record.");
            }

            paired.Add((entry, capture));
        }

        var failed = false;
        foreach (var group in paired.GroupBy(p => p.Entry.Case.Principal, StringComparer.Ordinal))
        {
            var fake = new FakeSupportAgent(group.Key);
            var items = group.ToList();
            var results = await fake.Agent.EvaluateAsync(
                items.Select(p => Captures.ToAgentResponse(p.Capture)),
                items.Select(p => p.Capture["query"]!.GetValue<string>()),
                SupportChecks.Evaluator(),
                evalName: "native-recorded",
                expectedToolCalls: items.Select(p => Captures.ExpectedToolCalls(p.Entry.Case)),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            // The recorded responses are the evidence: scoring them must never regenerate a response.
            if (fake.Model.Calls != 0)
            {
                throw new InvalidOperationException($"The recorded-response evaluation called the model {fake.Model.Calls} time(s).");
            }

            failed |= !results.AllPassed;
            await WriteRunsAsync([.. items.Select(p => p.Entry)], results, "recorded", output, cancellationToken).ConfigureAwait(false);
        }

        return failed ? 1 : 0;
    }

    internal static string Query(EvaluationCase item) => item.Steps.First(step => step.Action == "send").Message!;

    /// <summary>One run document per dataset, in the shared result schema, with checks per case and its evidence.</summary>
    private static async Task WriteRunsAsync(IReadOnlyList<(Dataset Dataset, EvaluationCase Case)> entries, AgentEvaluationResults results, string mode, string output, CancellationToken cancellationToken)
    {
        foreach (var dataset in entries.Select((e, i) => (e, i)).GroupBy(x => x.e.Dataset.DatasetId, StringComparer.Ordinal))
        {
            var file = Path.Combine(output, $"native-{mode}-{dataset.Key}-{dataset.First().e.Case.Principal.ToLowerInvariant()}.json");
            var run = Run(dataset.First().e.Dataset, mode, dataset.Select(x => (x.e.Case, results.Items[x.i])));
            await File.WriteAllTextAsync(file, run.ToJsonString(EvaluationJson.Options), cancellationToken).ConfigureAwait(false);
            var summary = run["summary"]!;
            Console.WriteLine($"native {mode} {dataset.Key}: {summary["passed"]} passed, {summary["failed"]} failed -> {file}");
            foreach (var failed in run["results"]!.AsArray().Where(r => r!["status"]!.GetValue<string>() == "failed"))
            {
                var reasons = failed!["checks"]!.AsArray().Where(c => !c!["passed"]!.GetValue<bool>()).Select(c => $"{c!["name"]}: {c["actual"]}");
                Console.WriteLine($"  failed {failed["case_id"]}: {string.Join("; ", reasons)}");
            }
        }
    }

    internal static JsonObject Run(Dataset dataset, string mode, IEnumerable<(EvaluationCase Case, EvaluationResult Result)> scored)
    {
        JsonArray cases = [];
        foreach (var (item, result) in scored)
        {
            var checks = new JsonArray([.. result.Metrics.Values.OrderBy(m => m.Name, StringComparer.Ordinal).Select(metric => (JsonNode?)new JsonObject
            {
                ["name"] = metric.Name,
                ["passed"] = metric.Interpretation?.Failed != true,
                ["actual"] = metric.Reason,
            })]);
            cases.Add(new JsonObject
            {
                ["case_id"] = item.CaseId,
                ["split"] = item.Split,
                ["status"] = checks.All(c => c!["passed"]!.GetValue<bool>()) ? "passed" : "failed",
                ["latency_ms"] = 0,
                ["steps"] = new JsonArray(),
                ["checks"] = checks,
                ["evidence"] = $"evaluations/recordings/{dataset.DatasetId}-{item.CaseId}.json",
                ["usage"] = new JsonObject { ["available"] = false },
            });
        }

        int Count(string status) => cases.Count(c => c!["status"]!.GetValue<string>() == status);
        var now = DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var run = new JsonObject
        {
            ["schema_version"] = "1.2",
            ["run"] = new JsonObject
            {
                ["run_id"] = "run_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["started_at"] = now,
                ["finished_at"] = now,
                ["backend"] = "dotnet",
                ["source_commit"] = "unknown",
                ["model"] = new JsonObject { ["mode"] = mode, ["model_id"] = FakeSupportAgent.Name },
                ["dataset"] = new JsonObject
                {
                    ["dataset_id"] = dataset.DatasetId,
                    ["dataset_version"] = dataset.DatasetVersion,
                    ["splits"] = new JsonArray([.. cases.Select(c => c!["split"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(s => (JsonNode?)s)]),
                },
                ["scorer"] = new JsonObject { ["name"] = SupportChecks.ScorerName, ["version"] = SupportChecks.ScorerVersion },
                ["runner"] = new JsonObject { ["name"] = EvaluationRun.RunnerName, ["version"] = EvaluationRun.RunnerVersion },
                ["configuration"] = new JsonObject { ["mode"] = $"native-{mode}", ["evaluator"] = "LocalEvaluator", ["judge"] = "none" },
            },
            ["results"] = cases,
            ["summary"] = new JsonObject { ["total"] = cases.Count, ["passed"] = Count("passed"), ["failed"] = Count("failed"), ["error"] = 0, ["skipped"] = 0 },
        };
        var problems = EvaluationSchemas.Errors(EvaluationSchemas.Run, run);
        return problems.Count == 0 ? run : throw new InvalidOperationException($"The native run document breaks its schema: {problems[0]}");
    }
}
