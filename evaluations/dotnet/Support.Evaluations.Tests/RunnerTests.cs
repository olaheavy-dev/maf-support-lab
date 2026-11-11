using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Support.Evaluations.Tests;

/// <summary>
/// The data-to-result flow over a fake backend (L29): cases pass on matching facts, fail naming the fact that differed,
/// are skipped when they can't run safely, and every run document matches its schema. The real backends are covered by
/// evaluations/tools/run_against_backends.py.
/// </summary>
public sealed class RunnerTests
{
    private static readonly Uri Owner = new("http://owner.test/");

    [Fact]
    public async Task A_case_passes_when_every_observed_fact_matches()
    {
        var backend = new FakeBackend(Answer("answer", "orders", ["get_order_status"], "Order ORD-1002 has shipped."));
        var run = await RunAsync(backend, Case(expect: """{"outcome": "completed", "reply_kind": "answer", "route_target": "orders", "tools": ["get_order_status"], "mentions": ["ORD-1002"]}"""));

        var result = Assert.Single(run["results"]!.AsArray())!;
        Assert.Equal("passed", result["status"]!.GetValue<string>());
        Assert.All(result["steps"]![0]!["checks"]!.AsArray(), check => Assert.True(check!["passed"]!.GetValue<bool>()));
        Assert.Equal(["get_order_status"], result["steps"]![0]!["observed"]!["tools"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(1, run["summary"]!["passed"]!.GetValue<int>());
        Assert.False(result["usage"]!["available"]!.GetValue<bool>()); // unknown, never zero
    }

    [Fact]
    public async Task A_case_fails_and_names_the_fact_that_differed()
    {
        var backend = new FakeBackend(Answer("clarification", "orders", [], "Which order?"));
        var run = await RunAsync(backend, Case(expect: """{"reply_kind": "answer", "tools": ["get_order_status"]}"""));

        var result = Assert.Single(run["results"]!.AsArray())!;
        Assert.Equal("failed", result["status"]!.GetValue<string>());
        var failed = result["steps"]![0]!["checks"]!.AsArray().Where(c => !c!["passed"]!.GetValue<bool>()).Select(c => c!["name"]!.GetValue<string>());
        Assert.Equal(["reply_kind", "tools"], failed);
    }

    [Fact]
    public async Task Leaked_data_anywhere_in_the_result_fails_the_case()
    {
        var backend = new FakeBackend(Answer("not_found", "orders", ["get_order_status"], "Not found.", extra: "Mechanical Keyboard"));
        var run = await RunAsync(backend, Case(expect: """{"reply_kind": "not_found", "must_not_mention": ["Mechanical Keyboard"]}"""));
        Assert.Equal("failed", run["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_problem_response_is_observed_by_its_status_and_code()
    {
        var backend = new FakeBackend(Problem(400, "invalid_request"));
        var run = await RunAsync(backend, Case(action: "send_raw", expect: """{"http_status": 400, "problem_code": "invalid_request"}"""));
        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task Write_cases_are_skipped_unless_the_store_is_isolated()
    {
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval"}""");
        var skipped = await RunAsync(new FakeBackend(Answer("approval_request", "orders", ["cancel_order"], "Approve?")), item);
        Assert.Equal("skipped", skipped["results"]![0]!["status"]!.GetValue<string>());
        Assert.Contains("isolated store", skipped["results"]![0]!["skip_reason"]!.GetValue<string>(), StringComparison.Ordinal);

        var backend = new FakeBackend(Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"));
        var allowed = await RunAsync(backend, item, allowWrites: true);
        Assert.Equal("passed", allowed["results"]![0]!["status"]!.GetValue<string>());
        Assert.True(allowed["run"]!["configuration"]!["allow_writes"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_case_needing_a_customer_without_a_backend_process_is_skipped()
    {
        var run = await RunAsync(new FakeBackend(), Case(principal: "CUST-1002", expect: """{"outcome": "completed"}"""));
        Assert.Contains("CUST-1002", run["results"]![0]!["skip_reason"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Heldout_cases_run_only_when_their_split_is_selected()
    {
        var run = await RunAsync(new FakeBackend(), Case(split: "heldout", expect: """{"outcome": "completed"}"""));
        Assert.Equal("skipped", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.Equal(["development"], run["run"]!["dataset"]!["splits"]!.AsArray().Select(s => s!.GetValue<string>()));
    }

    [Fact]
    public async Task An_unreachable_backend_is_a_safe_error_not_a_crash()
    {
        var backend = new FakeBackend { Unreachable = true };
        var run = await RunAsync(backend, Case(expect: """{"outcome": "completed"}"""));
        var result = run["results"]![0]!;
        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal("HttpRequestException: the case could not be run.", result["error"]!.GetValue<string>());
        Assert.Equal(1, run["summary"]!["error"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_decision_answers_the_approval_from_the_step_before_it()
    {
        var backend = new FakeBackend(
            Answer("approval_request", "orders", ["cancel_order"], "Approve?", outcome: "awaiting_approval"),
            Answer("approval_result", "orders", [], "Done.", operationStatus: "committed"));
        var item = Case(writes: true, expect: """{"outcome": "awaiting_approval", "operation_kind": "cancel_order"}""");
        var steps = new List<CaseStep>(item.Steps)
        {
            new("decide", null, "approve", null, null, new Expectation(null, null, "completed", null, null, null, null, "approval_result", null, "committed", null, null, null, null, null, null)),
        };
        var run = await RunAsync(backend, item with { Steps = steps }, allowWrites: true);

        Assert.Equal("passed", run["results"]![0]!["status"]!.GetValue<string>());
        Assert.EndsWith("/approvals/appr_0123456789abcdef", backend.Paths[^1], StringComparison.Ordinal);
    }

    private static async Task<JsonObject> RunAsync(FakeBackend backend, EvaluationCase item, bool allowWrites = false)
    {
        using var client = new HttpClient(backend) { BaseAddress = Owner };
        var options = new RunOptions("dotnet", new Dictionary<string, Uri> { ["CUST-1001"] = Owner }, new HashSet<string> { "development" }, allowWrites);
        var dataset = new Dataset("1.0", "example", "1.0.0", "success", "An example.", "1.0", new LiveRun(false, 0, 1, 1), [item]);
        return await EvaluationRun.RunAsync(dataset, new CaseRunner(_ => client, options), options, new RunIdentity("unknown", "fake", null), TimeProvider.System, CancellationToken.None);
    }

    private static EvaluationCase Case(string expect, string action = "send", bool writes = false, string principal = "CUST-1001", string split = "development")
    {
        var expectation = JsonSerializer.Deserialize<Expectation>(expect, EvaluationJson.Options)!;
        var step = action == "send_raw"
            ? new CaseStep("send_raw", null, null, JsonSerializer.SerializeToElement(new { message = "no id" }), null, expectation)
            : new CaseStep("send", "Where is ORD-1002?", null, null, null, expectation);
        return new EvaluationCase("order-status", split, principal, "An example.", "new", writes, writes ? "fresh_store" : "fresh_conversation", null, [step], new StateChange("none", null));
    }

    private static Func<string> Answer(string kind, string target, string[] tools, string text, string outcome = "completed", string? extra = null, string? operationStatus = null) => () =>
    {
        var result = new JsonObject
        {
            ["contract_version"] = "1.1",
            ["turn_id"] = "turn_" + Guid.NewGuid().ToString("N")[..16],
            ["outcome"] = outcome,
            ["route"] = new JsonObject { ["status"] = "routed", ["target"] = target },
            ["reply"] = new JsonObject { ["kind"] = kind, ["text"] = text + (extra is null ? "" : $" ({extra})") },
            ["operations"] = new JsonArray(),
        };
        if (outcome == "awaiting_approval")
        {
            result["pending_approval"] = new JsonObject { ["approval_id"] = "appr_0123456789abcdef", ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order" };
            result["operations"] = new JsonArray(new JsonObject { ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order", ["status"] = "awaiting_approval" });
        }

        if (operationStatus is not null)
        {
            result["operations"] = new JsonArray(new JsonObject { ["operation_id"] = "op_0123456789abcdef", ["kind"] = "cancel_order", ["status"] = operationStatus });
        }

        StringBuilder stream = new();
        stream.Append("event: turn.started\ndata: {\"type\":\"turn.started\",\"sequence\":1}\n\n: keepalive\n\n");
        foreach (var tool in tools)
        {
            stream.Append(CultureInvariant($"event: turn.progress\ndata: {{\"type\":\"turn.progress\",\"data\":{{\"stage\":\"tool_call\",\"tool\":\"{tool}\"}}}}\n\n"));
        }

        stream.Append("event: turn.final\ndata: ").Append(new JsonObject { ["type"] = "turn.final", ["data"] = new JsonObject { ["result"] = result } }.ToJsonString()).Append("\n\n");
        return "SSE" + stream;
    };

    private static Func<string> Problem(int status, string code) => () => $"PROBLEM{status}" + new JsonObject { ["code"] = code, ["status"] = status }.ToJsonString();

    private static string CultureInvariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>Answers conversation creation, then each turn or decision with the next scripted response.</summary>
    private sealed class FakeBackend(params Func<string>[] responses) : HttpMessageHandler
    {
        private int next;

        public bool Unreachable { get; init; }

        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Unreachable)
            {
                throw new HttpRequestException("connection refused");
            }

            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path == "/api/v1/conversations")
            {
                return Task.FromResult(Respond(HttpStatusCode.Created, "application/json", """{"conversation_id":"conv_0123456789abcdef"}"""));
            }

            var body = responses.Length == 0 ? Answer("answer", "orders", [], "ok")() : responses[Math.Min(next++, responses.Length - 1)]();
            if (body.StartsWith("SSE", StringComparison.Ordinal))
            {
                return Task.FromResult(Respond(HttpStatusCode.OK, "text/event-stream", body[3..]));
            }

            var status = int.Parse(body.AsSpan(7, 3), System.Globalization.CultureInfo.InvariantCulture);
            return Task.FromResult(Respond((HttpStatusCode)status, "application/problem+json", body[10..]));
        }

        private static HttpResponseMessage Respond(HttpStatusCode status, string type, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, type) };
    }
}
