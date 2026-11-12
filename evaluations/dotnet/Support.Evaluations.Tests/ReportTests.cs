using System.Text.Json.Nodes;
using Microsoft.Extensions.AI.Evaluation;
using Microsoft.Extensions.AI.Evaluation.Reporting;
using Microsoft.Extensions.AI.Evaluation.Reporting.Formats.Html;
using Microsoft.Extensions.AI.Evaluation.Reporting.Storage;
using Support.Evaluations.Native;
using Support.Evaluations.Quality;
using Support.Evaluations.Reporting;

namespace Support.Evaluations.Tests;

/// <summary>
/// The report (L33): result documents map into Microsoft's reporting store without re-scoring or calling a model, and
/// the report keeps what matters: failures stay failures, errors and skips are not scores, provenance, run ids,
/// versions, unknown usage and estimates are visible, and judge calls show as fresh, never cached.
/// </summary>
public sealed class ReportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Recorded_runs_become_a_readable_report_without_any_model_call()
    {
        var results = Directory.CreateTempSubdirectory("report-results-");
        var store = Directory.CreateTempSubdirectory("report-store-");
        try
        {
            Assert.Equal(0, await NativeEvaluation.EvaluateRecordedAsync(Captures.Directory, results.FullName, None)); // fails on any model call
            var (scenarios, _) = await ReportCommand.LoadAsync(results.FullName, "test-exec", None);

            Assert.NotEmpty(scenarios);
            Assert.All(scenarios, s =>
            {
                Assert.Equal("test-exec", s.ExecutionName);
                Assert.Null(s.ChatDetails); // the evaluation made no model call
                Assert.NotEmpty(s.EvaluationResult.Metrics); // every native check is carried across
                Assert.All(s.EvaluationResult.Metrics.Values, m => Assert.StartsWith("evaluations/recordings/", m.Metadata!["evidence"], StringComparison.Ordinal));
                Assert.Contains(s.Tags!, t => t.StartsWith("run:run_", StringComparison.Ordinal));
                Assert.Contains("provenance:recorded responses (replayed, no model call)", s.Tags!);
                Assert.Contains("usage:unknown", s.Tags!);
                Assert.Contains(s.Tags!, t => t.StartsWith("dataset:support-", StringComparison.Ordinal) && t.Contains('@', StringComparison.Ordinal));
                Assert.Contains(s.Tags!, t => t.StartsWith("scorer:maf-local-evaluator@", StringComparison.Ordinal));
            });

            await new DiskBasedResultStore(store.FullName).WriteResultsAsync(scenarios, None);
            var html = Path.Combine(store.FullName, "report.html");
            await new HtmlReportWriter(html).WriteReportAsync(scenarios, None);
            var text = await File.ReadAllTextAsync(html, None);
            Assert.Contains("maf-local-evaluator.dotnet.recorded.support-success.invoice", text, StringComparison.Ordinal);
        }
        finally
        {
            results.Delete(recursive: true);
            store.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_failed_check_stays_failed_and_errors_and_skips_are_never_scores()
    {
        var document = await RecordedRunAsync();
        var results = document["results"]!.AsArray();
        var failed = results[0]!;
        failed["status"] = "failed";
        failed["checks"]![0]!["passed"] = false; // native checks sit on the case
        var errored = results[1]!;
        errored["status"] = "error";
        errored["error"] = "TimeoutException: the case could not be run.";
        errored["checks"] = new JsonArray();
        var skipped = results[2]!;
        skipped["status"] = "skipped";
        skipped["skip_reason"] = "split 'heldout' not selected";
        skipped["checks"] = new JsonArray();

        var scenarios = ReportAdapter.FromRun(document, "exec", (_, _) => null);

        var failedMetric = scenarios[0].EvaluationResult.Metrics.Values.First();
        Assert.True(failedMetric.Interpretation!.Failed);
        Assert.Equal(EvaluationRating.Unacceptable, failedMetric.Interpretation.Rating);
        Assert.Contains("status:failed", scenarios[0].Tags!);

        var error = Assert.Single(scenarios[1].EvaluationResult.Metrics.Values);
        Assert.True(error.Interpretation!.Failed);
        Assert.Contains(error.Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Error && d.Message.Contains("TimeoutException", StringComparison.Ordinal));
        Assert.Null(((BooleanMetric)error).Value);

        var skip = Assert.Single(scenarios[2].EvaluationResult.Metrics.Values);
        Assert.False(skip.Interpretation!.Failed);
        Assert.Equal(EvaluationRating.Inconclusive, skip.Interpretation.Rating);
        Assert.Null(((BooleanMetric)skip).Value);
        Assert.Contains("recorded conversation", scenarios[0].ModelResponse.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Quality_samples_show_judge_calls_as_fresh_turns_and_keep_errors_context_and_estimates()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath()) with
        {
            Backends = new BackendSettings(["dotnet"], "claude-haiku-5-5", 4),
            Cases = [new QualityCaseRef("support-success", "order-status")],
            Calibration = [],
        };
        var plan = QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]);
        var judge = new FakeJudge(prompt => prompt.Contains("ORD-1002 = {", StringComparison.Ordinal) ? throw new HttpRequestException("down") : 4);
        var document = await QualityLane.RunAsync(config, plan, new FakeReplies(), new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, None);

        var scenario = Assert.Single(ReportAdapter.FromQuality(document, "exec"));

        Assert.Equal("quality.dotnet.order-status", scenario.ScenarioName);
        Assert.Contains("cache:not-used", scenario.Tags!);
        Assert.Contains("estimate:backend-calls-counted-at-cap", scenario.Tags!);
        Assert.Contains("judge:claude-haiku-5-5", scenario.Tags!);
        var relevance = (NumericMetric)scenario.EvaluationResult.Metrics["Relevance"];
        Assert.Equal(4, relevance.Value);
        var groundedness = (NumericMetric)scenario.EvaluationResult.Metrics["Groundedness"];
        Assert.Null(groundedness.Value); // the judge failed: an error, not a score
        Assert.True(groundedness.Interpretation!.Failed);
        Assert.Contains(groundedness.Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Error);
        Assert.NotNull(groundedness.Context); // the grounding context the judge was given is in the report
        var turn = Assert.Single(scenario.ChatDetails!.TurnDetails); // one judge call answered; it was not cached
        Assert.False(turn.CacheHit);
        Assert.Equal("fake-judge", turn.Model);
    }

    [Fact]
    public async Task Failed_and_inconclusive_calibration_comparisons_are_failed_scenarios_in_the_report()
    {
        var config = QualityConfig.Load(QualityConfig.DefaultPath());
        var calibrationOnly = QualityLane.Plan(config, [.. DatasetLoader.AllPaths().Select(DatasetLoader.Load)]).Where(s => s.Kind == "calibration").ToList();
        // Every answer scores 3, except the contradicting one, whose judge call fails: one comparison inconclusive, two failed.
        var judge = new FakeJudge(prompt => prompt.Contains("refunded", StringComparison.Ordinal) ? throw new HttpRequestException("down") : 3);
        var document = await QualityLane.RunAsync(config, calibrationOnly, new FakeReplies(), new BudgetedJudgeClient(judge), GroundingContext.Load(), "unknown", TimeProvider.System, None);

        var checks = ReportAdapter.FromQuality(document, "exec").Where(s => s.ScenarioName.StartsWith("quality.calibration-check.", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, checks.Count);
        Assert.All(checks, s => Assert.True(Assert.Single(s.EvaluationResult.Metrics.Values).Interpretation!.Failed));
        var inconclusive = checks.Single(s => s.ScenarioName.EndsWith("good-vs-ungrounded", StringComparison.Ordinal));
        Assert.Null(((BooleanMetric)inconclusive.EvaluationResult.Metrics.Values.Single()).Value);
        Assert.Contains("status:inconclusive", inconclusive.Tags!);
        var failed = checks.Single(s => s.ScenarioName.EndsWith("good-vs-off-topic", StringComparison.Ordinal));
        Assert.False(((BooleanMetric)failed.EvaluationResult.Metrics.Values.Single()).Value);
        Assert.Contains(failed.EvaluationResult.Metrics.Values.Single().Diagnostics!, d => d.Severity == EvaluationDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_broken_result_document_or_two_results_for_one_scenario_stop_the_report()
    {
        var folder = Directory.CreateTempSubdirectory("report-broken-");
        try
        {
            var document = await RecordedRunAsync();
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "a.json"), document.ToJsonString(), None);
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "notes.json"), """{"hello": "world"}""", None); // not a result: left out
            var (scenarios, read) = await ReportCommand.LoadAsync(folder.FullName, "exec", None);
            Assert.Contains(read, line => line.StartsWith("notes.json: not a result document", StringComparison.Ordinal));

            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "b.json"), document.ToJsonString(), None); // the same run twice
            await Assert.ThrowsAsync<InvalidDataException>(() => ReportCommand.LoadAsync(folder.FullName, "exec", None));

            File.Delete(Path.Combine(folder.FullName, "b.json"));
            document["run"]!["backend"] = "ruby";
            await File.WriteAllTextAsync(Path.Combine(folder.FullName, "a.json"), document.ToJsonString(), None);
            Assert.Equal(2, await Cli.MainAsync(["report", "--results", folder.FullName, "--store", Path.Combine(folder.FullName, "store")]));
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Any_failed_or_errored_case_fails_the_run_but_skips_alone_do_not()
    {
        static JsonObject Run(int failed, int error, int skipped) => new() { ["summary"] = new JsonObject { ["failed"] = failed, ["error"] = error, ["skipped"] = skipped } };

        Assert.Equal(0, Cli.ExitCodeFor([Run(0, 0, 0), Run(0, 0, 3)]));
        Assert.Equal(1, Cli.ExitCodeFor([Run(0, 0, 0), Run(1, 0, 0)]));
        Assert.Equal(1, Cli.ExitCodeFor([Run(0, 1, 0)]));
    }

    /// <summary>A real, schema-valid run document: the native evaluation of the committed recordings (no model call).</summary>
    private static async Task<JsonObject> RecordedRunAsync()
    {
        var folder = Directory.CreateTempSubdirectory("report-run-");
        try
        {
            await NativeEvaluation.EvaluateRecordedAsync(Captures.Directory, folder.FullName, None);
            var file = Directory.GetFiles(folder.FullName, "native-recorded-support-success-cust-1001.json").Single();
            return JsonNode.Parse(await File.ReadAllTextAsync(file, None))!.AsObject();
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
