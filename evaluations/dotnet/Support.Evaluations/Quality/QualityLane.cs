using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI.Evaluation.Quality;

namespace Support.Evaluations.Quality;

/// <summary>A backend's reply to one question: the turn's outcome and the reply text the judge reads.</summary>
internal sealed record BackendReply(string? Outcome, string? Text, string? RouteTarget, string? ErrorCode);

/// <summary>Where backend replies come from: live launched backends in the lane, a fake in tests.</summary>
internal interface IReplySource : IAsyncDisposable
{
    Task<BackendReply> AskAsync(string backend, string customer, string message, CancellationToken cancellationToken);
}

/// <summary>One thing the lane judges: a backend's live reply to a dataset case, or a fixed calibration answer.</summary>
internal sealed record QualitySample(
    string SampleId,
    string Kind,
    string? Backend,
    string? Dataset,
    string? CaseId,
    string? CalibrationId,
    string? Expect,
    string? Purpose,
    int Repetition,
    string Principal,
    string Question,
    IReadOnlyList<string> ContextRefs,
    string? FixedAnswer);

/// <summary>
/// The model-judged quality lane. It asks each configured backend the same questions (live), then judges every
/// reply with the same evaluators, judge model, and grounding context, plus fixed calibration answers. Every model
/// call, backend or judge, comes out of one budget; work is admitted in plan order until the budget is spent, at most
/// <see cref="BudgetSettings.Concurrency"/> samples run at once, and cancellation stops new work. The scores describe
/// reply quality only: authorization, approvals and exactly-once writes are proven by the deterministic lanes.
/// </summary>
internal static class QualityLane
{
    public const string ResultSchemaVersion = "1.1";

    public static IReadOnlyList<QualitySample> Plan(QualityConfig config, IReadOnlyList<Dataset> datasets)
    {
        List<QualitySample> samples = [];
        foreach (var backend in config.Backends.Names)
        {
            foreach (var reference in config.Cases)
            {
                var dataset = datasets.FirstOrDefault(d => d.DatasetId == reference.Dataset)
                    ?? throw new InvalidDataException($"quality case {reference.Dataset}/{reference.CaseId}: no such dataset");
                var item = dataset.Cases.FirstOrDefault(c => c.CaseId == reference.CaseId)
                    ?? throw new InvalidDataException($"quality case {reference.Dataset}/{reference.CaseId}: no such case");
                if (item.Writes || item.Steps.Count(step => step.Action == "send") != 1 || item.Steps[0].Action != "send")
                {
                    throw new InvalidDataException($"quality case {reference.CaseId}: only read-only cases with one question can be judged live");
                }

                for (var repetition = 1; repetition <= config.Budget.Repetitions; repetition++)
                {
                    samples.Add(new QualitySample($"{backend}/{item.CaseId}/{repetition}", "backend", backend, dataset.DatasetId, item.CaseId, null, null, item.Purpose, repetition, item.Principal, item.Steps[0].Message!, item.Context ?? [], null));
                }
            }
        }

        foreach (var example in config.Calibration)
        {
            samples.Add(new QualitySample($"calibration/{example.Id}", "calibration", null, null, null, example.Id, example.Expect, example.Purpose, 1, "", example.Question, example.Context, example.Answer));
        }

        return samples;
    }

