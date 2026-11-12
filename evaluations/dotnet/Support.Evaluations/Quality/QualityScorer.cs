using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace Support.Evaluations.Quality;

/// <summary>One metric's outcome. Only <c>scored</c> carries a score; an error or skip never becomes one.</summary>
/// <param name="Metadata">The evaluator's per-call metadata (judge model, duration, tokens), as reported; L33 turns it into report chat details.</param>
internal sealed record MetricOutcome(string Name, string Status, double? Score = null, string? Rating = null, bool? Failed = null, string? Reason = null, string? Error = null, IReadOnlyDictionary<string, string>? Metadata = null)
{
    public JsonObject ToJson()
    {
        var json = new JsonObject { ["name"] = Name, ["status"] = Status };
        if (Score is { } score)
        {
            json["score"] = score;
        }

        if (Rating is not null)
        {
            json["rating"] = Rating;
        }

        if (Failed is { } failed)
        {
            json["failed"] = failed;
        }

        if (Reason is not null)
        {
            json["reason"] = Reason;
        }

        if (Error is not null)
        {
            json["error"] = Error;
        }

        if (Metadata is { Count: > 0 })
        {
            var metadata = new JsonObject();
            foreach (var (key, value) in Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                metadata[key] = value;
            }

            json["metadata"] = metadata;
        }

        return json;
    }
}

/// <summary>
/// Scores one answer with Microsoft.Extensions.AI.Evaluation.Quality's AI-judged evaluators (L32). The evaluators own
/// the rubric (their built-in prompts, 1-5 scale); this class only supplies the query, the answer, the grounding
/// context, and the judge, and turns the evaluator's metric into a status that can't be mistaken for a pass:
/// a missing value or any error diagnostic is an error.
/// </summary>
internal static class QualityScorer
{
    private const int MaxText = 400;

    public static string EvaluatorName(string metric) => metric switch
    {
        "relevance" => nameof(RelevanceEvaluator),
        "groundedness" => nameof(GroundednessEvaluator),
        _ => throw new ArgumentException($"Unknown metric {metric}."),
    };

    public static async Task<MetricOutcome> ScoreAsync(string metric, string question, string answer, string? context, IChatClient judge, CancellationToken cancellationToken)
    {
        IEvaluator evaluator = metric switch
        {
            "relevance" => new RelevanceEvaluator(),
            "groundedness" => new GroundednessEvaluator(),
            _ => throw new ArgumentException($"Unknown metric {metric}."),
        };
        var metricName = metric == "relevance" ? RelevanceEvaluator.RelevanceMetricName : GroundednessEvaluator.GroundednessMetricName;
        // Only groundedness reads context. A missing context is passed on as missing, so the evaluator's own handling
        // (an error diagnostic) is what the lane records: proof that it never turns into a score.
        EvaluationContext[] contexts = metric == "groundedness" && !string.IsNullOrWhiteSpace(context) ? [new GroundednessEvaluatorContext(context)] : [];
        try
        {
            var result = await evaluator.EvaluateAsync(
                [new ChatMessage(ChatRole.User, question)],
                new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)),
                new ChatConfiguration(judge),
                contexts,
                cancellationToken).ConfigureAwait(false);
            return Interpret(metric, result.Get<NumericMetric>(metricName));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new MetricOutcome(metric, "skipped", Reason: "cancelled");
        }
        catch (JudgeBudgetExceededException error)
        {
            return new MetricOutcome(metric, "error", Error: error.Message);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A safe description only: the judge's exception text can echo request content.
            return new MetricOutcome(metric, "error", Error: $"the judge call failed ({error.GetType().Name})");
        }
    }

    /// <summary>A metric becomes a score only with a value and no error diagnostic; anything else is an error.</summary>
    internal static MetricOutcome Interpret(string metric, NumericMetric value)
    {
        var errors = (value.Diagnostics ?? []).Where(d => d.Severity == EvaluationDiagnosticSeverity.Error).Select(d => d.Message).ToList();
        var metadata = value.Metadata is { Count: > 0 } ? new Dictionary<string, string>(value.Metadata, StringComparer.Ordinal) : null;
        if (errors.Count > 0 || value.Value is null)
        {
            return new MetricOutcome(metric, "error", Error: Trim(errors.Count > 0 ? string.Join("; ", errors) : "the judge returned no score"), Metadata: metadata);
        }

        return new MetricOutcome(
            metric,
            "scored",
            value.Value,
            value.Interpretation?.Rating.ToString(),
            value.Interpretation?.Failed,
            value.Reason is null ? null : Trim(value.Reason),
            Metadata: metadata);
    }

    private static string Trim(string text) => text.Length <= MaxText ? text : text[..MaxText] + "…";
}
