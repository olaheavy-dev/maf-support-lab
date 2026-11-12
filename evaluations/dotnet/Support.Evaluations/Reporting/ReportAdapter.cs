using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;
using Microsoft.Extensions.AI.Evaluation.Reporting;

namespace Support.Evaluations.Reporting;

/// <summary>
/// Maps this repository's result documents into Microsoft.Extensions.AI.Evaluation.Reporting's
/// <see cref="ScenarioRunResult"/> (L33), so the <c>aieval</c> tool can render them. It is an adapter, not an
/// evaluator: nothing is re-scored and no model is called. The documents are already scored by the deterministic runner,
/// the native evaluators (recorded responses), or the quality lane; the adapter only carries their verdicts across.
/// <list type="bullet">
/// <item>An evaluation-run case becomes a scenario <c>{scorer}.{backend}.{model mode}.{dataset}.{case}</c>. Each check is a boolean
/// metric (failed when the check failed); an errored case gets an error diagnostic, a skipped one an informational one.
/// The "response" is the observed API facts, because the run documents keep facts, not model wording.</item>
/// <item>A quality sample becomes <c>quality.{backend}.{case}</c> or <c>quality.calibration.{id}</c> with the judge's
/// numeric metrics, the grounding context, and one chat turn per judge call (model, duration, tokens; never cached).</item>
/// </list>
/// Tags carry what the report must not lose: run id, backend, dataset and scorer versions, model mode, provenance
/// (fake model, recorded, live), usage availability, and which numbers are estimates.
/// </summary>
internal static class ReportAdapter
{
    public static IReadOnlyList<ScenarioRunResult> FromRun(JsonObject document, string executionName, Func<string, string, Dataset?> datasets)
    {
        var run = document["run"]!;
        var backend = Text(run["backend"]);
        var datasetId = Text(run["dataset"]!["dataset_id"]);
        var datasetVersion = Text(run["dataset"]!["dataset_version"]);
        var scorer = $"{Text(run["scorer"]!["name"])}@{Text(run["scorer"]!["version"])}";
        var mode = Text(run["model"]!["mode"]);
        var dataset = datasets(datasetId, datasetVersion);
        var created = DateTime.Parse(Text(run["finished_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        List<ScenarioRunResult> results = [];
        foreach (var result in document["results"]!.AsArray().OfType<JsonObject>())
        {
            var caseId = Text(result["case_id"]);
            var status = Text(result["status"]);
            var item = dataset?.Cases.FirstOrDefault(c => c.CaseId == caseId);
            List<EvaluationMetric> metrics = [];
            foreach (var step in result["steps"]!.AsArray().OfType<JsonObject>())
            {
                metrics.AddRange(step["checks"]!.AsArray().OfType<JsonObject>().Select(check => CheckMetric($"step {step["index"]} ", check)));
            }

            // Native evaluations (L30/L31) score the whole conversation: their checks sit on the case, not on a step.
            metrics.AddRange((result["checks"]?.AsArray() ?? []).OfType<JsonObject>().Select(check => CheckMetric("", check)));

            if (status == "error")
            {
                metrics.Add(Problem("case error", EvaluationDiagnostic.Error(Text(result["error"]) is { Length: > 0 } error ? error : "the case could not be run"), failed: true));
            }
            else if (status == "skipped")
            {
                metrics.Add(Problem("case skipped", EvaluationDiagnostic.Informational(Text(result["skip_reason"])), failed: false));
            }

            var evaluation = new EvaluationResult(metrics);
            foreach (var metric in evaluation.Metrics.Values)
            {
                metric.AddOrUpdateMetadata("usage", result["usage"]?["available"]?.GetValue<bool>() == true ? "reported" : "unknown");
                metric.AddOrUpdateMetadata("provenance", Provenance(mode));
                if (result["evidence"] is { } evidence)
                {
                    metric.AddOrUpdateMetadata("evidence", Text(evidence));
                }
            }

            results.Add(new ScenarioRunResult(
                $"{Text(run["scorer"]!["name"])}.{backend}.{mode}.{datasetId}.{caseId}",
                "1",
                executionName,
                created,
                Questions(item),
                new ChatResponse(new ChatMessage(ChatRole.Assistant, ObservedFacts(result))),
                evaluation,
                chatDetails: null, // no model was called by the evaluation itself
                tags:
                [
                    $"run:{Text(run["run_id"])}",
                    $"backend:{backend}",
                    $"dataset:{datasetId}@{datasetVersion}",
                    $"split:{Text(result["split"])}",
                    $"scorer:{scorer}",
                    $"runner:{Text(run["runner"]!["name"])}@{Text(run["runner"]!["version"])}",
                    $"model:{mode}",
                    $"provenance:{Provenance(mode)}",
                    $"usage:{(result["usage"]?["available"]?.GetValue<bool>() == true ? "reported" : "unknown")}",
                    $"status:{status}",
                    $"source-commit:{Text(run["source_commit"])}",
                ]));
        }

        return results;
    }

    public static IReadOnlyList<ScenarioRunResult> FromQuality(JsonObject document, string executionName)
    {
        var run = document["run"]!;
        var created = DateTime.Parse(Text(run["finished_at"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var judge = Text(run["judge"]!["model"]);
        var packages = run["packages"]!.AsObject().Select(p => $"package:{p.Key}@{Text(p.Value)}");
        List<ScenarioRunResult> results = [];
        foreach (var sample in document["samples"]!.AsArray().OfType<JsonObject>())
        {
            var kind = Text(sample["kind"]);
            var status = Text(sample["status"]);
            var context = sample["context"]?.GetValue<string>();
            List<EvaluationMetric> metrics = [];
            List<ChatTurnDetails> turns = [];
            foreach (var outcome in sample["metrics"]!.AsArray().OfType<JsonObject>())
            {
                var name = Text(outcome["name"]);
                var metric = new NumericMetric(name == "relevance" ? RelevanceEvaluator.RelevanceMetricName : GroundednessEvaluator.GroundednessMetricName);
                switch (Text(outcome["status"]))
                {
                    case "scored":
                        metric.Value = outcome["score"]!.GetValue<double>();
                        metric.Reason = outcome["reason"]?.GetValue<string>();
                        metric.Interpretation = new EvaluationMetricInterpretation(
                            Enum.TryParse<EvaluationRating>(outcome["rating"]?.GetValue<string>(), out var rating) ? rating : EvaluationRating.Unknown,
                            failed: outcome["failed"]?.GetValue<bool>() ?? false);
                        break;
                    case "error":
                        metric.AddDiagnostics(EvaluationDiagnostic.Error(outcome["error"]?.GetValue<string>() ?? "the judge returned no score"));
                        metric.Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Unknown, failed: true, reason: "not scored");
                        break;
                    default:
                        metric.AddDiagnostics(EvaluationDiagnostic.Informational($"skipped: {outcome["reason"]?.GetValue<string>() ?? Text(sample["skip_reason"])}"));
                        metric.Interpretation = new EvaluationMetricInterpretation(EvaluationRating.Inconclusive, failed: false, reason: "not scored");
                        break;
                }

                if (name == "groundedness" && !string.IsNullOrWhiteSpace(context))
                {
                    metric.AddOrUpdateContext(new GroundednessEvaluatorContext(context));
                }

                if (outcome["metadata"] is JsonObject metadata)
                {
                    foreach (var (key, value) in metadata)
                    {
                        metric.AddOrUpdateMetadata(key, Text(value));
                    }

                    if (Turn(metadata, judge) is { } turn)
                    {
                        turns.Add(turn);
                    }
                }

                metric.AddOrUpdateMetadata("provenance", "live, fresh (no response cache)");
                metrics.Add(metric);
            }

            if (status == "error" && metrics.Count == 0)
            {
                metrics.Add(Problem("sample error", EvaluationDiagnostic.Error(Text(sample["error"])), failed: true));
            }

            results.Add(new ScenarioRunResult(
                kind == "backend" ? $"quality.{Text(sample["backend"])}.{Text(sample["case_id"])}" : $"quality.calibration.{Text(sample["calibration_id"])}",
                Text(sample["repetition"]),
                executionName,
                created,
                [new ChatMessage(ChatRole.User, Text(sample["question"]))],
                new ChatResponse(new ChatMessage(ChatRole.Assistant, sample["answer"]?.GetValue<string>() ?? $"(no reply: {Text(sample["error"]) ?? Text(sample["skip_reason"])})")),
                new EvaluationResult(metrics),
                turns.Count > 0 ? new ChatDetails(turns) : null,
                tags:
                [
                    $"run:{Text(run["run_id"])}",
                    $"judge:{judge}",
                    $"backend-model:{Text(run["backends"]!["model"])}",
                    .. packages,
                    "provenance:live",
                    "cache:not-used",
                    $"usage:{(run["judge_usage"]!["available"]!.GetValue<bool>() ? "judge-reported" : "unknown")}",
                    "estimate:backend-calls-counted-at-cap",
                    $"repetitions:{Text(run["repetitions"])}",
                    $"status:{status}",
                    $"source-commit:{Text(run["source_commit"])}",
                    .. kind == "calibration" ? [$"calibration-expect:{Text(sample["expect"])}"] : Array.Empty<string>(),
                ]));
        }

        return results;
    }

    /// <summary>One check as a boolean metric: failed when the check failed, with what was expected and what was seen.</summary>
    private static BooleanMetric CheckMetric(string prefix, JsonObject check)
    {
        var passed = check["passed"]!.GetValue<bool>();
        var reason = check.ContainsKey("expected") ? $"expected {Json(check["expected"])}, got {Json(check["actual"])}" : Text(check["actual"]);
        return new BooleanMetric($"{prefix}{Text(check["name"])}", passed, reason)
        {
            Interpretation = new EvaluationMetricInterpretation(passed ? EvaluationRating.Good : EvaluationRating.Unacceptable, failed: !passed),
        };
    }

    /// <summary>A judge call's chat turn from the evaluator's metadata: real model, duration and tokens, never a cache hit.</summary>
    private static ChatTurnDetails? Turn(JsonObject metadata, string judge)
    {
        if (!double.TryParse(metadata["eval-duration-ms"]?.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return null;
        }

        long? Tokens(string key) => long.TryParse(metadata[key]?.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null;
        var (input, output) = (Tokens("eval-input-tokens"), Tokens("eval-output-tokens"));
        UsageDetails? usage = input is null && output is null ? null : new UsageDetails { InputTokenCount = input, OutputTokenCount = output, TotalTokenCount = Tokens("eval-total-tokens") };
        return new ChatTurnDetails(TimeSpan.FromMilliseconds(milliseconds), metadata["eval-model"]?.GetValue<string>() ?? judge, usage, cacheKey: null, cacheHit: false);
    }

    private static BooleanMetric Problem(string name, EvaluationDiagnostic diagnostic, bool failed)
    {
        var metric = new BooleanMetric(name, value: null, reason: diagnostic.Message)
        {
            Interpretation = new EvaluationMetricInterpretation(failed ? EvaluationRating.Unknown : EvaluationRating.Inconclusive, failed: failed, reason: diagnostic.Message),
        };
        metric.AddDiagnostics(diagnostic);
        return metric;
    }

    private static string Provenance(string mode) => mode switch
    {
        "fake" => "fake model (no billable calls)",
        "recorded" => "recorded responses (replayed, no model call)",
        "live" => "live model",
        _ => mode,
    };

    /// <summary>The case's inputs from its dataset (each step as the user's action), or a note if the dataset version is gone.</summary>
    private static List<ChatMessage> Questions(EvaluationCase? item)
    {
        if (item is null)
        {
            return [new ChatMessage(ChatRole.User, "(the dataset version this run used is not in the repository)")];
        }

        return [.. item.Steps.Select(step => new ChatMessage(ChatRole.User, step.Action switch
        {
            "send" => step.Message!,
            "decide" => $"[decide: {step.Decision}]",
            "send_raw" => $"[raw request: {step.RawBody?.GetRawText()}]",
            _ => $"[{step.Action.Replace('_', ' ')}]",
        }))];
    }

    /// <summary>The observed API facts per step: what happened, not model wording (run documents don't keep reply text).</summary>
    private static string ObservedFacts(JsonObject result)
    {
        StringBuilder text = new();
        foreach (var step in result["steps"]!.AsArray().OfType<JsonObject>())
        {
            var observed = step["observed"]!.AsObject();
            var facts = observed.Where(pair => pair.Value is not null && !(pair.Value is JsonArray { Count: 0 }))
                .Select(pair => $"{pair.Key}={(pair.Value is JsonValue ? Text(pair.Value) : pair.Value!.ToJsonString())}");
            text.AppendLine(CultureInfo.InvariantCulture, $"step {step["index"]} {Text(step["action"])} -> HTTP {step["http_status"]}: {string.Join(", ", facts)}");
        }

        if (text.Length == 0)
        {
            text.Append(
                Text(result["status"]) == "skipped" ? $"(not run: {Text(result["skip_reason"])})"
                : result["evidence"] is { } evidence ? $"(recorded conversation, scored as a whole: {Text(evidence)})"
                : "(no steps ran)");
        }

        return text.ToString().TrimEnd();
    }

    private static string Text(JsonNode? node) => node switch
    {
        null => "",
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        _ => node.ToJsonString(),
    };

    private static string Json(JsonNode? node) => node?.ToJsonString() ?? "null";
}