    public static async Task<JsonObject> RunAsync(
        QualityConfig config,
        IReadOnlyList<QualitySample> plan,
        IReplySource replies,
        BudgetedJudgeClient judge,
        GroundingContext grounding,
        string sourceCommit,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var started = clock.GetUtcNow();
        var budget = new CallBudget(config.Budget.MaxModelCalls);
        var backendTurns = 0;

        // Admission, in plan order: a sample runs only if its worst case fits what's left. Deterministic, so the same
        // configuration and budget always skip the same samples.
        var admitted = plan.Select(sample =>
        {
            var metrics = sample.Kind == "calibration"
                ? config.Calibration.First(e => e.Id == sample.CalibrationId).Metrics
                : config.Metrics;
            var need = (sample.Kind == "backend" ? config.Backends.MaxModelCallsPerTurn : 0) + metrics.Count;
            return (Sample: sample, Metrics: metrics, Admitted: budget.TryReserve(need));
        }).ToList();

        using var gate = new SemaphoreSlim(config.Budget.Concurrency);
        var results = await Task.WhenAll(admitted.Select(async entry =>
        {
            if (!entry.Admitted)
            {
                return Skipped(entry.Sample, entry.Metrics, "budget");
            }

            await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return Skipped(entry.Sample, entry.Metrics, "cancelled");
                }

                if (entry.Sample.Kind == "backend")
                {
                    Interlocked.Increment(ref backendTurns);
                }

                return await RunSampleAsync(entry.Sample, entry.Metrics, replies, judge, grounding, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);

        return Document(config, results, budget, judge, backendTurns, sourceCommit, started, clock.GetUtcNow());
    }

    private static async Task<JsonObject> RunSampleAsync(QualitySample sample, IReadOnlyList<string> metrics, IReplySource replies, BudgetedJudgeClient judge, GroundingContext grounding, CancellationToken cancellationToken)
    {
        var json = SampleJson(sample);
        string? context = null;
        try
        {
            context = grounding.Resolve(sample.ContextRefs);
            json["context"] = context;
        }
        catch (InvalidDataException error)
        {
            json["context_error"] = error.Message; // groundedness will report an error, never a score
        }

        var answer = sample.FixedAnswer;
        if (sample.Kind == "backend")
        {
            try
            {
                var reply = await replies.AskAsync(sample.Backend!, sample.Principal, sample.Question, cancellationToken).ConfigureAwait(false);
                json["reply_outcome"] = reply.Outcome;
                json["route_target"] = reply.RouteTarget;
                if (reply.Outcome != "completed" || string.IsNullOrWhiteSpace(reply.Text))
                {
                    return Finish(json, "error", [], $"the backend turn ended '{reply.Outcome ?? "unknown"}'{(reply.ErrorCode is null ? "" : $" ({reply.ErrorCode})")} without a reply to judge");
                }

                answer = reply.Text;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return Finish(json, "skipped", [], skipReason: "cancelled");
            }
            catch (Exception error) when (error is HttpRequestException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException or OperationCanceledException or IOException)
            {
                return Finish(json, "error", [], $"the backend could not be asked ({error.GetType().Name})");
            }
        }

        json["answer"] = answer;
        List<MetricOutcome> outcomes = [];
        foreach (var metric in metrics)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new MetricOutcome(metric, "skipped", Reason: "cancelled"));
                continue;
            }

            judge.Grant(); // this call was reserved at admission
            outcomes.Add(await QualityScorer.ScoreAsync(metric, sample.Question, answer!, context, judge, cancellationToken).ConfigureAwait(false));
        }

