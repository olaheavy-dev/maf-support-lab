using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Support.Evaluations.Native;

namespace Support.Evaluations.Tests;

/// <summary>
/// Native MAF evaluation (L30): local checks pass and fail on real evidence, recorded responses are scored without a
/// model call, and the results keep their versions and evidence links. No judge model, no credentials.
/// </summary>
public sealed class NativeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Recorded_responses_are_scored_without_calling_the_model()
    {
        var suite = NativeEvaluation.Suite().Where(e => e.Case.Principal == "CUST-1001").ToList();
        var captures = suite.Select(e => Captures.Load(RecordingPath(e))).ToList();
        var fake = new FakeSupportAgent("CUST-1001");

        var results = await fake.Agent.EvaluateAsync(
            captures.Select(Captures.ToAgentResponse),
            captures.Select(c => c["query"]!.GetValue<string>()),
            SupportChecks.Evaluator(),
            expectedToolCalls: suite.Select(e => Captures.ExpectedToolCalls(e.Case)),
            cancellationToken: None);

        Assert.Equal(0, fake.Model.Calls);
        Assert.True(results.AllPassed, Failures(results));
        Assert.Equal(suite.Count, results.Total);
    }

    [Fact]
    public async Task The_native_evaluate_command_scores_every_committed_recording_and_writes_versioned_results()
    {
        var output = Directory.CreateTempSubdirectory("native-results-");
        try
        {
            Assert.Equal(0, await NativeEvaluation.EvaluateRecordedAsync(Captures.Directory, output.FullName, None));
            var runs = output.GetFiles("native-recorded-*.json").Select(f => JsonNode.Parse(File.ReadAllText(f.FullName))!.AsObject()).ToList();
            Assert.NotEmpty(runs);
            Assert.All(runs, run => Assert.Empty(EvaluationSchemas.Errors(EvaluationSchemas.Run, run)));
            Assert.Equal(NativeEvaluation.Suite().Count, runs.Sum(run => run["summary"]!["passed"]!.GetValue<int>()));
            Assert.All(runs, run => Assert.Equal("recorded", run["run"]!["model"]!["mode"]!.GetValue<string>()));
        }
        finally
        {
            output.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_fake_agent_run_goes_through_MAF_and_does_call_the_model()
    {
        var fake = new FakeSupportAgent("CUST-1001");
        var results = await fake.Agent.EvaluateAsync(
            ["Where is my order ORD-1002?"],
            SupportChecks.Evaluator(),
            expectedToolCalls: [[new ExpectedToolCall("get_order_status", new Dictionary<string, object> { ["orderId"] = "ORD-1002" })]],
            cancellationToken: None);

        Assert.True(fake.Model.Calls >= 2, "the model asked for the tool, then answered from its result");
        Assert.True(results.AllPassed, Failures(results));
        Assert.Contains("ORD-1002", results.InputItems![0].Response, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_deliberately_wrong_tool_argument_fails()
    {
        var capture = Captures.Load(RecordingPath("support-success", "order-status"));
        var call = capture["messages"]![0]!["contents"]![0]!;
        call["arguments"]!["orderId"] = "ORD-1003"; // the right tool, the wrong order
        Assert.Contains("tool_call_args_match", await ScoreAsync(capture, "order-status"));
    }

    [Fact]
    public async Task A_write_that_ran_without_approval_fails()
    {
        var capture = Captures.Load(RecordingPath("support-success", "cancel-approved"));
        var request = capture["messages"]![0]!["contents"]![0]!;
        request["type"] = "function_call";
        capture["messages"]!.AsArray().Add(new JsonObject
        {
            ["role"] = "tool",
            ["contents"] = new JsonArray(new JsonObject { ["type"] = "function_result", ["call_id"] = request["call_id"]!.GetValue<string>(), ["result"] = "{\"status\":\"committed\"}" }),
        });
        Assert.Contains("writes_wait_for_approval", await ScoreAsync(capture, "cancel-approved"));
    }

    [Fact]
    public async Task A_write_the_case_did_not_ask_for_fails()
    {
        var capture = Captures.Load(RecordingPath("support-success", "order-status"));
        capture["messages"]![0]!["contents"]!.AsArray().Add(new JsonObject
        {
            ["type"] = "approval_request",
            ["call_id"] = "call_refund_order",
            ["name"] = "refund_order",
            ["arguments"] = new JsonObject { ["orderId"] = "ORD-2003", ["reason"] = "injected" },
        });
        Assert.Contains("no_unexpected_writes", await ScoreAsync(capture, "order-status"));
    }

    [Fact]
    public async Task A_record_id_no_tool_returned_fails_but_a_format_example_does_not()
    {
        var leaked = Captures.Load(RecordingPath("support-success", "order-status"));
        Reply(leaked, "Order ORD-1002 is shipped. Your other order ORD-2001 is delivered.");
        Assert.Contains("record_ids_are_grounded", await ScoreAsync(leaked, "order-status"));

        var example = Captures.Load(RecordingPath("support-ambiguity", "missing-order-id"));
        Assert.Empty(await ScoreAsync(example, "missing-order-id"));
    }

    /// <summary>
    /// The stretch: one captured reply, two checks. The tool-argument check sees the call and passes; the citation check
    /// sees the reply text and fails. Each assesses only its own evidence.
    /// </summary>
    [Fact]
    public async Task A_reply_that_drops_the_record_it_found_fails_only_the_citation_check()
    {
        var capture = Captures.Load(RecordingPath("support-success", "order-status"));
        Reply(capture, "It is on its way.");
        Assert.Equal(["cites_found_record"], await ScoreAsync(capture, "order-status"));
    }

    [Fact]
    public async Task A_recording_for_an_older_dataset_version_is_refused()
    {
        var directory = Directory.CreateTempSubdirectory("stale-recordings-");
        try
        {
            var capture = Captures.Load(RecordingPath("support-success", "order-status"));
            capture["dataset_version"] = "1.0.0";
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "stale.json"), capture.ToJsonString(), None);
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvaluation.EvaluateRecordedAsync(directory.FullName, directory.FullName, None));
            Assert.Contains("native record", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void A_capture_missing_its_call_id_is_rejected()
    {
        var capture = Captures.Load(RecordingPath("support-success", "order-status"));
        capture["messages"]![0]!["contents"]![0]!.AsObject().Remove("call_id");
        Assert.NotEmpty(Captures.Problems(capture));
    }

    [Theory]
    [InlineData("order_id", "orderId")]
    [InlineData("related_order_id", "relatedOrderId")]
    [InlineData("reference", "reference")]
    public void Canonical_argument_names_map_to_the_dotnet_tools_names(string canonical, string dotnet) =>
        Assert.Equal(dotnet, Captures.DotNetName(canonical));

    [Fact]
    public void Every_native_case_has_a_current_recording_and_results_link_to_it()
    {
        foreach (var entry in NativeEvaluation.Suite())
        {
            var capture = Captures.Load(RecordingPath(entry));
            Assert.Equal(entry.Dataset.DatasetVersion, capture["dataset_version"]!.GetValue<string>());
        }

        var (dataset, item) = NativeEvaluation.Suite()[0];
        var run = NativeEvaluation.Run(dataset, "recorded", [(item, new Microsoft.Extensions.AI.Evaluation.EvaluationResult(), RecordingPath(dataset.DatasetId, item.CaseId))]);
        Assert.Equal($"evaluations/recordings/{dataset.DatasetId}-{item.CaseId}.json", run["results"]![0]!["evidence"]!.GetValue<string>());
        Assert.Equal(SupportChecks.ScorerName, run["run"]!["scorer"]!["name"]!.GetValue<string>());
        Assert.Equal(dataset.DatasetVersion, run["run"]!["dataset"]!["dataset_version"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_empty_or_incomplete_recordings_folder_is_refused_not_passed()
    {
        var directory = Directory.CreateTempSubdirectory("partial-recordings-");
        try
        {
            var empty = await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvaluation.EvaluateRecordedAsync(directory.FullName, directory.FullName, None));
            Assert.Contains("Missing:", empty.Message, StringComparison.Ordinal);

            var one = Captures.Load(RecordingPath("support-success", "order-status"));
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "a.json"), one.ToJsonString(), None);
            var partial = await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvaluation.EvaluateRecordedAsync(directory.FullName, directory.FullName, None));
            Assert.Contains("support-success/invoice", partial.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("support-success/order-status", partial.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_case_recorded_twice_is_refused()
    {
        var directory = Directory.CreateTempSubdirectory("duplicate-recordings-");
        try
        {
            foreach (var file in Directory.GetFiles(Captures.Directory, "*.json"))
            {
                File.Copy(file, Path.Combine(directory.FullName, Path.GetFileName(file)));
            }

            File.Copy(RecordingPath("support-success", "order-status"), Path.Combine(directory.FullName, "copy.json"));
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => NativeEvaluation.EvaluateRecordedAsync(directory.FullName, directory.FullName, None));
            Assert.Contains("Recorded more than once: support-success/order-status", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Results_link_to_the_recording_that_was_actually_scored()
    {
        var directory = Directory.CreateTempSubdirectory("custom-recordings-");
        try
        {
            foreach (var file in Directory.GetFiles(Captures.Directory, "*.json"))
            {
                File.Copy(file, Path.Combine(directory.FullName, "renamed-" + Path.GetFileName(file)));
            }

            Assert.Equal(0, await NativeEvaluation.EvaluateRecordedAsync(directory.FullName, directory.FullName, None));
            var links = directory.GetFiles("native-recorded-*.json")
                .SelectMany(f => JsonNode.Parse(File.ReadAllText(f.FullName))!["results"]!.AsArray())
                .Select(r => r!["evidence"]!.GetValue<string>())
                .ToList();
            Assert.NotEmpty(links);
            Assert.All(links, link => Assert.Contains("/renamed-", link, StringComparison.Ordinal));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>Scores one capture with the native checks; returns the names of the checks that failed.</summary>
    private static async Task<IReadOnlyList<string>> ScoreAsync(JsonObject capture, string caseId)
    {
        Assert.Empty(Captures.Problems(capture));
        var entry = NativeEvaluation.Suite().Single(e => e.Case.CaseId == caseId);
        var fake = new FakeSupportAgent(entry.Case.Principal);
        var results = await fake.Agent.EvaluateAsync(
            [Captures.ToAgentResponse(capture)],
            [capture["query"]!.GetValue<string>()],
            SupportChecks.Evaluator(),
            expectedToolCalls: [Captures.ExpectedToolCalls(entry.Case)],
            cancellationToken: None);
        Assert.Equal(0, fake.Model.Calls);
        return [.. results.Items[0].Metrics.Values.Where(m => m.Interpretation?.Failed == true).Select(m => m.Name).Order(StringComparer.Ordinal)];
    }

    private static void Reply(JsonObject capture, string text)
    {
        var messages = capture["messages"]!.AsArray();
        messages[^1] = new JsonObject { ["role"] = "assistant", ["contents"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) };
        capture["response_text"] = text;
    }

    private static string Failures(AgentEvaluationResults results) =>
        string.Join("; ", results.Items.SelectMany(i => i.Metrics.Values).Where(m => m.Interpretation?.Failed == true).Select(m => $"{m.Name}: {m.Reason}"));

    private static string RecordingPath((Dataset Dataset, EvaluationCase Case) entry) => RecordingPath(entry.Dataset.DatasetId, entry.Case.CaseId);

    private static string RecordingPath(string dataset, string caseId) => Path.Combine(Captures.Directory, $"{dataset}-{caseId}.json");
}
