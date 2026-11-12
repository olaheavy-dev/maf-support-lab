using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Evaluation;
using Support.Evaluations.Quality;

namespace Support.Evaluations.Tests;

/// <summary>
/// The model-judged quality lane (L32) with a fake judge and fake backends: no model, no credentials. These prove the
/// orchestration around the real Microsoft evaluators: the budget is never exceeded, a judge error, malformed judge
/// output, missing context, or a failed backend turn is an error and never a score, concurrency stays bounded, and
/// cancellation stops new work.
/// </summary>
public sealed class QualityTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_committed_lane_scores_every_sample_within_budget_and_records_what_affects_a_score()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath());
        var judge = new FakeJudge(GroundedScore);
        var replies = new FakeReplies();

        var document = await RunAsync(config, judge, replies);

        var run = document["run"]!;
        var summary = document["summary"]!;
        Assert.Equal(0, summary["error"]!.GetValue<int>());
        Assert.Equal(0, summary["skipped"]!.GetValue<int>());
        Assert.Equal(12, summary["scored"]!.GetValue<int>()); // 2 backends x 4 cases + 4 calibration answers
        Assert.Equal(8, replies.Asked);
        Assert.Equal(21, judge.Calls); // 8 replies x 2 metrics + 5 calibration metrics: one judge call per metric
        Assert.Equal(judge.Calls, run["budget"]!["judge_calls"]!.GetValue<int>());
        Assert.True(run["budget"]!["reserved_calls"]!.GetValue<int>() <= config.Budget.MaxModelCalls);
        Assert.Equal("claude-haiku-5-5", run["judge"]!["model"]!.GetValue<string>());
        Assert.Equal("10.11.0", run["packages"]!["Microsoft.Extensions.AI.Evaluation.Quality"]!.GetValue<string>());
        Assert.True(run["judge_usage"]!["available"]!.GetValue<bool>());
        Assert.Equal(21 * 100, run["judge_usage"]!["input_tokens"]!.GetValue<long>());
        Assert.Contains("Single sample", run["uncertainty"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.All(Samples(document, "backend"), sample => Assert.Contains(" = ", sample["context"]!.GetValue<string>(), StringComparison.Ordinal));
        Assert.All(summary["calibration"]!.AsArray(), check => Assert.Equal("passed", check!["result"]!.GetValue<string>()));
        Assert.Equal(3, summary["calibration"]!.AsArray().Count); // groundedness x2 (ungrounded, changed context), relevance x1
    }

    [Fact]
    public void The_committed_configuration_stays_within_its_budget_and_only_samples_read_only_single_question_cases()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath());
        var plan = QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]);

        Assert.Equal(53, config.WorstCaseCalls());
        Assert.True(config.WorstCaseCalls() <= config.Budget.MaxModelCalls);
        Assert.Equal(12, plan.Count);
        var grounding = GroundingContext.Load();
        Assert.All(plan, sample => Assert.NotEmpty(grounding.Resolve(sample.ContextRefs)));
    }

    [Fact]
    public async Task A_small_budget_admits_samples_in_plan_order_and_records_the_rest_as_skipped_for_budget()
    {
        var config = Small(budget: 13); // each backend sample needs 4 (turn cap) + 2 (metrics) = 6
        var judge = new FakeJudge(_ => 4);
        var replies = new FakeReplies();

        var document = await RunAsync(config, judge, replies);

        var samples = Samples(document, "backend");
        Assert.Equal(["scored", "scored", "skipped", "skipped"], samples.Select(s => s["status"]!.GetValue<string>()));
        Assert.All(samples.Skip(2), s =>
        {
            Assert.Equal("budget", s["skip_reason"]!.GetValue<string>());
            Assert.All(s["metrics"]!.AsArray(), m => Assert.Null(m!["score"]));
        });
        Assert.Equal(2, replies.Asked);
        Assert.Equal(4, judge.Calls);
        var used = (replies.Asked * config.Backends.MaxModelCallsPerTurn) + judge.Calls;
        Assert.True(used <= config.Budget.MaxModelCalls, $"{used} calls over a budget of {config.Budget.MaxModelCalls}");
        Assert.Equal(12, document["run"]!["budget"]!["reserved_calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_judge_error_is_recorded_as_an_error_never_as_a_score()
    {
        var judge = new FakeJudge(_ => throw new HttpRequestException("judge down: secret request text"));

        var document = await RunAsync(Small(), judge, new FakeReplies());

        Assert.Equal(0, document["summary"]!["scored"]!.GetValue<int>());
        Assert.All(Samples(document, "backend"), sample =>
        {
            Assert.Equal("error", sample["status"]!.GetValue<string>());
            Assert.All(sample["metrics"]!.AsArray(), metric =>
            {
                Assert.Equal("error", metric!["status"]!.GetValue<string>());
                Assert.Null(metric["score"]);
                Assert.DoesNotContain("secret", metric["error"]!.GetValue<string>(), StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public async Task Malformed_judge_output_is_an_error_not_a_score()
    {
        var judge = new FakeJudge(_ => 4, reply: _ => "I think it's pretty good overall.");

        var document = await RunAsync(Small(), judge, new FakeReplies());

        Assert.All(Samples(document, "backend").SelectMany(s => s["metrics"]!.AsArray()), metric =>
        {
            Assert.Equal("error", metric!["status"]!.GetValue<string>());
            Assert.Null(metric["score"]);
        });
    }

    [Fact]
    public async Task Missing_or_unknown_grounding_context_makes_groundedness_an_error_while_relevance_is_still_scored()
    {
        var config = Small();
        var judge = new FakeJudge(_ => 4);
        QualitySample Sample(string id, IReadOnlyList<string> context) =>
            new(id, "backend", "dotnet", "support-success", "order-status", null, null, null, 1, "CUST-1001", "Where is my order ORD-1002?", context, null);

        var document = await QualityLane.RunAsync(config, [Sample("none", []), Sample("unknown", ["orders:ORD-0000"])], new FakeReplies(), new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, None);

        foreach (var sample in Samples(document, "backend"))
        {
            Assert.Equal("error", sample["status"]!.GetValue<string>());
            Assert.NotNull(sample["context_error"]);
            Assert.Equal("scored", Metric(sample, "relevance")["status"]!.GetValue<string>());
            var groundedness = Metric(sample, "groundedness");
            Assert.Equal("error", groundedness["status"]!.GetValue<string>());
            Assert.Null(groundedness["score"]);
        }

        Assert.Contains("unknown fixture reference 'orders:ORD-0000'", Samples(document, "backend")[1]["context_error"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(2, judge.Calls); // the evaluator refuses groundedness without context before calling the judge
    }

    [Fact]
    public async Task A_backend_turn_that_did_not_complete_is_an_error_and_is_not_judged()
    {
        var judge = new FakeJudge(_ => 5);
        var replies = new FakeReplies(_ => new BackendReply("failed", null, "orders", "model_unavailable"));

        var document = await RunAsync(Small(), judge, replies);

        Assert.All(Samples(document, "backend"), sample =>
        {
            Assert.Equal("error", sample["status"]!.GetValue<string>());
            Assert.Contains("model_unavailable", sample["error"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Empty(sample["metrics"]!.AsArray());
        });
        Assert.Equal(0, judge.Calls);
    }

    [Fact]
    public async Task At_most_the_configured_number_of_samples_run_at_once()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath()); // concurrency 2
        var judge = new FakeJudge(GroundedScore, delay: TimeSpan.FromMilliseconds(20));
        var replies = new FakeReplies(delay: TimeSpan.FromMilliseconds(40));
        var client = new BudgetedJudgeClient(judge);

        await QualityLane.RunAsync(config, QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]), replies, client, GroundingContext.Load(), "unknown", TimeProvider.System, None);

        Assert.InRange(replies.MaxConcurrent, 1, 2);
        Assert.InRange(client.MaxConcurrent, 1, 2);
        Assert.Equal(2, replies.MaxConcurrent); // it did run in parallel
    }

    [Fact]
    public async Task Cancellation_stops_new_work_and_records_the_rest_as_cancelled()
    {
        using var cancel = new CancellationTokenSource();
        var config = Small(concurrency: 1);
        var replies = new FakeReplies(onAsk: count =>
        {
            if (count == 1)
            {
                cancel.Cancel();
            }
        });
        var judge = new FakeJudge(_ => 4);

        var document = await QualityLane.RunAsync(config, Plan(config), replies, new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, cancel.Token);

        var samples = Samples(document, "backend");
        Assert.Equal(1, replies.Asked);
        Assert.Equal(0, judge.Calls); // cancelled before its metrics were judged
        Assert.All(samples, sample => Assert.Equal("skipped", sample["status"]!.GetValue<string>()));
        Assert.All(samples, sample => Assert.Equal("cancelled", sample["skip_reason"]!.GetValue<string>()));
    }

    [Fact]
    public void A_metric_is_a_score_only_with_a_value_and_no_error_diagnostic()
    {
        var withError = new NumericMetric("Relevance", 4);
        withError.AddDiagnostics(EvaluationDiagnostic.Error("the judge's reply was truncated"));
        var noValue = new NumericMetric("Relevance");
        var good = new NumericMetric("Relevance", 4);

        Assert.Equal("error", QualityScorer.Interpret("relevance", withError).Status);
        Assert.Null(QualityScorer.Interpret("relevance", withError).Score);
        Assert.Equal("error", QualityScorer.Interpret("relevance", noValue).Status);
        Assert.Equal(("scored", 4d), (QualityScorer.Interpret("relevance", good).Status, QualityScorer.Interpret("relevance", good).Score!.Value));
    }

    [Fact]
    public async Task A_judge_call_that_no_reservation_covers_is_refused()
    {
        using var client = new BudgetedJudgeClient(new FakeJudge(_ => 4));

        await Assert.ThrowsAsync<JudgeBudgetExceededException>(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: None));
        client.Grant();
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: None);
        Assert.Equal(1, client.Calls);
    }

    [Fact]
    public async Task A_calibration_pair_the_judge_cannot_separate_fails_and_an_unscored_one_is_inconclusive()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath()) with { Backends = new BackendSettings(["dotnet"], "claude-haiku-5-5", 4) };
        var calibrationOnly = Plan(config).Where(s => s.Kind == "calibration").ToList();
        var judge = new FakeJudge(prompt => prompt.Contains("refunded", StringComparison.Ordinal) ? throw new HttpRequestException("down") : 3);

        var document = await QualityLane.RunAsync(config, calibrationOnly, new FakeReplies(), new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, None);

        var checks = document["summary"]!["calibration"]!.AsArray();
        Assert.Equal("inconclusive", checks.Single(c => c!["low"]!.GetValue<string>() == "ungrounded")!["result"]!.GetValue<string>());
        Assert.All(checks.Where(c => c!["low"]!.GetValue<string>() != "ungrounded"), c => Assert.Equal("failed", c!["result"]!.GetValue<string>()));
    }

    [Fact]
    public void Grounding_context_is_the_fixture_records_and_includes_the_policy_clock()
    {
        var text = GroundingContext.Load().Resolve(["orders:ORD-2001", "policies:refund"]);

        Assert.Contains("orders:ORD-2001 = {", text, StringComparison.Ordinal);
        Assert.Contains("\"window_days\":30", text, StringComparison.Ordinal);
        Assert.Contains("policies:reference_time = \"2026-11-01T12:00:00Z\"", text, StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => GroundingContext.Load().Resolve(["policies:nope"]));
    }

    [Fact]
    public async Task The_command_refuses_to_run_without_live_or_over_budget_before_calling_anything()
    {
        Assert.Equal(2, await Cli.MainAsync(["quality"]));

        var config = Path.GetTempFileName();
        try
        {
            var json = JsonNode.Parse(await File.ReadAllTextAsync(QualityConfig.DefaultPath(), None))!;
            json["budget"]!["max_model_calls"] = 10;
            await File.WriteAllTextAsync(config, json.ToJsonString(), None);
            Assert.Equal(2, await Cli.MainAsync(["quality", "--live", "--config", config]));
        }
        finally
        {
            File.Delete(config);
        }
    }

    /// <summary>
    /// The fake judge's rule for the committed calibration: the off-topic and contradicting answers score 1, and so does
    /// the shipped answer when the context is the other order's record (ORD-1001); everything else scores 5.
    /// </summary>
    private static int GroundedScore(string prompt) =>
        prompt.Contains("Wi-Fi from the mobile app", StringComparison.Ordinal)
        || prompt.Contains("refunded the full amount", StringComparison.Ordinal)
        || (prompt.Contains("has shipped", StringComparison.Ordinal) && prompt.Contains("\"order_id\":\"ORD-1001\"", StringComparison.Ordinal))
            ? 1
            : 5;

    private static QualityConfig Small(int budget = 60, int concurrency = 2)
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath());
        return config with
        {
            Backends = new BackendSettings(["dotnet", "python"], config.Backends.Model, 4),
            Cases = [new QualityCaseRef("support-success", "order-status"), new QualityCaseRef("support-success", "invoice")],
            Budget = new BudgetSettings(budget, 1, concurrency),
            Calibration = [],
        };
    }

    private static IReadOnlyList<QualitySample> Plan(QualityConfig config) => QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]);

    private static Task<JsonObject> RunAsync(QualityConfig config, FakeJudge judge, FakeReplies replies) =>
        QualityLane.RunAsync(config, Plan(config), replies, new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, None);

    private static List<JsonObject> Samples(JsonObject document, string kind) =>
        [.. document["samples"]!.AsArray().Select(s => s!.AsObject()).Where(s => s["kind"]!.GetValue<string>() == kind)];

    private static JsonObject Metric(JsonObject sample, string name) =>
        sample["metrics"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == name)!.AsObject();

    /// <summary>A judge that answers in the evaluators' tagged format with a score chosen from the prompt it was sent.</summary>
    private sealed class FakeJudge(Func<string, int> score, Func<string, string>? reply = null, TimeSpan? delay = null) : IChatClient
    {
        private int calls;

        public int Calls => Volatile.Read(ref calls);

        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref calls);
            if (delay is { } wait)
            {
                await Task.Delay(wait, cancellationToken);
            }

            var prompt = string.Join("\n", messages.Select(m => m.Text));
            var text = reply?.Invoke(prompt) ?? $"<S0>Let's think step by step.</S0><S1>Fake judge.</S1><S2>{score(prompt)}</S2>";
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, text)) { Usage = new UsageDetails { InputTokenCount = 100, OutputTokenCount = 20 } };
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    /// <summary>Backends that answer every question with a grounded reply (or as told), counting and timing the asks.</summary>
    private sealed class FakeReplies(Func<string, BackendReply>? answer = null, TimeSpan? delay = null, Action<int>? onAsk = null) : IReplySource
    {
        private int asked;
        private int active;
        private int maxConcurrent;

        public int Asked => Volatile.Read(ref asked);

        public int MaxConcurrent => Volatile.Read(ref maxConcurrent);

        public async Task<BackendReply> AskAsync(string backend, string customer, string message, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref asked);
            var now = Interlocked.Increment(ref active);
            lock (this)
            {
                maxConcurrent = Math.Max(maxConcurrent, now);
            }

            try
            {
                onAsk?.Invoke(count);
                if (delay is { } wait)
                {
                    await Task.Delay(wait, CancellationToken.None);
                }

                return answer?.Invoke(message) ?? new BackendReply("completed", $"Here is what I found for: {message}", "orders", null);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