        var status = outcomes.Any(o => o.Status == "error") ? "error" : outcomes.All(o => o.Status == "scored") ? "scored" : "skipped";
        return Finish(json, status, outcomes, skipReason: status == "skipped" ? "cancelled" : null);
    }

    private static JsonObject Skipped(QualitySample sample, IReadOnlyList<string> metrics, string reason) =>
        Finish(SampleJson(sample), "skipped", [.. metrics.Select(m => new MetricOutcome(m, "skipped", Reason: reason))], skipReason: reason);

    private static JsonObject SampleJson(QualitySample sample)
    {
        var json = new JsonObject
        {
            ["sample_id"] = sample.SampleId,
            ["kind"] = sample.Kind,
            ["repetition"] = sample.Repetition,
            ["question"] = sample.Question,
            ["context_refs"] = new JsonArray([.. sample.ContextRefs.Select(r => (JsonNode?)r)]),
        };
        void Add(string key, string? value)
        {
            if (value is not null)
            {
                json[key] = value;
            }
        }

        Add("backend", sample.Backend);
        Add("dataset", sample.Dataset);
        Add("case_id", sample.CaseId);
        Add("calibration_id", sample.CalibrationId);
        Add("expect", sample.Expect);
        Add("purpose", sample.Purpose);
        Add("principal", sample.Principal.Length == 0 ? null : sample.Principal);
        return json;
    }

    private static JsonObject Finish(JsonObject json, string status, IReadOnlyList<MetricOutcome> metrics, string? error = null, string? skipReason = null)
    {
        json["status"] = status;
        json["metrics"] = new JsonArray([.. metrics.Select(m => (JsonNode?)m.ToJson())]);
        if (error is not null)
        {
            json["error"] = error;
        }

        if (skipReason is not null)
        {
            json["skip_reason"] = skipReason;
        }

        return json;
    }

    private static JsonObject Document(QualityConfig config, JsonObject[] samples, CallBudget budget, BudgetedJudgeClient judge, int backendTurns, string sourceCommit, DateTimeOffset started, DateTimeOffset finished)
    {
        int Count(string status) => samples.Count(s => s["status"]!.GetValue<string>() == status);
        var document = new JsonObject
        {
            ["schema_version"] = ResultSchemaVersion,
            ["run"] = new JsonObject
            {
                ["run_id"] = "quality_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)),
                ["started_at"] = Timestamp(started),
                ["finished_at"] = Timestamp(finished),
                ["source_commit"] = sourceCommit,
                ["lane"] = "live",
                ["judge"] = new JsonObject { ["provider"] = config.Judge.Provider, ["model"] = config.Judge.Model, ["max_output_tokens"] = config.Judge.MaxOutputTokens },
                ["backends"] = new JsonObject
                {
                    ["names"] = new JsonArray([.. config.Backends.Names.Select(n => (JsonNode?)n)]),
                    ["model"] = config.Backends.Model,
                    ["max_model_calls_per_turn"] = config.Backends.MaxModelCallsPerTurn,
                },
                ["packages"] = Packages(),
                ["metrics"] = new JsonArray([.. config.Metrics.Select(m => (JsonNode?)new JsonObject
                {
                    ["name"] = m,
                    ["evaluator"] = QualityScorer.EvaluatorName(m),
                    ["scale"] = "1-5 (higher is better)",
                    ["rubric"] = "the evaluator's built-in prompt (Microsoft.Extensions.AI.Evaluation.Quality); unchanged",
                })]),
                ["response_cache"] = "not used: every backend reply and judge call in this run is fresh (a cached judge answer would hide judge variability, and the budget counts real calls)",
                ["repetitions"] = config.Budget.Repetitions,
                ["concurrency"] = config.Budget.Concurrency,
                ["budget"] = new JsonObject
                {
                    ["max_model_calls"] = budget.Limit,
                    ["reserved_calls"] = budget.Reserved,
                    ["backend_turns"] = backendTurns,
                    ["backend_calls_reserved_per_turn"] = config.Backends.MaxModelCallsPerTurn,
                    ["backend_calls_actual"] = "unknown: the support contract does not report model calls per turn, so each turn is counted at its cap",
                    ["judge_calls"] = judge.Calls,
                },
                ["judge_usage"] = judge.UsageAvailable
                    ? new JsonObject { ["available"] = true, ["calls"] = judge.Calls, ["input_tokens"] = judge.InputTokens, ["output_tokens"] = judge.OutputTokens }
                    : new JsonObject { ["available"] = false, ["calls"] = judge.Calls },
                ["uncertainty"] = config.Budget.Repetitions == 1
                    ? "Single sample: each case was answered once per backend and judged once, by one judge model at the evaluators' default settings. A one-point difference on a single sample is within judge and model noise; read the scores as a smoke signal, not a ranking of the backends."
                    : $"{config.Budget.Repetitions} repetitions per case and backend, one judge model; compare means with their spread, not single scores.",
            },
            ["samples"] = new JsonArray([.. samples.Select(s => (JsonNode?)s)]),
            ["summary"] = new JsonObject
            {
                ["samples"] = samples.Length,
                ["scored"] = Count("scored"),
                ["error"] = Count("error"),
                ["skipped"] = Count("skipped"),
                ["backends"] = BackendSummary(config, samples),
                ["calibration"] = Calibration(samples),
            },
        };

        var problems = EvaluationSchemas.Errors(EvaluationSchemas.Quality, document);
        return problems.Count == 0 ? document : throw new InvalidOperationException($"The quality document breaks its schema:\n  {string.Join("\n  ", problems)}");
    }

    private static JsonObject BackendSummary(QualityConfig config, JsonObject[] samples)
    {
        var json = new JsonObject();
        foreach (var backend in config.Backends.Names)
        {
            var metrics = new JsonObject();
            foreach (var metric in config.Metrics)
            {
                var outcomes = samples.Where(s => s["backend"]?.GetValue<string>() == backend)
                    .SelectMany(s => s["metrics"]!.AsArray()).Where(m => m!["name"]!.GetValue<string>() == metric).ToList();
                var scores = outcomes.Where(m => m!["status"]!.GetValue<string>() == "scored").Select(m => m!["score"]!.GetValue<double>()).ToList();
                metrics[metric] = new JsonObject
                {
                    ["scored"] = scores.Count,
                    ["error"] = outcomes.Count(m => m!["status"]!.GetValue<string>() == "error"),
                    ["skipped"] = outcomes.Count(m => m!["status"]!.GetValue<string>() == "skipped"),
                    ["mean"] = scores.Count == 0 ? null : Math.Round(scores.Average(), 2),
                    ["min"] = scores.Count == 0 ? null : scores.Min(),
                };
            }

            json[backend] = metrics;
        }

        return json;
    }

    /// <summary>
    /// For each calibration question and metric, every "high" answer must score above every "low" one. A check whose
    /// samples weren't all scored is inconclusive, never passed.
    /// </summary>
    private static JsonArray Calibration(JsonObject[] samples)
    {
        var calibration = samples.Where(s => s["kind"]!.GetValue<string>() == "calibration").ToList();
        JsonArray checks = [];
        foreach (var group in calibration.GroupBy(s => s["question"]!.GetValue<string>()))
        {
            var metrics = group.SelectMany(s => s["metrics"]!.AsArray()).Select(m => m!["name"]!.GetValue<string>()).Distinct();
            foreach (var metric in metrics)
            {
                double? Score(JsonObject sample) => sample["metrics"]!.AsArray().FirstOrDefault(m => m!["name"]!.GetValue<string>() == metric && m["status"]!.GetValue<string>() == "scored")?["score"]?.GetValue<double>();
                var highs = group.Where(s => s["expect"]!.GetValue<string>() == "high" && s["metrics"]!.AsArray().Any(m => m!["name"]!.GetValue<string>() == metric)).ToList();
                var lows = group.Where(s => s["expect"]!.GetValue<string>() == "low" && s["metrics"]!.AsArray().Any(m => m!["name"]!.GetValue<string>() == metric)).ToList();
                foreach (var high in highs)
                {
                    foreach (var low in lows)
                    {
                        var (h, l) = (Score(high), Score(low));
                        checks.Add(new JsonObject
                        {
                            ["metric"] = metric,
                            ["high"] = high["calibration_id"]!.GetValue<string>(),
                            ["low"] = low["calibration_id"]!.GetValue<string>(),
                            ["high_score"] = h,
                            ["low_score"] = l,
                            ["result"] = h is null || l is null ? "inconclusive" : h > l ? "passed" : "failed",
                        });
                    }
                }
            }
        }

        return checks;
    }

    private static JsonObject Packages()
    {
        static string Version(Assembly assembly) =>
            (assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "unknown").Split('+')[0];
        return new JsonObject
        {
            ["Microsoft.Extensions.AI.Evaluation.Quality"] = Version(typeof(RelevanceEvaluator).Assembly),
            ["Microsoft.Extensions.AI.Evaluation"] = Version(typeof(Microsoft.Extensions.AI.Evaluation.IEvaluator).Assembly),
            ["Anthropic"] = Version(typeof(Anthropic.AnthropicClient).Assembly),
        };
    }

    private static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
